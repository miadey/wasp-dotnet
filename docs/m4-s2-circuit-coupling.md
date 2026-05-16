# M4.S2 — CircuitHost ↔ SignalR coupling spike

**Session:** M4.S2 (issue #58). **Status:** research spike — no runtime behavior shipped. **Deliverable:** this doc + `aot/Wasp.AspNetCore.Blazor.Server/src/IcCircuitTransport.cs` (interface only).

Goal: identify the exact API seam to replace so that S3 can land an `IcCircuitTransport` implementation that routes Blazor's CircuitHost over `Wasp.WebSockets` instead of SignalR.

All file:line citations are against `dotnet/aspnetcore` at branch `release/10.0`, path `src/Components/Server/src/`.

## TL;DR

1. The seam is **two-sided and narrow**: 13 inbound Hub methods on `ComponentHub`, 6 outbound `IClientProxy.SendAsync` call sites across `RemoteRenderer` + `RemoteJSRuntime`. Both sides go through `CircuitClientProxy` which wraps `ISingleClientProxy`. Replacing `ISingleClientProxy` is the surgical cut.
2. SignalR is brought in by **`services.AddSignalR()` at `ComponentServiceCollectionExtensions.cs:57`** — unconditional, called from `AddInteractiveServerRenderMode()`. There is no opt-out flag. To avoid SignalR's reflective Hub dispatch in AOT, we must **not call `MapBlazorHub`** and instead drive `CircuitHost` ourselves from `Wasp.WebSockets` callbacks.
3. The hub already uses **BlazorPack (MessagePack) as the sole protocol** (`ComponentServiceCollectionExtensions.cs:57-64`) — no JSON fallback to remove. That's the wire format the IC-WS payloads need to mimic so unmodified `blazor.web.js` parses them.
4. `CircuitClientProxy`, `CircuitFactory.CreateCircuitHostAsync`, and most of the `Circuits/` machinery are **internal**. S3 will need IL weaving (`Wasp.RenderTreeWeaver` pattern from M2 #54) to construct `CircuitHost` from outside the assembly, OR a vendored fork of `Microsoft.AspNetCore.Components.Server`.

## Inbound seam — `ComponentHub`

Path: `src/Components/Server/src/ComponentHub.cs`. Every method here is a SignalR Hub method invoked by `blazor.web.js`. The Hub deserializes the BlazorPack envelope, dispatches via `HubMethodDescriptor` reflection, and `await`s the method. For IC-WS we don't get any of that — we get a CBOR `WebsocketMessage` with a `content : blob`. So we must replicate the dispatch ourselves (S4 = source-gen).

| # | Method (verbatim signature) | Purpose | Notes for IC-WS |
|---|---|---|---|
| 1 | `ValueTask<string> StartCircuit(string baseUri, string uri, string serializedComponentRecords, string applicationState)` | First call from blazor.web.js after WS open. Returns the *circuit secret* used for ConnectCircuit on reconnect. | Returns a value — IC-WS is async-only, so the secret becomes a queued outbound message correlated by an invocation id we generate. |
| 2 | `Task UpdateRootComponents(string serializedComponentOperations, string applicationState)` | Add/remove root components after circuit start. | Fire-and-forget; easy. |
| 3 | `ValueTask<bool> ConnectCircuit(string circuitIdSecret)` | Reconnect to an existing circuit by secret. | Needs S5 (stable-memory circuit store) to actually rehydrate. |
| 4 | `ValueTask<string> ResumeCircuit(string circuitIdSecret, string baseUri, string uri, string rootComponents, string applicationState)` | Resume from persisted state. | Same as ConnectCircuit + needs persistence. |
| 5 | `ValueTask<bool> PauseCircuit()` | Persist + tear down circuit. | Trigger stable-memory snapshot. |
| 6 | `ValueTask BeginInvokeDotNetFromJS(string callId, string assemblyName, string methodIdentifier, long dotNetObjectId, string argsJson)` | **Event dispatch**. Every button click, input change, navigation, etc. arrives here. | Hot path. Latency-critical (each click = one IC update call ≈ 2 s). |
| 7 | `ValueTask EndInvokeJSFromDotNet(long asyncHandle, bool succeeded, string arguments)` | Browser's reply to a server-initiated `JS.BeginInvokeJS`. | |
| 8 | `ValueTask ReceiveByteArray(int id, byte[] data)` | Binary side-channel for `JS.ReceiveByteArray`. | |
| 9 | `ValueTask<bool> ReceiveJSDataChunk(long streamId, long chunkId, byte[] chunk, string error)` | Browser streams bytes to server. | One IC-WS message per chunk; ordering enforced by `WaspWs` sequence number. |
| 10 | `IAsyncEnumerable<ArraySegment<byte>> SendDotNetStreamToJS(long streamId)` | SignalR **server-stream**. | No IC-WS equivalent — we manually chunk into N `WaspWs.Send` calls. |
| 11 | `ValueTask OnRenderCompleted(long renderId, string errorMessageOrNull)` | Browser ack of a render batch. | Required for `RemoteRenderer.ProcessPendingRender` flow control. |
| 12 | `ValueTask OnLocationChanged(string uri, string? state, bool intercepted)` | URL changed via browser navigation. | |
| 13 | `ValueTask OnLocationChanging(int callId, string uri, string? state, bool intercepted)` | Pre-navigation hook. | |

There is **no** `DispatchBrowserEvent` in 10.x — that older protocol is gone. All event dispatch is folded into `BeginInvokeDotNetFromJS`.

## Outbound seam — `IClientProxy.SendAsync` call sites

Both files inject `CircuitClientProxy client` and store as `_client`. The proxy wraps `ISingleClientProxy.SendCoreAsync` and `InvokeCoreAsync<T>`.

### `Circuits/RemoteRenderer.cs`

| Approx line | Call | Args |
|---|---|---|
| ~88 | `_client.SendAsync("JS.AttachComponent", componentId, domElementSelector)` | `(int, string)` |
| ~223 | `_client.SendAsync("JS.RenderBatch", pending.BatchId, segment)` | `(long, ArraySegment<byte>)` |

`JS.RenderBatch` is the firehose — every UI mutation ships as a binary diff here. The `segment` is the BlazorPack-encoded render tree delta produced by `Microsoft.AspNetCore.Components.RenderTree.RenderBatchWriter`. We re-emit it byte-identical into a `WaspWs.Send` payload; `blazor.web.js` parses with its existing BlazorPack reader, no fork needed.

### `Circuits/RemoteJSRuntime.cs`

| Line | Call | Args |
|---|---|---|
| 104 | `_clientProxy.SendAsync("JS.EndInvokeDotNet", callId, success: false, errorMessage)` | `(string, bool, string)` |
| 110 | `_clientProxy.SendAsync("JS.EndInvokeDotNet", callId, success: true, resultJson)` | `(string, bool, string)` |
| 116 | `_clientProxy.SendAsync("JS.ReceiveByteArray", id, data)` | `(int, byte[])` |
| 140 | `_clientProxy.SendAsync("JS.BeginInvokeJS", asyncHandle, identifier, argsJson, resultType, targetInstanceId, callType)` | `(long, string, string, int, long, int)` |
| 193 | `_clientProxy.SendAsync("JS.BeginTransmitStream", streamId)` | `(long)` |

Total outbound surface: **6 distinct target method names**. The S3 `IcClientProxy` only needs to serialize these into BlazorPack envelopes — it doesn't need a general-purpose `IClientProxy`.

## `CircuitClientProxy` — the seam itself

`src/Components/Server/src/Circuits/CircuitClientProxy.cs` (full file, ~50 lines):

```csharp
internal sealed class CircuitClientProxy
{
    public bool Connected { get; private set; }
    public string ConnectionId { get; private set; }
    public ISingleClientProxy Client { get; private set; }

    public void Transfer(ISingleClientProxy client, string connectionId) { ... Connected = true; }
    public void SetDisconnected() { Connected = false; }

    public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
        => Client?.SendCoreAsync(method, args, ct) ?? throw ...;

    public Task<T> InvokeCoreAsync<T>(string method, object?[] args, CancellationToken ct = default)
        => Client?.InvokeCoreAsync<T>(method, args, ct) ?? throw ...;
}
```

This wraps `ISingleClientProxy`. **`ISingleClientProxy` is the one interface we need to implement.** Our `IcClientProxy : ISingleClientProxy` takes a per-client `byte[] clientPrincipal`, serializes `(method, args)` to a BlazorPack invocation message, and calls `WaspWs.Send(clientPrincipal, payload)`.

For `InvokeCoreAsync<T>` (return-value Hub methods — only #1, #3, #4, #5, #9 from the table above), the IC-WS protocol has no native request/response; we embed a SignalR-style invocation id and complete a per-id `TaskCompletionSource<T>` when the matching response arrives via `WsHandlers.OnMessage`.

## AOT/trim hazards

What `services.AddRazorComponents().AddInteractiveServerRenderMode()` drags in (call chain ends at `ComponentServiceCollectionExtensions.cs`):

| Line | Registration | AOT hazard |
|---|---|---|
| 47 | `services.AddDataProtection()` | **Already handled** — M2 #52 fix shadows the `IDataProtectionProvider` registration with our ephemeral provider. |
| 57-61 | `services.AddSignalR().AddHubOptions<ComponentHub>(o => { o.SupportedProtocols.Clear(); o.SupportedProtocols.Add(BlazorPackHubProtocol.ProtocolName); })` | **Trim-hostile core**. `AddSignalR()` registers `HubDispatcher`, `HubMethodDescriptor`, `IHubProtocolResolver`, etc. — all backed by `Type.GetMethods()` reflection over `ComponentHub`. |
| 64 | `services.TryAddEnumerable(ServiceDescriptor.Singleton<IHubProtocol, BlazorPackHubProtocol>())` | The BlazorPack protocol itself uses `MessagePack` reflection-based serialization (see `BlazorPack/BlazorPackHubProtocolWorker.cs`). |
| ~80-115 | Circuit infra (`CircuitFactory`, `CircuitRegistry`, `CircuitMetrics`, …) | Mostly fine for AOT — these are constructor-injected services. |

**Strategy:** don't try to make SignalR AOT-clean. Skip it entirely:

1. **Don't call `MapBlazorHub`.** The `ComponentHub` SignalR endpoint never gets registered, so the `HubDispatcher` never instantiates. The TryAdd-pattern in S2's plan (M2 #52 style) is the wrong tool — `AddSignalR()` uses `Add*` (non-Try), so we can't shadow it.
2. **Vendor or weave `CircuitFactory.CreateCircuitHostAsync`.** It currently takes an `ISingleClientProxy` from `HubCallerContext.Clients.Caller`. We need to call it with our `IcClientProxy`. The method is internal; same IL-weaving trick as `Wasp.RenderTreeWeaver` (M2 #54).
3. **Substitute SignalR's surface** via `ILLink.Substitutions.xml` for any leak-paths that still pull in `HubMethodDescriptor`. Most of these come from `HubLifetimeManager<ComponentHub>` and friends — if we never register the hub, the link trim should eliminate them, but verify with a build that has `-p:SuppressTrimAnalysisWarnings=false`.
4. **BlazorPack stays.** We're keeping the wire format. `BlazorPackHubProtocol` itself is small (~200 LoC). Its MessagePack dependency uses dynamic codegen by default, but has a static (`MessagePackSerializerOptions.Standard.WithResolver(...)`) mode that works on AOT. If trim warnings light up here, we substitute the dynamic resolver lookup.

## The `IcCircuitTransport` interface

`aot/Wasp.AspNetCore.Blazor.Server/src/IcCircuitTransport.cs` lands the seam shape:

```csharp
public interface IIcCircuitTransport
{
    ValueTask SendCoreAsync(string target, object?[] args, CancellationToken ct);
    ValueTask<T> InvokeCoreAsync<T>(string target, object?[] args, CancellationToken ct);
    event Action<IcCircuitInboundMessage>? InboundMessage;
    bool Connected { get; }
    string ConnectionId { get; }
}

public readonly record struct IcCircuitInboundMessage(string Target, object?[] Args);
```

Mirrors `CircuitClientProxy` outbound + an event for inbound (raised from `WsHandlers.OnMessage`). S3 will adapt this to `ISingleClientProxy` via a tiny adapter.

## Risks surfaced this spike

1. **Internal-API exposure**: `CircuitClientProxy`, `CircuitFactory`, `CircuitHost.CreateAsync`, `RemoteJSDataStream` — all internal. IL weaving needed; see `Wasp.RenderTreeWeaver` for the pattern.
2. **`SendDotNetStreamToJS` is `IAsyncEnumerable`** — a SignalR-specific streaming abstraction. IC-WS has no native streams; we chunk into discrete `WaspWs.Send` calls and reassemble on the client side. blazor.web.js's stream consumer expects SignalR's `StreamItem`/`StreamCompletion` frames, so we encode each chunk in that envelope.
3. **Invocation-id correlation for `InvokeCoreAsync<T>`** — SignalR generates an `InvocationId` and matches the `Completion` frame. We need to do the same inside the BlazorPack envelope; ordering is guaranteed by `WaspWs` sequence numbers.
4. **MessagePack AOT**: not yet validated end-to-end. If the linker eats the resolver, the canister will crash at first `JS.RenderBatch`. S3 must add a smoke test that round-trips a single render batch payload through BlazorPack before going further.
5. **Latency** (unchanged from M4 plan): every render diff is `WaspWs.Send` → `ws_get_messages` poll → gateway → browser. The gateway's poll interval is ~100 ms; the IC update for the inbound event is ~2 s. Total click→update RTT ≈ 2.1 s. Not a transport problem; an inherent IC consensus cost.

## Files to read next (S3 prep)

- `src/Components/Server/src/Circuits/CircuitFactory.cs` — constructor signature for the IL weaver to call.
- `src/Components/Server/src/Circuits/CircuitHost.cs` — the dispatch targets that the S4 source-generator will emit calls into.
- `src/Components/Server/src/BlazorPack/BlazorPackHubProtocol.cs` — wire format we must produce.
- `aot/Wasp.AspNetCore/Vendor/` — see how M2's `RenderTreeWeaver` opens internals; same pattern for `CircuitHost`.

## What this spike did NOT do

- No actual `IcClientProxy` impl — that's S3.
- No source-gen — that's S4.
- No `BlazorPack` round-trip test — that's a S3 prerequisite, scoped there.
- No `samples/CircuitOnIc/` Razor component — S7.
- No live test against a gateway (HelloChat IC-WS round-trip is already plumbed in `aot/samples/HelloChat/test-client/`; out of scope for this spike).
