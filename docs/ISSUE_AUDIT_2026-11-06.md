# GitHub issue audit — 2026-11-06

Repository: [miadey/wasp-dotnet](https://github.com/miadey/wasp-dotnet).
Reviewed source: [b3ec62b](https://github.com/miadey/wasp-dotnet/commit/b3ec62b863ea7e177a71df1ade5f05e7d801159f).

## Result and evidence boundary

Reviewed the descriptions and comments of all **53 open issues**, then checked
the relevant source, project configuration and commit history. **Two issues are
closed as completed: #80 and #116. The other 51 remain open.** This is a dated
audit, not a live issue tracker.

“Open” does not mean that no work has been done. It means the requested behavior,
release or acceptance criteria are not fully established by the available
evidence. AOT implementations are not treated as completion of separate
Mono/runtime-track issues. Optional IC-WebSocket work explicitly retained by the
author is not closed simply because HTTP long polling became the preferred path.

Current host-test evidence is recorded in [Testing](TESTING.md): **147 passed,
1 failed**, plus **11/11 JavaScript decoder vectors**. These checks were run
during the documentation work on the same application/framework source; they were
not rerun just to change issue state. The failure concerns WaspRouter path/query
normalization, not the compiler defect in #80.

Docker was not running during this audit. No fresh Wasm build, replica deployment,
browser circuit session, mainnet acceptance or latency benchmark was performed.
The closures below rely on inspected current source and explicit historical
verification recorded in commits, not on a claim of new end-to-end execution.
No production canister was modified.

## Closed issues

### #80 — corrupted RenderBatch strings on wasm32-wasi

- [6aa9733](https://github.com/miadey/wasp-dotnet/commit/6aa9733e44613eeefced950f2595d532a73107a3)
  brings in the upstream compiler fix and records all four Issue80Repro struct-copy
  cases passing, followed by three real Blazor render-diff clicks (0 → 1 → 2 → 3).
- [7cebc85](https://github.com/miadey/wasp-dotnet/commit/7cebc8538aad31c072a8830f36e97aca7a9c529c)
  removes the server-side fake counter/DOM update workaround and oversized-batch
  guard, adds the patch installation step, and records another successful local
  click sequence.
- The [rebuild recipe](../aot/samples/Issue80Repro/REBUILD_JIT.md),
  [reproducer](../aot/samples/Issue80Repro/Program.cs),
  [compiler artifact](../runtime/inputs/libclrjit_universal_wasm32_x64.patched-issue80.so)
  and [installation script](../shared/tools/rebuild-vendor.sh) are still tracked.
  The artifact SHA-256 was checked and matches the script's expected value:
  `eec1df0e9367758c5301543a9fc4940d9164d82497fd9d4b458ba13b5638a8d5`.
- The correction requires the patched compiler. The script can skip installation
  when the stock compiler is not yet present in a fresh NuGet cache; that
  reproducibility gap remains under #64. This closure does not certify arbitrary
  builds using the unpatched upstream toolchain.

### #116 — preserve the Counter circuit across page navigation

- [5e628e8](https://github.com/miadey/wasp-dotnet/commit/5e628e839a70b5a46a287ac94bd7abd3ca4f1e99)
  implements the issue's proposed approach C: all three page shells stay mounted,
  and navigation changes their visibility. The Counter marker and DOM survive.
- [837ac86](https://github.com/miadey/wasp-dotnet/commit/837ac8686a3ea259fbdd4c2d554b7b4cb12350dd)
  fixes Home's empty-link full reload. Its recorded mainnet check navigates
  Counter → Home → Counter and increments the count while the negotiate request
  count stays at one.
- Current [App.razor](../aot/samples/BlazorVanilla/Components/App.razor) retains
  the mounted shells, Home `href="/"`, click interception, history updates and
  visibility-only navigation, including back/forward handling.
- Both fixes are ancestors of the reviewed commit. Some surrounding Razor
  comments still describe the older splice/reload workaround; the executable
  code and later fix supersede those comments.
- This closes the circuit-persistence issue for this sample. It does not establish
  generic stock Router support (#107) or a fresh latency benchmark.

## Certification work retained open

#61 and #117 have substantial implementations in
[IcCertifiedAssets](../aot/Wasp.AspNetCore/src/IcCertifiedAssets.cs),
[IcResponseCertV2](../aot/Wasp.AspNetCore/src/IcResponseCertV2.cs) and
[BlazorOnIcHostingExtensions](../aot/Wasp.AspNetCore.Blazor.Server/src/BlazorOnIcHostingExtensions.cs).

Commits [326f0f2](https://github.com/miadey/wasp-dotnet/commit/326f0f2586f2ef79e80678d808467d99c73335d9)
and [f7ac78b](https://github.com/miadey/wasp-dotnet/commit/f7ac78b857f1f98c8e6c2d8d97e0f1313e4008c1)
record canonical-subdomain verification, preserved update handling for state
changes, and 133 passing framework tests. Those 133 correspond to the current
7 + 126 passing host suites, not the entire repository's test count.

Historical negotiate timings of 270–410 ms do not fully meet #117's literal
“under 400 ms” criterion. Recorded empty polls of 296–385 ms and approximately
four-second warmup are encouraging but were not remeasured here. #61 also calls
for build-time population and M3 non-regression acceptance; the requested
WebAssembly hydration demo remains incomplete under #56. Retain both issues
until their remaining acceptance is demonstrated or their scope is explicitly
revised.

## Complete disposition

| Issue | Decision | Evidence or remaining gap |
| --- | --- | --- |
| [#14](https://github.com/miadey/wasp-dotnet/issues/14) | Open — incomplete | Managed Candid support for all requested records, variants, options and vectors, including reflection, is not established in the runtime track. |
| [#15](https://github.com/miadey/wasp-dotnet/issues/15) | Open — partial | Attributes exist, but runtime `WaspHost/Bridge.cs` still contains a sentinel dispatcher rather than the requested attribute-driven invocation. |
| [#16](https://github.com/miadey/wasp-dotnet/issues/16) | Open — partial | Runtime `MessageContext.cs` exposes ArgData, Caller and Now; the requested CycleBalance and MethodName API is missing. |
| [#17](https://github.com/miadey/wasp-dotnet/issues/17) | Open — incomplete | Rust/ic0 imports do not provide the requested managed StableCell, StableBTreeMap and StableLog wrappers. |
| [#19](https://github.com/miadey/wasp-dotnet/issues/19) | Open — incomplete | The runtime counter with AOT-equivalent endpoints and recorded performance is not delivered; an AOT click-counter fix does not satisfy this issue. |
| [#20](https://github.com/miadey/wasp-dotnet/issues/20) | Open — incomplete | No runtime-surface PocketIC integration suite wired into CI was found. |
| [#21](https://github.com/miadey/wasp-dotnet/issues/21) | Open — incomplete | The requested curated runtime ASP.NET reference-pack artifact is missing. |
| [#22](https://github.com/miadey/wasp-dotnet/issues/22) | Open — different track | An AOT IcServer exists; this issue belongs to the Mono/runtime ASP.NET milestone, whose dispatch/boot path remains incomplete. |
| [#23](https://github.com/miadey/wasp-dotnet/issues/23) | Open — different track | The AOT synchronization context and host tests do not establish the requested runtime-track integration. |
| [#24](https://github.com/miadey/wasp-dotnet/issues/24) | Open — different track | AOT HTTP feature implementations do not establish working request/response features in the runtime track. |
| [#25](https://github.com/miadey/wasp-dotnet/issues/25) | Open — different track | AOT hosting extensions exist; the requested runtime WebApplication builder-to-canister execution is not demonstrated. |
| [#26](https://github.com/miadey/wasp-dotnet/issues/26) | Open — partial | Asyncify infrastructure exists, but the runtime phase report records simdhash corruption/boot failure after yielding. |
| [#27](https://github.com/miadey/wasp-dotnet/issues/27) | Open — incomplete | The requested runtime ResponseStore and Rust streaming callback for large responses are not delivered. |
| [#28](https://github.com/miadey/wasp-dotnet/issues/28) | Open — incomplete | No runtime AspNetTodo sample with the requested reflection JSON and stable B-tree persistence was found; AOT TodoEf is a different sample. |
| [#29](https://github.com/miadey/wasp-dotnet/issues/29) | Open — incomplete | The runtime WaspIcHttpClient HTTP handler is not implemented. |
| [#30](https://github.com/miadey/wasp-dotnet/issues/30) | Open — unverified release | No evidence of publishing the two requested Wasp.Runtime NuGet packages as 0.1.0-preview was found. |
| [#31](https://github.com/miadey/wasp-dotnet/issues/31) | Open — unverified acceptance | The three runtime mainnet demos and 24-hour soak are not demonstrated. Existing AOT deployments are not this acceptance. |
| [#32](https://github.com/miadey/wasp-dotnet/issues/32) | Open — incomplete | The requested runtime/AOT comparative performance report is missing. |
| [#39](https://github.com/miadey/wasp-dotnet/issues/39) | Open — partial | Upload support stores bytes in a heap Vec and retains embedded BCL data; it does not establish the requested stable-memory upload/init round trip. |
| [#43](https://github.com/miadey/wasp-dotnet/issues/43) | Open — unresolved | The runtime boot/registration path remains blocked; successful execution within the instruction limit is not demonstrated. |
| [#53](https://github.com/miadey/wasp-dotnet/issues/53) | Open — incomplete | EmbeddedStaticFiles is a direct asset registry, not the requested IFileProvider integrated with default wwwroot embedding. |
| [#55](https://github.com/miadey/wasp-dotnet/issues/55) | Open — partial | AssetCanister.targets copies assets after Build and prints manual configuration; automatic dfx configuration, Publish split and hydration acceptance remain incomplete. |
| [#56](https://github.com/miadey/wasp-dotnet/issues/56) | Open — incomplete | RazorOnIc explicitly has no WASM client and uses a form POST. Interactive WebAssembly hydration/base-href acceptance is not met. |
| [#57](https://github.com/miadey/wasp-dotnet/issues/57) | Open — incomplete | No IcOutcallsHttpMessageHandler implementation, registration factory and requested endpoint analyzer were found. |
| [#59](https://github.com/miadey/wasp-dotnet/issues/59) | Open — incomplete | HTTP long polling does not complete the requested IC-WebSocket circuit transport and upgrade-survival acceptance. |
| [#61](https://github.com/miadey/wasp-dotnet/issues/61) | Open — implemented, acceptance incomplete | Static certification code and historical verification exist. Build-time static-route population and the requested M3 non-regression acceptance are not fully established; see certification notes below. |
| [#62](https://github.com/miadey/wasp-dotnet/issues/62) | Open — incomplete | The requested StaticPage/DynamicPage classification attributes and build-time analyzer are not implemented. |
| [#63](https://github.com/miadey/wasp-dotnet/issues/63) | Open — incomplete | General onboarding docs now exist, but the requested Wasp.Http migration walkthrough, project/Program diffs and export-collision guidance are not delivered. |
| [#64](https://github.com/miadey/wasp-dotnet/issues/64) | Open — partial | Some package versions are pinned, but the full build-image/toolchain reproduction contract remains incomplete; a fresh NuGet cache can also skip the #80 patch swap. |
| [#65](https://github.com/miadey/wasp-dotnet/issues/65) | Open — epic incomplete | Dependent asset, hydration, transport and classification work remains open; no full milestone acceptance. |
| [#71](https://github.com/miadey/wasp-dotnet/issues/71) | Open — optional backlog | Partial target/shim code exists. The author's comment explicitly retains this as optional IC-WebSocket work after the long-poll pivot. |
| [#72](https://github.com/miadey/wasp-dotnet/issues/72) | Open — optional backlog | IcWsEcho/gateway smoke acceptance is absent; explicitly retained as optional work in the issue comments. |
| [#75](https://github.com/miadey/wasp-dotnet/issues/75) | Open — optional backlog | Local gateway setup and browser integration CI are absent; explicitly retained as optional work. |
| [#76](https://github.com/miadey/wasp-dotnet/issues/76) | Open — optional backlog | The middleware bypass does not demonstrate reflection JSON under the requested size budget. The author explicitly retained this enhancement. |
| [#80](https://github.com/miadey/wasp-dotnet/issues/80) | Closed — correction verified in source/history | Patched upstream compiler artifact, reproducible recipe, matching hash and recorded real render-diff click verification; details below. |
| [#81](https://github.com/miadey/wasp-dotnet/issues/81) | Open — incomplete | No MultiCounter acceptance with multiple independent interactive roots was found; a shared CounterService is not equivalent. |
| [#83](https://github.com/miadey/wasp-dotnet/issues/83) | Open — incomplete | No requested FormDemo and end-to-end validation acceptance was found. |
| [#84](https://github.com/miadey/wasp-dotnet/issues/84) | Open — partial | Counter disposal exists, but the complete LifecycleLog lifecycle/disposal browser acceptance is missing. |
| [#85](https://github.com/miadey/wasp-dotnet/issues/85) | Open — incomplete | The requested CascadeDemo/ThemedChild acceptance is missing. |
| [#86](https://github.com/miadey/wasp-dotnet/issues/86) | Open — incomplete | The requested typed-int EventCallback demo and both SignalR-path acceptance are missing. |
| [#93](https://github.com/miadey/wasp-dotnet/issues/93) | Open — partial | vanilla-acceptance.mjs uses HTTP fetch, not a browser, and skips missing canisters. It does not satisfy the full browser/screenshot acceptance. |
| [#94](https://github.com/miadey/wasp-dotnet/issues/94) | Open — epic incomplete | The stock-router, MVC/static-file and browser-acceptance gaps remain; one-line vanilla compatibility is not established. |
| [#98](https://github.com/miadey/wasp-dotnet/issues/98) | Open — incomplete | No fail-fast empty TypeInfoResolver diagnostic was found; explicit resolver configuration in samples is only a workaround. |
| [#104](https://github.com/miadey/wasp-dotnet/issues/104) | Open — incomplete | MvcVanilla manually serves a CSS response; it does not implement UseStaticFiles through the requested embedded IFileProvider. |
| [#105](https://github.com/miadey/wasp-dotnet/issues/105) | Open — incomplete | The requested AddRazorSupportForMvc project change and Tag Helper acceptance are absent. |
| [#107](https://github.com/miadey/wasp-dotnet/issues/107) | Open — workaround | BlazorVanilla uses manual routing. It does not demonstrate that the stock Router SSR NullReferenceException is fixed. |
| [#108](https://github.com/miadey/wasp-dotnet/issues/108) | Open — unverified acceptance | No passing EmailAddress/RegularExpression sample under the size cap was found. |
| [#109](https://github.com/miadey/wasp-dotnet/issues/109) | Open — workaround | Removing/bypassing Router navigation components does not demonstrate working NotFoundPage, FocusOnNavigate and LayoutView after trimming. |
| [#110](https://github.com/miadey/wasp-dotnet/issues/110) | Open — workaround | Build scripts continue to use dotnet build. No fix and successful verification of the circular dotnet publish dependency were found. |
| [#114](https://github.com/miadey/wasp-dotnet/issues/114) | Open — not implemented | The query handler rejects methods other than GET; handshake-only POST requests still take the update path. |
| [#116](https://github.com/miadey/wasp-dotnet/issues/116) | Closed — correction verified in source/history | Persistent mounted page shells plus the Home-link fix implement proposed approach C; recorded navigation uses one negotiate request. Details below. |
| [#117](https://github.com/miadey/wasp-dotnet/issues/117) | Open — implemented, acceptance incomplete | The v2 negotiate/empty-poll path exists with historical boundary measurements. Recorded negotiate latency includes 410 ms, above the stated <400 ms criterion; no fresh end-to-end acceptance was performed. |
| [#118](https://github.com/miadey/wasp-dotnet/issues/118) | Open — partial | Render-as-query and Bzzz exist, but full Router/template/publishing acceptance is not established; the WaspRouter host suite also has an unresolved contract failure. |

## Follow-up order

1. Resolve the WaspRouter path/query contract and its existing failing test.
2. Make fresh toolchain setup reliably install the #80 compiler patch (#64).
3. Reproduce certification and circuit acceptance locally, then measure the
   canonical-subdomain criteria on an explicitly selected test deployment.
4. Complete the concrete sample/browser gaps before closing the larger epics.
5. Keep runtime boot work separate from the working AOT and Bzzz paths.

Useful source entry points:
[runtime status](../runtime/README.md),
[runtime phase C](../runtime/PHASE_C_STATUS.md),
[runtime bridge](../runtime/wasp_dotnet_app/WaspHost/Bridge.cs),
[runtime context](../runtime/wasp_dotnet_app/WaspHost/MessageContext.cs),
[asset target](../aot/Wasp.AspNetCore.Blazor.Server/Wasp.AspNetCore.AssetCanister.targets),
[HTTP harness](../aot/tests/vanilla-acceptance.mjs),
[WaspRouter tests](../aot/Wasp.AspNetCore.Blazor.Wasp.Tests/WaspRouterTests.cs).
