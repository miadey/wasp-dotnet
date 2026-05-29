# M4 — Collapse a Blazor click from TWO consensus updates to ONE

**Status:** design only (no code in this doc)
**Owner area:** `Wasp.AspNetCore.Blazor.Server` transport + client shim
**Goal:** A single user click on an `@onclick` handler in an `InteractiveServer`
Blazor circuit should cost exactly **one** ~2 s IC update call, not two.

---

## 0. TL;DR

Today a click costs two ~2 s update calls:

1. `POST /_blazor?id=…` — the update call that actually runs the `@onclick`
   handler inline and **enqueues** the render-diff frames onto a per-connection
   `ConcurrentQueue<byte[]>`.
2. the next `GET /_blazor?id=…` poll — finds the queue non-empty, the gh #115
   query fast-path **bails** (`return null;`), `IcServer.Dispatch` re-issues the
   GET as a **second** ~2 s update purely to `DrainOutbound()` and hand back
   bytes that were already computed in step 1.

The handler execution in step 1 mutates state and therefore *must* run on the
replicated update path — that ~2 s is irreducible. The second update is pure
waste: it computes nothing, it only ships already-serialized bytes.

The fix is to make the event POST return the render-diff frames **inline in its
own response body**, exactly as the production `POST /_wasp/event` endpoint in
`Wasp.AspNetCore.Blazor.Wasp/src/WaspRenderEndpoints.cs` already does for the
render-as-query architecture. The blocker is that stock SignalR LongPolling
`send()` **discards the POST response body**, and the click is a fire-and-forget
`Invocation` (no `invocationId`), so any "smuggle bytes into the POST response"
hack on the SignalR-LP path races the concurrent GET poll over the same queue.
The right move is to converge the interactive circuit onto the
render-as-query transport (or, narrowly, ship a custom LP client shim that reads
the POST body) — **not** to hack stock SignalR LongPolling.

---

## 1. Current 2-update flow (verified, with file:line citations)

### 1.1 The click POST (update #1 — irreducible)

Client side, stock `blazor.web.js` SignalR LongPolling client serializes the UI
event as a BlazorPack `Invocation` frame targeting `BeginInvokeDotNetFromJS`
and `POST`s it to `/_blazor?id=<connectionId>`.

Server side:

- `LongPollingEndpoints.cs:125` — `MapPost(pattern, …)` reads the body and calls
  `conn.Transport.HandleInbound(bytes)` (`:148`).
- `IcCircuitTransportImpl.cs:67` `HandleInbound` splits frames and, for an
  `Invocation` (msgType 1), raises `InboundMessage` (`:118`).
- `CircuitHubFacade.Bind` wired `InboundMessage` to
  `BlazorHubDispatcher.Dispatch(...)` (`CircuitHubFacade.cs:132–142`).
- `BlazorHubDispatcher.cs:130` routes `BeginInvokeDotNetFromJS` to
  `hub.BeginInvokeDotNetFromJS(...)`. Note this case emits **no** Completion
  frame — it is fire-and-forget.
- `CircuitHubFacade.BeginInvokeDotNetFromJS` (`:379`) intercepts
  `DispatchEventAsync` (`:393`). Because wasm-wasi has no thread pool, it pulls
  the renderer dispatcher's private `_context`
  (`RendererSynchronizationContextDispatcher._context`) via reflection
  (`:424–433`), installs it as `SynchronizationContext.Current` (`:441`), then
  calls `renderer.DispatchEventAsync(eventHandlerId, null, EventArgs.Empty,
  waitForQuiescence: false)` (`:445`) so the `@onclick` handler runs
  **synchronously inline** on this same update call.
