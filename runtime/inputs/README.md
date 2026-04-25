# runtime/inputs/ — Microsoft pre-built .NET WASM artifacts

These are the **inputs** to the wasp-dotnet runtime story (Phase A). All files
in this directory were produced by Microsoft's standard `dotnet publish`
pipeline for a Blazor WebAssembly project, then copied here verbatim. None
were modified.

## Files

| File | Source | SHA-256 | Size |
|---|---|---|---|
| `dotnet.native.wasm` | `aot/samples/BlazorChat/bin/Release/net10.0/publish/wwwroot/_framework/dotnet.native.4zyobjtzg3.wasm` | `106ef9eb78924da35e1413d78011e891c6c618635fd3afd466926fed3291ddcd` | 3,002,101 B (2.86 MiB) |
| `System.Private.CoreLib.dll` | `aot/samples/BlazorChat/.../System.Private.CoreLib.nocnj2g7a4.wasm` | `0965a437a06f1788f5c6bf4f011f8e30d50507c288e03b7cccc4e7ce59cf77b3` | 1,653,525 B (1.58 MiB) |
| `boot.json` | `aot/samples/BlazorChat/bin/Release/net10.0/publish/BlazorChat.staticwebassets.endpoints.json` | `ac04084b7c0a22acbc7767b458aeea8c8b36e6a85e965debd495aefe12af0f3b` | 688,370 B (672 KiB) |

Notes on the file names:

- **`dotnet.native.wasm`** is the Mono runtime engine itself. The hash suffix
  was dropped so build scripts can refer to a stable filename.
- **`System.Private.CoreLib.dll`** is named `.dll` for clarity, but on disk
  it is the *AOT-compiled webcil/wasm form* of the BCL produced by the Blazor
  toolchain (the hashed source file ends in `.wasm`). Mono accepts it as the
  corelib assembly.
- **`boot.json`** is the closest thing this Blazor 10 publish layout produces
  to the historical `blazor.boot.json` manifest — it is the
  `staticwebassets.endpoints.json` describing every published asset (dll,
  wasm, hash). Newer Blazor builds embed the boot manifest in a different
  format and no `blazor.boot.json` is emitted under `_framework/`. We keep
  this file for reference / future tooling that needs to know which
  assemblies were in the publish output.

## Build environment

- **`dotnet --version`**: `10.0.202`
- Built on macOS 25.3.0 (Darwin) / arm64.
- Standard `dotnet publish -c Release` of the BlazorChat sample.

## License

These are unmodified Microsoft binaries originating from the
[`dotnet/runtime`](https://github.com/dotnet/runtime) and
[`dotnet/aspnetcore`](https://github.com/dotnet/aspnetcore) repositories,
distributed under the **MIT License**. We redistribute them here unchanged
under that same license; the wasp-dotnet project adds no patent or copyright
claim over them.

## How to refresh

When upgrading to a newer .NET SDK or rebuilding:

```bash
# 1. Rebuild BlazorChat with the new SDK
cd /Users/miadey/dev/csharp/aot/samples/BlazorChat
dotnet publish -c Release

# 2. Locate the new hashed filenames in publish/wwwroot/_framework/
ls bin/Release/net10.0/publish/wwwroot/_framework/dotnet.native.*.wasm
ls bin/Release/net10.0/publish/wwwroot/_framework/System.Private.CoreLib.*.wasm

# 3. Re-copy with the hash dropped
cp bin/Release/net10.0/publish/wwwroot/_framework/dotnet.native.*.wasm \
   ../../../runtime/inputs/dotnet.native.wasm
cp bin/Release/net10.0/publish/wwwroot/_framework/System.Private.CoreLib.*.wasm \
   ../../../runtime/inputs/System.Private.CoreLib.dll
cp bin/Release/net10.0/publish/BlazorChat.staticwebassets.endpoints.json \
   ../../../runtime/inputs/boot.json

# 4. Re-hash and update this README
cd ../../../runtime/inputs
shasum -a 256 dotnet.native.wasm System.Private.CoreLib.dll boot.json

# 5. Re-run the env-import discovery (the import surface may have changed)
wasm-tools print dotnet.native.wasm 2>&1 | grep '(import "env"' | wc -l
# expect: 75 (as of .NET 10.0.202). If different, env_imports.rs needs updating.
```

## Import surface (as of this snapshot)

`wasm-tools print dotnet.native.wasm | grep '(import "env"'` reports **75**
imports from the `env` module. These are the functions stubbed in
`runtime/wasp_canister/src/env_imports.rs`. There are also 10 imports from
`wasi_snapshot_preview1` (handled separately in `wasi_imports.rs`).
