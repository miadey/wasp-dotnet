# M4 progress — Blazor Server on canister

Snapshot as of 2026-05-18 (afternoon — feature-switch trim session).
Companion to `m4-blazor-server-on-canister.md` (plan) and
`m4-s2-circuit-coupling.md` (spike).

## TL;DR

M4 click-counter demo is **working end-to-end in a real browser**. CircuitOnIc
serves the Counter page, slow click (SignalR Long Polling update call) and
fast click (query RPC, ~50 ms RTT) both increment the count. Architectural
pivot from original plan: transport is SignalR Long Polling on the canister's
own HTTP gateway, not IC-WebSockets — no off-chain gateway dependency.

Remaining defects: one upstream framework bug worked around at the JS
layer (#80 RenderBatchWriter strings table). The earlier #111 (CircuitOnIc
broken build script) closed in afternoon session; #112 (ValueStopwatch
race) is fix-committed via `ValueStopwatch.IsActive=true` substitution
pending one browser re-verification.

## Code-section budget (the binding architectural constraint)

The IC validator caps wasm modules at **11.5 MB code section** per canister
(decompressed; gzip does not help — the validator reads the parsed code
section). NativeAOT-LLVM expands C# IL → wasm machine code at roughly
3–5× IL size, so the framework + user code are tightly bounded.

After the 2026-05-18 trim session:

| Sample        | Code section | Headroom | Notes |
|---------------|-------------:|---------:|-------|
| BlazorVanilla |     9.50 MB  |  2.00 MB | Static SSR only (`EnableInteractiveServer=false`) |
| CircuitOnIc   |     9.54 MB  |  1.96 MB | Live Blazor Server circuit |

This headroom is real but bounded. Roughly:

