# M4 — Blazor Server on Canister

Multi-session implementation plan for running Blazor's **InteractiveServer** render mode (a.k.a. "Blazor Server") on an ICP canister. The work in `main` today is **Blazor Static SSR** — server-rendered HTML, no client interactivity. M4 turns that into a real interactive Blazor Server app where the C# event handlers run on the canister and UI diffs stream back to the browser over a persistent connection.

## What "Blazor Server" actually means (vs Blazor WASM, Blazor Static SSR)

| Variant | Where C# runs | Browser ↔ Server | What's downloaded |
|---|---|---|---|
| **Blazor Static SSR** (where we are) | Server only | Full HTTP request per interaction | HTML only |
| **Blazor Server** (M4 target) | Server only | Persistent WebSocket; events ↔ render diffs | `blazor.web.js` (~150 KB), no .NET runtime |
| **Blazor WebAssembly** | Browser only | Browser calls API endpoints | `_framework/*.wasm` (~5 MB Mono .NET runtime + DLLs) |

In Blazor Server: browser loads an HTML shell, downloads `blazor.web.js`, opens a WebSocket back to the server. Component state lives on the server in a `CircuitHost`. User events flow over WS, server runs the handler, computes the render diff, ships it back, browser patches DOM.

## The architecture on ICP

ICP doesn't have raw TCP sockets or "long-lived" connections — every request is a discrete update/query call against the canister. The bridge is the **IC-WebSocket protocol** (`ic-websocket-gateway`, `ic-websocket-js`, `ic-websocket-cdk`):

1. Browser opens a real WebSocket to a public IC-WS **gateway** (off-chain bridge service).
2. Gateway forwards each message as an `update_call` to the canister's `ws_message` export.
3. Canister responds + queues outbound messages.
4. Gateway polls the canister's `ws_get_messages` and ships them back to the browser over the WS.

