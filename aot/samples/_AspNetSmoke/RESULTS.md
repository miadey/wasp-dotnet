# Issue #44 — AspNet smoke results: **GREEN**

## Verdict

`Microsoft.AspNetCore.App` AOT-publishes cleanly to `wasm32-wasi` via the
existing `aot/` toolchain. **No fallback to "wasp-aspnet-lite" needed.** Plan
proceeds: M0.2 (#45), M0.3 (#46), M0.4 (#47) unblocked.

## Numbers

| Metric | Value | Budget |
|---|---|---|
| `dotnet build` exit code | 0 | 0 |
| Build time | 34 seconds | — |
| IL trim warnings (IL2xxx, IL3xxx) | **0** | 0 |
| Published `.wasm` (with debug info) | 2.33 MB | < 50 MB |
| Final canister `.wasm` (after `icp-publish` + `wasi-stub`) | **1.03 MB** | < 50 MB |
| WASI imports | 13 (all standard) | covered by `wasi-stub` |
| WASI imports remaining after stub | 0 | 0 |
| Function count | 2,677 | — |
| Code section | 715 KB | — |
| Avg bytes/function | ~270 | (1M instr/func limit not at risk) |

## Sample shape

`Program.cs` exports a single `canister_query__smoke` thunk that constructs
a `WebApplication` and registers `MapGet("/")`. Never deployed or invoked —
purpose is to force the AOT compiler to keep `Microsoft.AspNetCore.App`
reachable.

```csharp
[UnmanagedCallersOnly(EntryPoint = "canister_query__smoke")]
public static void SmokeAspNet()
{
    var builder = WebApplication.CreateSlimBuilder();
    var app = builder.Build();
    app.MapGet("/", () => Results.Text("hi from AspNetSmoke"));
    GC.KeepAlive(app);
}
```

The csproj differs from `HelloWeb.csproj` by exactly one line:

```xml
<FrameworkReference Include="Microsoft.AspNetCore.App" />
```

That's it. No special trim configuration, no source-gen, no roots beyond the
default. `WebApplication.CreateSlimBuilder` does its job — the slim builder
path is genuinely AOT-clean on `wasm32-wasi`.

## WASI imports (all 13)

```
clock_time_get, environ_get, environ_sizes_get, fd_close, fd_fdstat_get,
fd_prestat_dir_name, fd_prestat_get, fd_seek, fd_write, poll_oneoff,
proc_exit, random_get, sched_yield
```

All 13 are emitted by the .NET 10 runtime during `_initialize` and are
trivially no-op'd by `aot/tools/wasi-stub` (which matches by module name
`wasi_snapshot_preview1`, not by import name — so any future imports get
auto-stubbed too).

## What this proves and what it doesn't

**Proves**:
- The `aot/` toolchain (NativeAOT-LLVM `wasm32-wasi`) can link
  `Microsoft.AspNetCore.App` without trim warnings or build errors.
- The slim builder + minimal-API path is small after trimming (1 MB final).
- WASI surface is unchanged from HelloWeb — the existing wasi-stub covers it.

**Does not yet prove** (deferred to M1.2 verification gate, #49):
- Full middleware pipeline (`UseRouting`, `UseAuthorization`) AOTs.
- DI scope resolution AOTs without trim warnings.
- Source-generated minimal-API endpoints work end-to-end.
- `MapGroup`, `MapPost` body-binding, JSON serialization AOT.
- Razor Components SSR (M2 territory).

These are the next gates and live in #48 / #49 / #51.

## Reproducing

```bash
cd /Users/miadey/dev/csharp/aot/samples/_AspNetSmoke
./build-smoke.sh
```

Requires Docker running and `wasp-dotnet-build:latest` image present.