- The handler's `StateHasChanged` produces a render batch. The framework's
  `RemoteRenderer` ships it by calling the client proxy, which lands in
  `IcCircuitTransport.SendCoreAsync` / `SendRawFrame`
  (`IcCircuitTransportImpl.cs:168` / `:174`). The `_send` delegate for a
  LongPolling connection is `bytes => outbound!.Enqueue(bytes)`
  (`IcCircuitTransportRegistry.cs:119`) — i.e. it **enqueues** onto
  `LongPollingConnection.Outbound` (`IcCircuitTransportRegistry.cs:106`).
- Back in `LongPollingEndpoints.cs:163–170`, the POST handler **deliberately
  does not drain** the queue. The comment is explicit: "SignalR Long Polling's
  `send` helper DISCARDS the POST response body … we let the next GET poll pick
  up any bytes". It returns `200` with `ContentLength = 0`.

Cost: this POST is upgraded to update by `IcServer.Dispatch` (POST never matches
the GET-only query fast-path — see §1.3), runs the handler, ~2 s consensus.
**This update is necessary and stays.**

### 1.2 The follow-up GET poll (update #2 — pure waste)

- The client's LP receive loop issues `GET /_blazor?id=<connectionId>`.
- `IcServer.Dispatch(isUpdate:false)` runs first as a query
  (`IcServer.cs:266`, `:291`). For `/_blazor` GET it consults the gh #115
  query fast-path handler registered at
  `BlazorOnIcHostingExtensions.cs:179` (`IcServer.RegisterQueryHandler("/_blazor", …)`).
- That handler (`BlazorOnIcHostingExtensions.cs:179–233`):
  - returns an empty body when the connection is missing or
    `conn.Outbound.IsEmpty` (`:214`, `:228`) — served cheaply as a query;
  - returns **`null`** when `conn.Outbound` has data (`:232`), which tells
    `IcServer.Dispatch` to fall through to upgrade
    (`IcServer.cs:429` "Handler returned null → fall through to upgrade",
    `:465` `Reply.Bytes(... IcHttpResponse.Upgrading())`).
- The boundary re-issues the GET as `http_request_update`. The second pass runs
  `MapGet` (`LongPollingEndpoints.cs:182`), which calls `conn.DrainOutbound()`
  (`:211`, impl `IcCircuitTransportRegistry.cs:129`) and writes the bytes.

Cost: a full ~2 s update call that executes **zero handler logic** — it only
copies the queue contents (already computed in §1.1) into a response body. This
is the call we are eliminating.

### 1.3 Why the POST can't just be drained today

- `IcServer.Dispatch` only runs query handlers for `GET`/`POST` on the query
  path, and the `/_blazor` handler explicitly bails for non-GET
  (`BlazorOnIcHostingExtensions.cs:181` `if (method != "GET") return null;`).
- The POST handler at `LongPollingEndpoints.cs:163` chooses not to drain because
  the **client** (stock SignalR LP `HttpConnection.send`) throws the POST
  response body away. Even if we wrote the bytes there, `blazor.web.js` would
  never read them, and the bytes would be gone from the queue when the GET poll
  arrives — breaking the existing path with no benefit.

---

## 2. Target 1-update flow

One click = one update call:

1. Client issues the mutating request. The handler runs inline on the update
   call (unchanged from §1.1 — this is the irreducible ~2 s).
2. The **same response** carries the render-diff frames produced by that
   handler. No follow-up poll, no second update.
3. The client applies the frames it received in the response body directly.

This is structurally identical to the already-shipping render-as-query
transport (`UseInternetComputerWasp`):

> `WaspRenderEndpoints.cs:91` — `POST /_wasp/event` is **one** `canister_update`.
> It dispatches the event via `renderer.DispatchEvent(er)` (`:107`) and writes
> the resulting batch bytes inline in the same response
> (`:110–112` `EncodeBatch(batch)` → `ctx.Response.Body.WriteAsync`).
> Client side, `wasp.js:154` `await fetch('/_wasp/event', …)` then
> `:174 var batch = await resp.json(); … _applyBatch(batch)` — it **reads the
> POST response body**. No GET poll is involved in committing a local click.

