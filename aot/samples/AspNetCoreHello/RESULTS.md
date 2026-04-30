# Issue #47 — AspNetCoreHello: **PASSED**

## Verdict

The full ASP.NET Core middleware pipeline runs inside an Internet Computer
canister. `curl http://<canister-id>.localhost:4944/` returns the literal
string from a `MapGet` handler. Routing dispatches correctly (200 for matched
routes, 404 for unmatched).

This is the first end-to-end proof that `WebApplication` + `IServer` (via
`Wasp.AspNetCore.IcServer`) + `MapGet` works in canister wasm.

## Test results

```
GET / ............ 200  Hello from ASP.NET Core inside an IC canister!
GET /health ...... 200  ok
GET /missing ..... 404  (from ASP.NET Core endpoint routing)
```

## Final canister wasm

| Metric | Value |
|---|---|
| Final canister `.wasm` (after `icp-publish` + `wasi-stub`) | **9.04 MB** |
| Budget | < 50 MB |

## What it took (roadmap of fixes during M0.4)

1. **`FrameworkReference Microsoft.AspNetCore.App` must be on the consumer**
   csproj, not just on `Wasp.AspNetCore` — transitive flow is dropped by
   NativeAOT-LLVM.
2. **AspNet impl dlls must be passed via `<IlcReference>`** — there's no
   `Microsoft.AspNetCore.App.Runtime.wasi-wasm`, so we feed the linux-x64
   impl IL directly (it's platform-independent).
3. **`wasi-stub` extended** to also strip Preview 2 component imports
   (`wasi:clocks/monotonic-clock@0.2.0`, `wasi:io/poll@0.2.0`) which .NET 10
   threading code emits. Tool also now traps `proc_exit` (which is
   `[[noreturn]]` and was leading to undefined behavior on return) and
   returns `EBADF (8)` for `fd_prestat_get` to short-circuit preopen
   enumeration cleanly.
4. **Trim-time substitutions via `<IlcArg>` (NOT `<EmbeddedResource>`)** —
   embedded substitutions only apply to types in the embedding assembly
   (warning IL2101). For cross-assembly substitutions (`Directory.Exists` in
   `System.Private.CoreLib`, `PhysicalFileProvider.ctor` in
   `Microsoft.Extensions.FileProviders.Physical`), the xml must be passed to
   ilc directly via `--substitution=`.
5. **`PhysicalFileProvider` ctor stubbed** because it calls `Directory.Exists`
   and would throw `DirectoryNotFoundException` on canister. The provider is
   never actually read in this sample (no static files), so a no-op ctor is
   safe. M2 (#52) replaces it with an embedded-resource provider.
6. **`WebApplication.CreateEmptyBuilder` instead of `CreateSlimBuilder`** —
   even Slim builder loads `appsettings.json` via JsonConfigurationExtensions,
   which dereferences a stubbed PhysicalFileProvider and traps. Empty builder
   skips all defaults; we add `AddRoutingCore()` ourselves.
7. **`IcServer.Dispatch` upgrades all queries to update calls** — the
   ASP.NET pipeline blows past the 5B instruction query limit on first touch.
   Update calls have a 40B limit. Per-route certified queries are M5 (#61).

## Reproducing

```bash
cd /Users/miadey/dev/csharp/aot/samples/AspNetCoreHello
./build-and-deploy.sh
```

Requires:
- Docker running, `wasp-dotnet-build:latest` image present
- dfx running on `127.0.0.1:4944` (script auto-starts it; project's `dfx.json`
  has `networks.local.bind = "127.0.0.1:4944"` to avoid colliding with other
  dfx replicas on the default 4943)

## Files added/changed

New:
- `aot/samples/AspNetCoreHello/AspNetCoreHello.csproj`
- `aot/samples/AspNetCoreHello/Program.cs`
- `aot/samples/AspNetCoreHello/aspnetcorehello.did`
- `aot/samples/AspNetCoreHello/build-and-deploy.sh`
- `aot/samples/AspNetCoreHello/ILLink.Substitutions.xml`

Modified:
- `aot/Wasp.AspNetCore/src/IcServer.cs` — upgrade-to-update for queries +
  `InitFailureMessage` diagnostic surface
- `aot/dfx.json` — `aspnetcorehello` canister entry +
  `networks.local.bind = 127.0.0.1:4944`
- `aot/tools/wasi-stub/src/main.rs` — Preview 2 component imports,
  `proc_exit` traps, `fd_prestat_get` returns `EBADF`

## Architecture proven

```
curl GET /
  → IC HTTP gateway
  → canister query http_request
  → IcServer.HttpRequestQuery thunk
  → upgrade=true (queries can't run ASP.NET pipeline in 5B instr)
  → IC gateway re-issues as update_call
  → canister update http_request_update
  → IcServer.HttpRequestUpdate thunk
  → IcServer.Dispatch
  → CandidHttp.DecodeRequest
  → BuildHttpContext (DefaultHttpContext)
  → IcSyncContext.RunUntilComplete(() => app.ProcessRequestAsync(ctx))
    → ASP.NET Core middleware pipeline
    → endpoint routing matches "/"
    → MapGet handler returns string
    → response written to httpCtx.Response.Body (MemoryStream)
  → BuildIcResponse (Candid HttpResponse)
  → Reply.Bytes
  → 200 "Hello from ASP.NET Core inside an IC canister!"
```