- 400–600 KB of additional user IL after NativeAOT expansion
- ~50–100 components + modest domain logic
- Forms, navigation, custom services, query/update endpoints — all fine
- **Does NOT fit**: `AddAuthentication`/`AddIdentity` (~1 MB),
  EF Core (~2 MB), JWT bearer (~1 MB), heavy validation regex chains
  (#108 territory).

What got the headroom from ~600 KB to ~2 MB on 2026-05-18:

1. **Feature switches** in each sample's csproj — `EventSourceSupport=false`,
   `MetricsSupport=false`, `DebuggerSupport=false`, `HttpActivityPropagationSupport=false`,
   `BuiltInComInteropSupport=false`, `EnableUnsafeBinaryFormatterSerialization=false`,
   `EnableUnsafeUTF7Encoding=false`, `XmlResolverIsNetworkingEnabledByDefault=false`,
   `UseNativeHttpHandler=false`, `_AggressiveAttributeTrimming=true`,
   `_DataSetXmlSerializationSupport=false`, `IlcOptimizationPreference=Size`,
   `OptimizationPreference=Size`, `IlcGenerateStackTraceData=false`.
   Combined: ~900 KB drop. The single biggest mover was
   `IlcGenerateStackTraceData=false` (drops stack-trace symbol metadata —
   typically the largest entry in NativeAOT's data section, indirectly
   affecting code via reflection roots).
2. **`EnableInteractiveServer=false`** on BlazorVanilla via `IcOptions` —
   skips `AddInteractiveServerComponents` and the circuit transport
   endpoint chain. Trimmer was already mostly pruning this when unused;
   the flag makes the intent explicit and saves ~5 KB.
3. **ILLink substitutions** — `EventSource.IsEnabled() → false`,
   `ActivitySource.HasListeners() → false`,
   `DiagnosticListener.IsEnabled(...) → false`,
   `ValueStopwatch.IsActive → true` (fixes #112 race),
   `AuthenticatedEncryptorConfiguration.Validate() → no-op`,
   `RegexInterpreter.ctor → no-op`. Combined: ~20 KB; the architectural
   correctness is the larger win.

## Architectural escape hatches beyond 2 MB

When the 2 MB headroom is exhausted (heavy framework or large user app),
the only paths forward on the AOT story are:

1. **Multi-canister split** — each canister gets its own 11.5 MB code
   section. Patterns: `backend + assets` (DFINITY's `assetstorage` for
   static files), `presentation + data`, `logic + auth`. Cost: each
   cross-canister call is ~2 s update latency, so split along axes where
   inter-canister calls are infrequent.
2. **Runtime path** (`runtime/` — Phase B/C work) — Mono interpreter in
   the canister code section (~7.5 MB fixed) + framework + user DLLs in
   stable memory. Single canister, unbounded user code. **Currently
   research-blocked**: see `runtime/PHASE_B_RESUME.md` for the
   dn_simdhash function-pointer relocation issue. Estimated 1–3 days IF
   the proposed wasm-fnptr-fixup pass works first try; multi-week if it
   needs the dn_simdhash-replacement fallback (option A in the resume
   doc) or the wasm-ld pivot (option B).

**What does NOT help:**
- wasm64. IC accepts wasm64 modules today (no flag needed on dfx 0.29.2+),
  but per `wasp-php-85/docs/archive/wasm64_investigation.md` wasm64 makes
  code section ~9.2% *larger* (i64 instructions are bigger encoded). Only
  helps with heap >4 GiB — irrelevant to our code-section problem.
- Gzipping the wasm. The IC validator decompresses before measuring
  code section size. Gzip helps install-message size and on-disk storage
  cost only.
- Asset canister specifically. Static files (blazor.web.js, css, fonts)
  live in the wasm's **data section**, not code section. Moving them
  out saves ~150 KB of data + heap, zero code section impact.

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
| **S7** (#60) | Live click-counter demo | ✅ done (HTTP Long Polling pivot) | `aot/samples/CircuitOnIc/`, canister `vb2j2-fp777-77774-qaafq-cai`. Code section **9.54 MB** after afternoon trim session (under 11.5 MB cap, 2.0 MB headroom). Two click paths: **(a) slow click** via SignalR Long Polling carried by canister `http_request_update` — count 0→1 verified in real browser on 2026-05-18; **(b) fast click** via query RPC on canister `http_request` — 52–53 ms RTT, count 1→2 verified. State persisted to stable memory across upgrades. Architectural pivot from original IC-WS plan (see #60 close comment). Build script fixed in commit `b6106d6` (#111 closed); #112 ValueStopwatch race likely fixed via `ValueStopwatch.IsActive=true` substitution (pending one browser re-verification of the failure mode). |

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

## What is NOT working end-to-end (residual defects)

1. **#80 Framework RenderBatchWriter strings table is empty for delta
   batches on wasi-wasm AOT.** Workaround active in CircuitHubFacade
   (lines 67–76): after each click we push a parallel
   `JS.BeginInvokeJS → window.waspSetCount` so the visible counter
   increments via direct DOM update. Upstream framework bug; the workaround
   is sticky until the framework is patched.

2. **`SendDotNetStreamToJS` still throws.** Last of the original 5
   `IBlazorHubFacade` stubs (`CircuitHubFacade.cs:536`). Needs StreamItem
   /StreamCompletion frame writers in BlazorPackWriter. No shipped sample
   exercises `DotNetStreamReference` interop, so this is fence-tier.

3. **#107 Router NRE workaround.** BlazorVanilla uses a manual
   `NavigationManager`-based router in `App.razor` because the stock
   `<Router>/<RouteView>` chain NREs in
   `StaticHtmlRenderer.RenderAttributes`. Real fix needs a Cecil weaver
   pass on `Microsoft.AspNetCore.Components.dll` (the same pattern as
   `Wasp.RenderTreeWeaver` for the Append* fix). Workaround is stable.

## Closed during 2026-05-18 sessions

- #58, #60, #66, #67, #68, #69, #70, #73, #74, #87, #88, #90, #91, #92,
  #97, #100, #101, #102, #103, #106, #111 — 21 issues, all with verified
  evidence in commit messages and `gh issue close` comments.

## Likely-fixed-pending-verification

- #112 ValueStopwatch race — `ValueStopwatch.IsActive=true` substitution
  in `Wasp.AspNetCore/ILLink.Substitutions.xml` directly kills the throw
  path in #112's stack trace. The bug requires a specific race window
  (fresh circuit handshake → immediate slow-click) to reproduce; the
  fix is theoretically certain but end-to-end browser re-verification
  is the satisfying close. Browser-test agent in flight as of this
  doc revision.

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

- Closed-as-done: #58, #60, #66, #67, #68, #69, #70, #73, #74, #87, #88,
  #90, #91, #92, #97, #100, #101, #102, #103, #106, #111
- Open residual: #80 (RenderBatchWriter — upstream), #107 (Router NRE —
  workaround in place), #108/#109/#110 (smaller Blazor trim/router items),
  #112 (ValueStopwatch — fix-committed, verify pending), #76 (S.T.Json
  reflection trim — now optional), and the M4.S8.* feature-expansion
  issues #81–#86 (multi-component, NavLink, EditForm, lifecycle,
  CascadingValue, EventCallback<T>).
- Architectural pivot: #71, #72, #75 (IC-WS path) kept open as optional
  future work; shipped demo uses HTTP Long Polling.
- Source roots: `aot/Wasp.AspNetCore.Blazor.Server/`,
  `aot/Wasp.IcCdk/src/StableRegion.cs`,
  `shared/tools/Wasp.CircuitHostWeaver/`
- Vendored: `aot/Wasp.AspNetCore.Blazor.Server/Vendor/Microsoft.AspNetCore.Components.Server.dll`
  (do not edit by hand — regenerate via the weaver)
- Track B research: `runtime/PHASE_B_RESUME.md`,
  `runtime/PHASE_C_STATUS.md`. Currently blocked on a wasm-merge
  function-pointer relocation issue per the resume doc's section 4.
  Three identified fix paths (wasm-fnptr-fixup tool, dn_simdhash
  replacement, wasm-ld pivot); none attempted in current code.
- Size-trim context for the 2 MB headroom number:
  `wasp-php-85/docs/archive/wasm64_investigation.md` (sibling project's
  wasm64 measurements; cited in "What does NOT help" above).