The reactivity poll (`wasp.js:779 _reactivityPoll`) remains a separate, cheap
v2-cert **query** (`GET /_wasp/render`, `WaspRenderEndpoints.cs:58`) used only to
observe *other* clients' changes — it is never on the local-click critical path.

So the target is: the interactive Blazor circuit's click should behave like
`/_wasp/event` — mutate-and-return-bytes in one update — instead of
mutate-then-poll-to-fetch-bytes in two.

---

## 3. The exact seam

### 3.1 Why a SignalR-LP inline hack is wrong (the trap)

Three independent facts make "just drain the queue in the POST response" unsafe
on the stock SignalR LongPolling transport:

1. **Stock LP `send()` discards the POST body.** `@microsoft/signalr`'s
   `HttpConnection`/`LongPollingTransport.send` issues the POST and ignores its
   response body entirely; only the dedicated GET `_poll` loop feeds bytes into
   `onreceive`. This is stated verbatim at `LongPollingEndpoints.cs:163–167`.
   Writing render frames into the POST 200 would send them into a void.

2. **The click is a fire-and-forget `Invocation` with no `invocationId`.**
   `BlazorHubDispatcher`'s `BeginInvokeDotNetFromJS` case (`:130–136`) emits no
   Completion and the inbound message carries a null `invocationId`
   (`IcCircuitTransportImpl.cs:118` passes `inv.InvocationId`, which is nil for
   these frames). There is therefore no correlation id to tie a POST-inline
   response back to a specific pending client call — the LP client has no slot
   waiting on it.

3. **Racing the GET poll over one queue.** The render frames land on the single
   shared `LongPollingConnection.Outbound` queue
   (`IcCircuitTransportRegistry.cs:106`, `:119`). The LP client's GET `_poll`
   runs concurrently with the click POST. If the POST handler drained the queue
   inline *and* the GET poll also drained it, `DrainOutbound`
   (`IcCircuitTransportRegistry.cs:129`) is a destructive `TryDequeue` loop —
   whichever request wins gets the bytes and the other gets an empty body. The
   handshake-ack dedupe logic (`:102`, `:136`) exists precisely because
   duplicate/mis-ordered frames make `blazorpack` throw "Message is incomplete".
   Interleaving a second drainer multiplies that hazard.

Conclusion: you cannot make stock SignalR LongPolling commit a click in one
update without forking the client transport anyway. Once you accept a client-
side change, the render-as-query transport is the cleaner, already-proven target.

### 3.2 Recommended seam — converge interactive circuits onto a render-inline POST

Two viable shapes; **Option A is recommended.**

#### Option A (recommended): adopt the render-as-query transport for the click path

Make the interactive sample(s) that need single-update clicks run on
`UseInternetComputerWasp` (`WaspRenderEndpoints.cs:31`/`:48`) rather than the
SignalR-LP circuit transport for the local-click commit. The event POST already
returns the render batch inline (`WaspRenderEndpoints.cs:107–112`) and the client
already consumes it (`wasp.js:154`, `:174`, `:193`). This is the production
BlazorWasp transport today; the work is wiring the interactive components'
event-handler dispatch behind an `IWaspRenderer` so a click drives
`renderer.DispatchEvent` and serializes the resulting fragment, instead of
enqueuing BlazorPack frames for a poll.

- **Seam to change:** `IWaspRenderer.DispatchEvent` (consumed at
  `WaspRenderEndpoints.cs:107`) is the integration point. A renderer
  implementation that drives the real `CircuitHost.Renderer.DispatchEventAsync`
  (the same inline-dispatch trick as `CircuitHubFacade.cs:424–445`, reusing the
  `_context` reflection) and emits the post-render fragment as a
  `WaspRenderBatch` collapses the two calls into one.