The repo already has `Wasp.WebSockets` (port of [omnia-network/ic-websocket-cdk-rs](https://github.com/omnia-network/ic-websocket-cdk-rs)) implementing the canister-side protocol. So:

```
Browser ────WS────▶ ic-websocket-gateway ────update_call────▶ Backend canister
                                                                   │
                                                                   │ runs CircuitHost
                                                                   │ over Wasp.WebSockets
                                                                   ▼
                                                              Component state
                                                              (stable memory)

Browser ───HTTPS──▶ Asset canister ──▶ blazor.web.js + IC-WS adapter shim
```

Two canisters:

- **Asset canister** — stock DFINITY `assetstorage` (already used by `aot/samples/BlazorChat/dfx.json`). Serves the HTML shell + `blazor.web.js` + an IC-WS JS adapter shim that monkey-patches `WebSocket` on the page.
- **Backend canister** — our existing ASP.NET Core stack from M0–M2, plus a new `Wasp.AspNetCore.Blazor.Server` package that hosts the `CircuitHost` over `Wasp.WebSockets`.

## The hard parts (none are optional)

### 1. Blazor's `CircuitHost` is glued to SignalR

`Microsoft.AspNetCore.Components.Server.Circuits.CircuitHost` takes an `IClientProxy` (SignalR's send-to-client abstraction). Hub method dispatch (`StartCircuit`, `BeginInvokeDotNetFromJS`, `OnRenderCompleted`, `DispatchBrowserEvent`) goes through SignalR's `Hub` reflection. We replace `IClientProxy` with our IC-WS-backed proxy and replace SignalR's reflective dispatch with a source-generated switch.

### 2. `blazor.web.js` expects a real `WebSocket`

The JS client opens `new WebSocket("ws://...")`. We can't ship a custom Blazor JS, but `ic-websocket-js` provides an `IcWebSocket` class with the same JS interface. The shim either monkey-patches `window.WebSocket` for the Blazor connection, or we vendor a forked `blazor.web.js` (build from `dotnet/aspnetcore`) that uses `IcWebSocket` directly.

### 3. SignalR isn't AOT-clean for wasm32-wasi

`Microsoft.AspNetCore.SignalR.Hub` pulls in reflective method dispatch + `Type.GetMethods()` etc. Even if we don't *use* the transport, the service registrations bring it into the graph. Need ILLink substitutions or a SignalR-less hub host.

### 4. Per-canister-replica determinism

IC canisters can be replicated across subnet replicas. CircuitHost state needs to be deterministic per message. Likely OK because update calls are serialized through consensus, but needs validation.

### 5. Latency

Each browser event → WS message → gateway → canister update call (~2 s on mainnet) → response → gateway → browser. So every click is ~2 s. Mitigations: batch events on the client (`blazor.web.js` already does some); push static SSR for non-interactive sections; investigate whether queries (~100 ms) can carry render diffs (they can't — query calls don't generate canister state changes, which `CircuitHost` mutations require).

## Session breakdown

Each session ≈ a working week of focused effort. The map onto existing issues:

| Session | Scope | Maps to issue |
|---|---|---|
| **S1** | IC-WS gateway primer + Wasp.WebSockets demo canister | (new — propose **M4.0**) |
| **S2** | SignalR-coupling spike on CircuitHost | **#58** (M4.1) |
| **S3** | `IcCircuitTransport` skeleton + IClientProxy adapter | **#59** (M4.2, part A) |
| **S4** | Source-gen hub method table | **#59** (M4.2, part B) |
| **S5** | Stable-memory circuit persistence | **#59** (M4.2, part C) |
| **S6** | Asset canister + blazor.web.js IC-WS shim | (new — propose **M3.x** integration) |
| **S7** | Click-counter end-to-end | **#60** (M4.3) |

### S1 — IC-WS gateway primer (research + smoke test)

**Goal:** confirm the IC-WS path works end-to-end with a hand-rolled canister.

- Read [ic-websocket-gateway](https://github.com/omnia-network/ic-websocket-gateway) protocol docs.
- Walk `aot/Wasp.WebSockets/src/WaspWs.cs` — confirm what the canister-side surface looks like (`Init`, `Send`, `Close`, four `ws_*` exports).
- Build a minimal `samples/IcWsEcho` canister: on every `ws_message`, echo back with `WaspWs.Send`. Deploy locally + against a local gateway. Connect with `ic-websocket-js` from a static page.

**Deliverable:** `samples/IcWsEcho/` + `docs/m4-s1-icws-primer.md`. Demonstrates that the IC-WS bridge actually moves bytes both ways.

### S2 — CircuitHost SignalR coupling spike (issue #58)

**Goal:** identify the exact API seam to replace.

- Clone dotnet/aspnetcore release/10.0 locally; trace `CircuitHost` → `IClientProxy` → `HubConnectionContext`.
- Find every site that calls `IClientProxy.SendAsync` from circuit code (these are the "send-to-browser" calls — render diffs, etc.).
- Find every site where SignalR delivers an inbound message (these become inbound IC-WS messages — `DispatchBrowserEvent`, `OnRenderCompleted`, `BeginInvokeDotNetFromJS`, etc.).
- Define the `IcCircuitTransport` interface in C# (does not implement yet).

**Deliverable:** `docs/m4-s2-circuit-coupling.md` with file:line citations + `IcCircuitTransport` interface as a C# file.

### S3 — `IcCircuitTransport` minimal impl (issue #59, part A)

**Goal:** standalone `IClientProxy` adapter that, when given an outbound message, queues it into `Wasp.WebSockets.WaspWs.Send`. When IC-WS receives a message via `WsHandlers.OnMessage`, deserializes (CBOR) and dispatches to the registered handler.

- No CircuitHost integration yet — exercise via unit tests.
- Define the message envelope (matches SignalR JSON protocol so blazor.web.js parses it).

**Deliverable:** `aot/Wasp.AspNetCore.Blazor.Server/src/IcCircuitTransport.cs`, unit tests.

### S4 — Source-gen hub method table (issue #59, part B)

**Goal:** replace SignalR's reflective method dispatch.

- Mirror M1.3 source-generator pattern (`aot/Wasp.AspNetCore.SourceGenerator/`).
- Generate a switch over the hub method names (`StartCircuit`, `OnRenderCompleted`, etc.) → typed argument deserialization → method call.
- Substitute SignalR's `HubMethodDescriptor` lookup with our table via DI or ILLink substitution.

**Deliverable:** `aot/Wasp.AspNetCore.Blazor.Server.SourceGenerator/`.

### S5 — Stable-memory circuit persistence (issue #59, part C)

**Goal:** circuit state survives canister upgrade.

- The current `StableCell<T>` only holds 16 bytes — extend `Wasp.IcCdk.StableMemory` with variable-size regions (allocator).
- On every `OnRenderCompleted`, snapshot component state to a stable region keyed by `clientPrincipal`.
- On `OnPostUpgrade`, rehydrate.

**Deliverable:** `Wasp.IcCdk.StableRegion` (variable-size), `Wasp.AspNetCore.Blazor.Server.CircuitStore`.

### S6 — Asset canister + blazor.web.js IC-WS shim

**Goal:** browser loads the page from canister A, downloads `blazor.web.js`, JS adapter monkey-patches `window.WebSocket` so the Blazor connection routes through `ic-websocket-js` to canister B.

- Build the M3.1 `Wasp.AspNetCore.AssetCanister` MSBuild target (extract static assets into a sibling asset canister).
- Vendor `blazor.web.js` from a Microsoft.AspNetCore.Components.Web nupkg → put it in the asset canister output.
- Write `ic-ws-blazor-adapter.js` — wraps `window.WebSocket` for the specific Blazor endpoint, delegates to `IcWebSocket`.

**Deliverable:** `aot/Wasp.AspNetCore.AssetCanister/`, `samples/CircuitOnIc/wwwroot/_framework/{blazor.web.js, ic-ws-adapter.js}`.

### S7 — Click-counter end-to-end (issue #60)

**Goal:** single component with `@rendermode InteractiveServer`, click button on the live mainnet site, see the count update without a page reload.

- `samples/CircuitOnIc/` Razor app with one `Counter.razor` component.
- Deploy both canisters; verify in a real browser.

**Deliverable:** live working demo.

## Risks ranked

1. **`CircuitHost` private API surface.** Many methods we need to override are internal. Likely needs IL weaving (same approach as `Wasp.RenderTreeWeaver`).
2. **`blazor.web.js` WebSocket-API assumptions.** The JS expects a TCP-WebSocket-shaped object — if `IcWebSocket` doesn't match the shape closely enough, we may need to vendor & fork `blazor.web.js`. That couples us to ASP.NET Core minor versions.
3. **2 s per click is awful UX.** May need to push as much as possible to the client (static SSR for non-interactive sections, optimistic updates client-side).
4. **SignalR pulled in transitively by `AddRazorComponents().AddInteractiveServerRenderMode()`.** Same DataProtection pattern (#52 fix) may apply — TryAdd-based registrations we shadow with our own.
5. **IC-WS gateway availability.** Production needs a public gateway running 24/7 (omnia-network runs one; or self-host). Single point of failure outside our canister.
6. **State sync across replicas** when canister replicas diverge mid-circuit (network partition). Probably OK because update consensus serializes, but needs validation.

## What this **isn't**

- Not a SignalR-compatible Hub host. We're targeting Blazor's exact protocol, not arbitrary SignalR hubs.
- Not a drop-in for an existing Blazor Server app. Razor pages will work; anything using `IHttpContextAccessor` will trap.
- Not a substitute for proper Blazor WebAssembly. WASM (M3) has lower latency, lower server load, true offline-capable. M4 is for "I have C# logic I refuse to ship to the client."
- Not for production parity. The honest goal is a working click counter, not a Reddit clone.

## Concrete next session

Pick one of:

- **S1** (IC-WS primer) — lowest friction, validates the transport works at all. Recommended start.
- **S2** (#58 — coupling spike) — pure research, no code; produces the design doc that gates S3+.
- Skip ahead to **S6** (asset canister) — useful regardless of M4, also needed for M3 (Blazor WASM). Could be done in parallel with S1/S2 by a second contributor.

## Existing issues touched

- #58 (M4.1: spike — coupling points) — maps to S2.
- #59 (M4.2: `IcCircuitTransport`) — maps to S3 + S4 + S5.
- #60 (M4.3: click-counter demo) — maps to S7.

Proposed new issues (file after agreeing on this plan):

- **M4.0**: IC-WS gateway primer + Wasp.WebSockets echo sample.
- **M3.1-prereq**: Asset canister MSBuild target (already issue #55 — promote to M4 blocker).
- **M4.2.b**: source-gen hub method table.
- **M4.2.c**: stable-memory circuit persistence.

## References

- [omnia-network/ic-websocket-cdk-rs](https://github.com/omnia-network/ic-websocket-cdk-rs) — the Rust CDK we ported to `Wasp.WebSockets`.
- [omnia-network/ic-websocket-gateway](https://github.com/omnia-network/ic-websocket-gateway) — the off-chain bridge service.
- [omnia-network/ic-websocket-js](https://github.com/omnia-network/ic-websocket-js) — JS client we'll adapt for Blazor.
- [dotnet/aspnetcore `Microsoft.AspNetCore.Components.Server`](https://github.com/dotnet/aspnetcore/tree/release/10.0/src/Components/Server) — `CircuitHost`, `ComponentHub`, etc.
- [Blazor render modes (.NET 10)](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/render-modes?view=aspnetcore-10.0).
- `aot/Wasp.WebSockets/src/WaspWs.cs` — existing IC-WS canister implementation.
- `aot/Wasp.AspNetCore/UNSUPPORTED.md` — accumulated AOT trim caveats we'll encounter again.
