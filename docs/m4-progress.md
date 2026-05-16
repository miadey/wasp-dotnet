# M4 progress — Blazor Server on canister

Snapshot as of 2026-05-16. Companion to `m4-blazor-server-on-canister.md`
(plan) and `m4-s2-circuit-coupling.md` (spike).

## Status by session

| Session | What it is | Status | Files / tests |
|---|---|---|---|
| **S0** (#72) | IC-WS gateway smoke test sample | open | `aot/samples/IcWsEcho/` not started |
| **S1** | Wasp.WebSockets IC-WS port | ✅ landed pre-M4 | `aot/Wasp.WebSockets/src/WaspWs.cs` |
| **S2** (#58) | CircuitHost ↔ SignalR coupling spike | ✅ done | `docs/m4-s2-circuit-coupling.md`, `IcCircuitTransport.cs` |
| **S3** (#66, #67) | IcCircuitTransport bidirectional pump | ✅ done | `IcCircuitTransport.cs` (interface), `IcCircuitTransportImpl.cs` (concrete), `IcCircuitTransportRegistry.cs`, `IcClientProxy.cs` (refactored), 10 transport tests + 11 JsDecode vectors |
| **S4** (#68) | Hub method dispatch (compile-time, replaces SignalR reflection) | ✅ done | `IBlazorHubFacade.cs`, `BlazorHubDispatcher.cs`, 17 dispatcher tests covering all 13 ComponentHub methods |
| **S5** (#69) | Cecil weaver opens Microsoft.AspNetCore.Components.Server internals | ✅ done | `shared/tools/Wasp.CircuitHostWeaver/`, `Vendor/Microsoft.AspNetCore.Components.Server.dll`, `.targets` for ILC substitution, 6 vendor-DLL verification tests |
| **S5** (#70) | StableRegion + CircuitStore (variable-size stable memory + per-principal snapshots) | ✅ done | `Wasp.IcCdk/src/StableRegion.cs`, `Wasp.AspNetCore.Blazor.Server/src/CircuitStore.cs`, 17 stable-memory tests including upgrade round-trip |
| **S6** (#71) | Asset canister + blazor.web.js + IC-WS shim | partial | `wwwroot/ic-ws-blazor-adapter.js` (JS shim, ready), `Wasp.AspNetCore.AssetCanister.targets` (MSBuild glue, ready). Missing: vendored `blazor.web.js` + `ic-websocket-js` UMD bundle, dfx.json wiring example. |
| **S5/S7** | `CircuitHubFacade` → real CircuitHost | ❗ blocked | `CircuitHubFacade.cs` exists with full wiring of `transport.InboundMessage → BlazorHubDispatcher.Dispatch`, but the 13 hub-method bodies throw `NotImplementedException` — they need to forward into `CircuitFactory.CreateCircuitHostAsync(...).{StartCircuitAsync, BeginInvokeDotNetFromJSAsync, ...}`. That call is now reachable (weaver made the types public) but requires standing up an `IServiceProvider` populated by `services.AddRazorComponents().AddInteractiveServerRenderMode()`, plus a synthetic `HttpContext` and `JS interop runtime`. **This is the gating piece for any click-counter demo.** |
| **S7** (#60) | Live click-counter demo | ❗ blocked on S5/S7 above | no sample, no deploy |

## Test counts (all real, all green)

```
xunit             : 118 / 118
JS cross-decoder  :  11 /  11  (@microsoft/signalr-protocol-msgpack)
```

Breakdown:
- BlazorPack writer/reader/round-trip: 68
- IcCircuitTransport (handshake, invocation, Ping, Close, completion correlation): 10
- BlazorHubDispatcher (all 13 ComponentHub methods + error paths): 17
- StableRegion + CircuitStore (alloc, upgrade rehydration, corruption resistance): 17
- Vendor DLL post-weaver verification (CircuitFactory/Host/ClientProxy public, IVT stripped): 6

## What is *actually* working end-to-end on a canister

**Wire format**: byte-identical to what `blazor.web.js` parses. Proven by
shipping 11 hand-crafted invocations through the canonical
`@microsoft/signalr-protocol-msgpack` decoder.

**Transport pump**: `WsHandlers.OnMessage` → SignalR handshake ack →
BlazorPack invocation → `InboundMessage` event. Outbound:
`SendCoreAsync(target, args)` → bytes ready for `WaspWs.Send`. Verified by
unit tests that drive the transport without booting a canister.

**Hub dispatch**: `IcCircuitInboundMessage` → typed unboxing → method call
on an `IBlazorHubFacade`. Compile-time switch, no reflection. Works against
a recording test double.

**Stable persistence**: per-principal snapshots survive a simulated canister
upgrade. Verified by save → discard CircuitStore → fresh CircuitStore →
LoadFromStable → Restore returns original bytes.

**Cecil weaver**: produces a `Microsoft.AspNetCore.Components.Server.dll`
with `CircuitFactory`, `CircuitHost`, `CircuitClientProxy`, etc. promoted
to `public`. Verified by `MetadataLoadContext` inspection.

## What is NOT working end-to-end (and what blocks it)

1. **No live CircuitHost.** `CircuitHubFacade` is wired into the transport
   correctly but its 13 hub methods throw `NotImplementedException`. To
   close them:
   - Construct a `Microsoft.AspNetCore.Components.Server.Circuits.CircuitHost`
     via `CircuitFactory.CreateCircuitHostAsync(...)`.
   - That ctor needs an `IServiceProvider` populated by
     `services.AddRazorComponents().AddInteractiveServerRenderMode()`,
     plus an `HttpContext`, `JS runtime`, `ResourceAssetCollection`,
     `ServerComponentSerializer`, and a `CircuitClientProxy` wrapping our
     `IcClientProxy`.
   - Most of these services are AOT-clean. The exception is
     `BlazorPackHubProtocol` — uses MessagePack reflective resolvers; we
     either substitute via `ILLink.Substitutions.xml` or never resolve the
     service (we already don't use SignalR protocol negotiation, so we may
     get away with not registering `BlazorPackHubProtocol` at all).
   - Estimate: 2–3 days of focused work to get the first
     `StartCircuit → JS.RenderBatch` cycle running in an xunit test that
     uses an in-memory backend.

2. **No asset bundle.** Need to vendor `blazor.web.js` from
   `Microsoft.AspNetCore.Components.Web 10.0.6` and bundle the
   `ic-websocket-js` UMD. Both are downloads + a `.targets` addition.
   Estimate: 1 day.

3. **No deploy.** `samples/CircuitOnIc/` doesn't exist. Needs the
   `CircuitHubFacade` integration above + an HTTP endpoint that serves the
   IC-WS handshake on `/_blazor/negotiate`. The wasm artifact then deploys
   like `aot/samples/RazorOnIc/` (which is already on mainnet).
   Estimate: 1 day (after #1 + #2 land).

## How to extend

To make a click-counter actually round-trip:

```csharp
// In samples/CircuitOnIc/Program.cs (does not exist yet):
WaspWs.Init(new WsHandlers
{
    OnOpen    = registry.HandleOpen,
    OnMessage = registry.HandleMessage,
    OnClose   = registry.HandleClose,
});

registry.TransportConnected += transport =>
{
    // ← TODAY: this calls into CircuitHubFacade.Bind which sets up
    //   dispatcher + completion sink correctly, but all 13 method
    //   bodies throw NotImplementedException because no real
    //   CircuitHost is constructed.
    var facade = CircuitHubFacade.Bind(transport);
    // To finish, CircuitHubFacade.Bind also needs to be passed an
    // IServiceProvider so it can resolve CircuitFactory and emit:
    //   _circuit = await factory.CreateCircuitHostAsync(...);
    // and then forward each hub method into _circuit.
};
```

## References

- Issues: #58 ✅ closed, #66, #67, #68, #69, #70, #71, #72 (open), #60 (open, M4.3 demo)
- Source roots: `aot/Wasp.AspNetCore.Blazor.Server/`, `aot/Wasp.IcCdk/src/StableRegion.cs`, `shared/tools/Wasp.CircuitHostWeaver/`
- Vendored: `aot/Wasp.AspNetCore.Blazor.Server/Vendor/Microsoft.AspNetCore.Components.Server.dll` (do not edit by hand — regenerate via the weaver)