- **No SignalR poll on the local-click path at all** — the queue/poll machinery
  (`LongPollingEndpoints.cs` GET, gh #115 fast-path) is simply not used for the
  click; it can remain for any app still on the SignalR-LP transport.

#### Option B (narrow): custom LP-style client shim that reads the POST body

If keeping the SignalR/CircuitHost BlazorPack pipeline is required, replace only
the *client transport* with a small shim (a hand-rolled `IConnection` /
`HttpTransport` substitute) that:

1. On the event POST, **reads the POST response body** (instead of discarding it)
   and feeds those bytes into the same `onreceive` path `blazor.web.js` uses.
2. Suppresses or gates the GET `_poll` so it does not race the POST over the
   queue for that connection.

Server side this requires:

- `LongPollingEndpoints.cs:163–171` (the POST handler) to call
  `conn.DrainOutbound()` and write the bytes inline **for this shim's
  connections**, with `ContentType = application/octet-stream` and a correct
  `ContentLength` (the GET handler already documents why `ContentLength` must be
  set before commit — `:178–181`).
- The POST must run on the update path and **must not** be served from a query
  fast-path (handler mutates the queue). `BeginInvokeDotNetFromJS`/inline
  dispatch already runs synchronously on the POST update call
  (`CircuitHubFacade.cs:441–447`), so by the time `HandleInbound` returns
  (`LongPollingEndpoints.cs:148`) the render frames are already on `Outbound`
  and can be drained in the same handler — no second await, no thread pool.
- The GET poll keeps serving *other-circuit / async* frames (e.g.
  cross-circuit reactivity, server-initiated JS calls) and stays on the gh #115
  empty-queue query fast-path for idle polls.

Option B keeps BlazorPack fidelity but forks the SignalR client; Option A reuses
a transport that already ships and is simpler. Pick A unless full SignalR/JS-
interop parity (streams, `DotNetObjectReference` round-trips) is a hard
requirement on the click path.

### 3.3 How the client consumes the inline render-diff

- **Option A:** exactly as `wasp.js` does today —
  `const batch = await resp.json()` then `_applyBatch(batch)` (`wasp.js:174`,
  `:193`, `:203`). No GET poll on the click path. The reactivity poll
  (`:779`) remains a separate cheap query for cross-client updates.
- **Option B:** the shim reads `await resp.arrayBuffer()` from the POST,
  splits the concatenated length-prefixed BlazorPack frames (same framing the
  GET poll returns — `LongPollingEndpoints.cs:173–176` notes each entry is "a
  complete length-prefixed frame the client can split"), and dispatches them
  into `blazor.web.js`'s `_processIncomingData` exactly as the receive loop
  would. The handshake-ack-once invariant (`IcCircuitTransportRegistry.cs:102`,
  `:136`) must be honored so the ack is never delivered twice across the
  POST-inline and any residual GET poll.

---

## 4. Risks

1. **No-threadpool synchronous-dispatch constraint.** The whole scheme depends
   on the `@onclick` handler completing **synchronously inline** on the update
   call so the render frames are on `Outbound` before the response is written.
   This already holds: `CircuitHubFacade.cs:420–447` installs the renderer's
   private `_context` and calls `DispatchEventAsync(..., waitForQuiescence:
   false)` so `CheckAccess()` is true and the handler runs inline. **Any handler
   that awaits a genuinely asynchronous operation** (a real timer, an
   `await Task.Yield()` that bounces to `TaskScheduler.Default`, an outbound
   `InvokeCoreAsync` awaiting a client Completion) will **not** have produced its
   render frames by the time the POST response is written — those frames would
   still need a later poll. The design must document that single-update commit
   covers synchronous handlers; async continuations fall back to the poll path
   (or are explicitly unsupported). The reflection field
   `RendererSynchronizationContextDispatcher._context` is also fragile across
   ASP.NET Core versions — `CircuitHubFacade.cs:430–431` already throws a
   descriptive error if the field name changes; that risk is inherited.

2. **gh #115 fast-path interaction.** The empty-queue query fast-path
   (`BlazorOnIcHostingExtensions.cs:179–233`) must keep its current behavior:
   GET polls with an empty queue stay cheap queries, GET polls with data bail to
   update. If Option B drains on the POST, the immediately-following GET poll
   for the same click will now usually find `Outbound.IsEmpty == true` (POST
   already drained it) and be served as a cheap empty query — good, that is the
   win. The hazard is the **race window**: the GET poll and the POST can be in
   flight simultaneously; if the GET poll's query snapshot observed a non-empty
   queue before the POST drained it, it upgrades to update and double-drains.
   `DrainOutbound`'s destructive dequeue means the loser gets an empty body
   (benign for render frames, but the handshake-ack-once guard must hold). Option
   A sidesteps this entirely by not using the queue/poll for clicks.

3. **Certified-response implications.** The click POST runs on the **update**
   path; update responses are signed by consensus and need no per-call cert (see
   `IcServer.cs:308–312`), so returning render bytes inline on the POST is
   cert-safe on both `.raw.` and canonical subdomains — this is exactly why
   `POST /_wasp/event` works on canonical today (`WaspRenderEndpoints.cs` does
   not register the event path for v2 cert; only `GET /_wasp/render` is
   pass-through-registered at `:57`). The reactivity/idle **GET** path still
   needs its v2 registration (`BlazorOnIcHostingExtensions.cs:238`
   `IcResponseCertV2.RegisterPassThroughPath("/_blazor", "GET")`) to be served on
   canonical; do not remove it. For Option A, the local-click POST inherits
   `/_wasp/event`'s already-working cert posture.

4. **Multi-tab / multi-circuit correctness.** The bound-facade map
   (`IcCircuitTransportRegistry.cs:40`, `:186`) exists so cross-circuit
   reactivity render-diffs reach *other* tabs' outboxes. Collapsing the local
   click to one update must not break the path by which tab B sees tab A's
   change — that remains a poll-driven (query) concern and is orthogonal to the
   local-click optimization. Verify a 2-tab scenario after the change.

5. **Ingress size.** The render fragment now rides the update response. Large
   diffs are bounded by the IC message envelope; the render-as-query client
   already enforces a ~1 MB image cap precisely for this reason
   (`wasp.js:501`). Keep the same bound.

---

## 5. Concrete numbered steps

1. **Decide transport.** Choose Option A (converge clicks onto the render-as-
   query `POST /_wasp/event` transport) unless full SignalR/BlazorPack JS-interop
   parity on the click path is mandatory, in which case choose Option B (custom
   LP client shim). Record the decision in this doc.

2. **(Option A) Implement an `IWaspRenderer` over the live CircuitHost.** Provide
   a renderer whose `DispatchEvent` (consumed at `WaspRenderEndpoints.cs:107`)
   resolves the target `eventHandlerId`, installs the renderer's `_context`
   (reuse the reflection from `CircuitHubFacade.cs:424–433`), calls
   `renderer.DispatchEventAsync(..., waitForQuiescence:false)` inline
   (`CircuitHubFacade.cs:445`), then serializes the resulting DOM fragment into a
   `WaspRenderBatch` (`EncodeBatch` shape, `WaspRenderEndpoints.cs:149`). No code
   in this doc — this is the implementation task.

3. **(Option A) Route interactive sample clicks through `/_wasp/event`.** Ensure
   the sample's clickable elements carry the `data-wasp-evt-click` markers
   `wasp.js` wires (`wasp.js:31`, `:45`) so a click POSTs to `/_wasp/event`
   and the response is applied via `_applyBatch` (`wasp.js:193`). Confirm the
   GET `/_blazor` poll is no longer on the local-click critical path.

4. **(Option B alternative) Add a connection-scoped "inline drain" flag.** Mark
   shim-originated `LongPollingConnection`s so the POST handler
   (`LongPollingEndpoints.cs:125`) drains `conn.DrainOutbound()`
   (`IcCircuitTransportRegistry.cs:129`) into the POST response body for those
   connections only, leaving stock SignalR-LP connections on the existing
   discard-and-poll behavior. Set `ContentLength` before commit
   (`LongPollingEndpoints.cs:178–181`).

5. **(Option B alternative) Gate the GET poll for shim connections.** Ensure the
   shim does not run a concurrent GET `_poll` that would race the POST over
   `Outbound`. Keep the gh #115 empty-queue fast-path
   (`BlazorOnIcHostingExtensions.cs:179`) for residual async/cross-circuit
   frames; verify the handshake-ack-once guard
   (`IcCircuitTransportRegistry.cs:136`) still holds.

6. **Document the synchronous-handler boundary.** State explicitly that
   single-update commit applies to handlers that complete synchronously inline
   (the common `@onclick` case). Handlers with genuinely async continuations
   fall back to the poll path (Option B) or are out of scope (Option A); add a
   trace/assert so an async handler doesn't silently lose its render frames.

7. **Verify cert posture.** Confirm the click POST returns inline bytes on both
   `.raw.` and canonical subdomains without a v2 registration (update responses
   are consensus-signed — `IcServer.cs:308–312`). Confirm the idle/reactivity
   GET still has its v2 pass-through registration
   (`BlazorOnIcHostingExtensions.cs:238` for SignalR-LP, or
   `WaspRenderEndpoints.cs:57` for render-as-query).

8. **Measure.** Instrument a single click and confirm exactly **one**
   `http_request_update` is issued (the event POST) and **zero** follow-up
   update calls for the GET poll. Compare against the current two-update
   baseline. Idle polls should remain cheap queries.

9. **Multi-tab regression check.** Open two tabs, click in tab A, confirm tab B
   still observes the change via the reactivity poll (query path) and that the
   local click in tab A cost one update.

---

## 6. Symbol index (load-bearing references)

| Concern | Symbol / file:line |
| --- | --- |
| Click POST handler (no drain) | `Wasp.AspNetCore.Blazor.Server/src/LongPollingEndpoints.cs:125`, drain-skip note `:163–171` |
| Follow-up GET poll (drains) | `LongPollingEndpoints.cs:182`, `DrainOutbound` call `:211` |
| Inline event dispatch (no threadpool) | `Wasp.AspNetCore.Blazor.Server/src/CircuitHubFacade.cs:379`, `_context` reflect `:424–433`, install `:441`, dispatch `:445` |
| Fire-and-forget click (no invocationId / Completion) | `Wasp.AspNetCore.Blazor.Server/src/BlazorHubDispatcher.cs:130–136`; nil id `IcCircuitTransportImpl.cs:118` |
| Outbound enqueue (the queue) | `IcCircuitTransportRegistry.cs:106` (queue), `:119` (`_send` = Enqueue) |
| Destructive drain | `IcCircuitTransportRegistry.cs:129` |
| gh #115 query fast-path (bail-to-update on data) | `Wasp.AspNetCore.Blazor.Server/src/BlazorOnIcHostingExtensions.cs:179–233` (null bail `:232`) |
| Upgrade-to-update mechanics | `Wasp.AspNetCore/src/IcServer.cs:266`, null-bail `:429`, `Upgrading()` `:465`; update cert note `:308–312` |
| Proven 1-update event POST (target) | `Wasp.AspNetCore.Blazor.Wasp/src/WaspRenderEndpoints.cs:91`, dispatch `:107`, inline write `:110–112` |
| Client reads POST body + applies | `Wasp.AspNetCore.Blazor.Wasp/wwwroot/wasp.js:154`, `:174`, `_applyBatch` `:193`/`:203` |
| Reactivity poll (separate cheap query) | `wasp.js:779`; render query endpoint `WaspRenderEndpoints.cs:58`, v2 reg `:57` |
| Handshake-ack-once guard | `IcCircuitTransportRegistry.cs:102`, `:136` |
