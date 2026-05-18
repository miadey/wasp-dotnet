# M4 progress — Blazor Server on canister

Snapshot as of 2026-05-18. Companion to `m4-blazor-server-on-canister.md`
(plan) and `m4-s2-circuit-coupling.md` (spike).

## TL;DR

M4 click-counter demo is **working end-to-end in a real browser**. CircuitOnIc
serves the Counter page, slow click (SignalR Long Polling update call) and
fast click (query RPC, ~50 ms RTT) both increment the count. Architectural
pivot from original plan: transport is SignalR Long Polling on the canister's
own HTTP gateway, not IC-WebSockets — no off-chain gateway dependency.

Remaining defects: one race condition (#112 ValueStopwatch on first click
after fresh handshake), one broken build script (#111 CircuitOnIc), one
upstream framework bug worked around at the JS layer (#80
RenderBatchWriter strings table). See "What is NOT working" below.

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
| **S6** (#71) | Asset canister + blazor.web.js + IC-WS shim | superseded | Pivot: shipped Blazor Server demo routes over HTTP Long Polling, not IC-WS. `wwwroot/ic-ws-blazor-adapter.js` retained as optional future path. AssetCanister MSBuild target still useful for sibling-asset-canister scenarios (#71 kept open as enhancement). |
| **S5/S7** (#74) | `CircuitHubFacade` → real CircuitHost | ✅ done | `CircuitHubFacade.Bind(transport, factory, services)` calls real `CircuitFactory.CreateCircuitHostAsync(...)`. **12 of 13 hub methods** forward to live CircuitHost or have intentional implementations. Only `SendDotNetStreamToJS` (CircuitHubFacade.cs:536) still throws `NotImplementedException` — needs StreamItem/StreamCompletion frame writers in BlazorPackWriter; no shipped sample exercises it. `UpdateRootComponents` uses reflection to dodge the `IClearableStore` cross-assembly type collision. `ConnectCircuit`/`PauseCircuit` intentionally return false (no reconnect/no pause snapshot). `ResumeCircuit` tears down + starts fresh. |
| **S7** (#60) | Live click-counter demo | ✅ done (HTTP Long Polling pivot) | `aot/samples/CircuitOnIc/`, canister `vb2j2-fp777-77774-qaafq-cai`. Code section 10.45 MB (under 11.5 MB cap). Two click paths: **(a) slow click** via SignalR Long Polling carried by canister `http_request_update` — count 0→1 verified in real browser on 2026-05-18; **(b) fast click** via query RPC on canister `http_request` — 52–53 ms RTT, count 1→2 verified. State persisted to stable memory across upgrades. Architectural pivot from original IC-WS plan (see #60 close comment). Known issues: #112 ValueStopwatch race on first click after fresh handshake (intermittent, reload-and-wait recovers); #111 build-and-deploy.sh broken (works only via prebuilt .canister.wasm artifact). |

## Test counts (all real, all green — 2026-05-18)

```
xunit             : 133 / 133
JS cross-decoder  :  11 /  11  (@microsoft/signalr-protocol-msgpack)
```

Breakdown:
- Wasp.AspNetCore.Blazor.Server.Tests: 126
  - BlazorPack writer/reader/round-trip: 68
  - IcCircuitTransport (handshake, invocation, Ping, Close, completion correlation): 10
  - BlazorHubDispatcher (all 13 ComponentHub methods + error paths): 17
  - StableRegion + CircuitStore (alloc, upgrade rehydration, corruption resistance): 17
  - Vendor DLL post-weaver verification (CircuitFactory/Host/ClientProxy public, IVT stripped): 6
  - Other (RenderBatch decode, marker parsing, etc.): 8
- Wasp.AspNetCore.Tests: 7

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

## What is NOT working end-to-end (residual defects, 2026-05-18)

1. **#112 ValueStopwatch race.** First slow-click immediately after a fresh
   circuit handshake throws
   `InvalidOperationException: An uninitialized, or 'default', ValueStopwatch
   cannot be used to get elapsed time.` from
   `RemoteRenderer.ProcessPendingBatch`. The circuit disconnects.
   Workaround: reload + wait ~18 s before clicking; subsequent clicks all
   work. Smells like an ordering race between `StartCircuit` initial render
   and the first `OnRenderCompletedAsync` ack.

2. **#111 CircuitOnIc build script broken.** The committed
   `samples/CircuitOnIc/build-and-deploy.sh` references the wrong docker
   image, uses `dotnet publish` (tripping #110 circular dep), and is missing
   the entire post-link pipeline (icp-publish + wasi-stub) that RazorOnIc
   has. The shipped 10.45 MB `CircuitOnIc.canister.wasm` works when
   installed directly via `dfx canister install --wasm`. Anyone modifying
   the sample today has to re-derive the build pipeline manually.

3. **#80 Framework RenderBatchWriter strings table is empty for delta
   batches on wasi-wasm AOT.** Workaround active in CircuitHubFacade
   (lines 67–76): after each click we push a parallel
   `JS.BeginInvokeJS → window.waspSetCount` so the visible counter
   increments via direct DOM update. Upstream framework bug; the workaround
   is sticky until the framework is patched.

4. **`SendDotNetStreamToJS` still throws.** Last of the original 5
   `IBlazorHubFacade` stubs (`CircuitHubFacade.cs:536`). Needs StreamItem
   /StreamCompletion frame writers in BlazorPackWriter. No shipped sample
   exercises `DotNetStreamReference` interop, so this is fence-tier.

## How to extend

See `aot/samples/CircuitOnIc/Program.cs` for the wired example. The hosting
extensions are in `BlazorOnIcHostingExtensions.cs`:

```csharp
// In samples/CircuitOnIc/Program.cs:
var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
{
    ContentRootPath = "/canister",
    ApplicationName = "CircuitOnIc",
});
builder.AddBlazorOnIC();                              // 1. Wasp setup
var app = builder.Build();
app.MapBlazorOnIC<App>(typeof(Program).Assembly);     // 2. SSR + /_blazor endpoints
// + any app-specific endpoints (query RPC, etc.)
```

## References

- Closed-as-done: #58, #60, #66, #67, #68, #69, #70, #73, #74, #87, #88, #90, #91
- Open residual: #112 (ValueStopwatch race), #111 (build script), #80 (RenderBatchWriter), #76 (System.Text.Json reflection trim — now optional), plus the M4.S8.* extension issues #81–#86 and the M4.S9.6* MVC trim cluster #100–#105.
- Architectural pivot: #71, #72, #75 (IC-WS path) kept open as optional future work; shipped demo uses HTTP Long Polling.
- Source roots: `aot/Wasp.AspNetCore.Blazor.Server/`, `aot/Wasp.IcCdk/src/StableRegion.cs`, `shared/tools/Wasp.CircuitHostWeaver/`
- Vendored: `aot/Wasp.AspNetCore.Blazor.Server/Vendor/Microsoft.AspNetCore.Components.Server.dll` (do not edit by hand — regenerate via the weaver)
