# Patched NativeAOT-LLVM JIT for gh #80

## Status

**Fixed**. Issue80Repro passes (all 4 variants preserve `[FieldOffset(16)]`
and `[FieldOffset(24)]` reference fields after struct-copy), and BlazorVanilla's
stock `@onclick` Counter now ticks via real render-diff (3 consecutive clicks
emit clean 160-byte `JS.RenderBatch` payloads — no more 10 MB garbage).

## What we shipped

`runtime/inputs/libclrjit_universal_wasm32_x64.patched-issue80.so` —
the LLVM JIT shared library, rebuilt from `dotnet/runtimelab` at base
commit `9d025fa5e9d07e3e93061b4457ba6279b178df98` (which matches the
`runtime.linux-x64.Microsoft.DotNet.ILCompiler.LLVM` 10.0.0-alpha.1.25162.1
package we use) **with the upstream fix cherry-picked from PR #3259
(commit `8449ba666`)**.

SHA-256: `eec1df0e9367758c5301543a9fc4940d9164d82497fd9d4b458ba13b5638a8d5`
Size: 13,194,336 bytes (vs 13,382,280 stock)
Linker: `Ubuntu clang-18 / GNU ld 18.1.3` (in `wasp-runtimelab-build` docker
image, based on `wasp-dotnet-build:latest`).

## How the patched .so is wired into the build

The wasp-dotnet build runs ILC inside the `wasp-dotnet-build:latest` docker
container with the host nuget cache mounted at `/nuget` (named volume:
`wasp-nuget`). The container's nuget cache contains the un-extracted
`runtime.linux-x64.microsoft.dotnet.ilcompiler.llvm/10.0.0-alpha.1.25162.1/tools/`
directory, which holds the original `libclrjit_universal_wasm32_x64.so`.

Replacing the JIT in the cache makes every wasm canister build use the
patched JIT:

```bash
docker run --rm --platform linux/amd64 \
  -v wasp-nuget:/nuget \
  -v "$(pwd)/runtime/inputs:/host" \
  wasp-runtimelab-build:latest \
  bash -c "cp /nuget/runtime.linux-x64.microsoft.dotnet.ilcompiler.llvm/10.0.0-alpha.1.25162.1/tools/libclrjit_universal_wasm32_x64.so \
              /nuget/runtime.linux-x64.microsoft.dotnet.ilcompiler.llvm/10.0.0-alpha.1.25162.1/tools/libclrjit_universal_wasm32_x64.so.stock && \
          cp /host/libclrjit_universal_wasm32_x64.patched-issue80.so \
             /nuget/runtime.linux-x64.microsoft.dotnet.ilcompiler.llvm/10.0.0-alpha.1.25162.1/tools/libclrjit_universal_wasm32_x64.so"
```

The `.stock` backup is preserved so the swap is reversible.

## How to rebuild from source (recipe)

If the patched .so ever needs to be regenerated (different .NET package
version, additional cherry-picks, debugging the JIT):

1. **Clone runtimelab on the `feature/NativeAOT-LLVM` branch**:
   ```bash
   git clone --depth 1 --branch feature/NativeAOT-LLVM \
     https://github.com/dotnet/runtimelab.git runtimelab
   cd runtimelab
   # Optionally pin to the commit matching our ILC package:
   git fetch --depth 1 origin 9d025fa5e9d07e3e93061b4457ba6279b178df98
   git checkout 9d025fa5e9d07e3e93061b4457ba6279b178df98
   ```

2. **Cherry-pick the fix** (if base commit pre-dates it):
   ```bash
   git fetch --depth 1 origin 8449ba666dd991e495d8363d9852f18eb86a1aa1
   # Cherry-pick may conflict — apply the storeObjAtAddress diff manually
   # against src/coreclr/jit/llvmcodegen.cpp. The fix replaces
   # bytesStored-tracking + memcpy padding fill with explicit
   # ExtractValue/CreateStore of padding fields from the SOURCE struct.
   ```

3. **Build the wasm32 JIT** inside the docker image with LLVM-18-dev,
   ninja-build, libzstd-dev, liblttng-ust-dev, libnuma-dev, libunwind-dev,
   libicu-dev, libcurl4-openssl-dev installed (Dockerfile lives at
   `runtime/inputs/Dockerfile.runtimelab-build` — see below):

   ```bash
   docker run --rm --platform linux/amd64 \
     -v /path/to/runtimelab:/work \
     -e LLVM_CMAKE_CONFIG_RELEASE=/usr/lib/llvm-18/lib/cmake/llvm \
     wasp-runtimelab-build:latest \
     bash -c "cd /work && ./build.sh clr.wasmjit -c Release"
   ```

   Output appears at
   `/work/artifacts/bin/coreclr/linux.x64.Release/libclrjit_universal_wasm32_x64.so`.
   The build takes ~3 minutes on M-class hardware (LLVM-18 is already
   provided by the system, so only ~30 wasm-jit object files need
   compiling).

4. **Swap into the nuget cache** as shown above.

## Upstream tracking

The fix is in `feature/NativeAOT-LLVM` (commit `8449ba666`, April 23 2026)
but not yet in any published `runtime.linux-x64.Microsoft.DotNet.ILCompiler.LLVM`
package that's also ABI-compatible with our managed code. The newer RC
packages (10.0.0-rc.1.26117.1 etc.) include the fix but also break
runtime helper signatures (e.g. `RhpReversePInvoke`), so dropping them
in directly causes `signature_mismatch` traps at canister startup.

Long-term: bump `Microsoft.DotNet.ILCompiler.LLVM` + companion runtime
packages in lockstep when Microsoft ships an RC matching our SDK floor,
and delete the patched .so + this doc.
