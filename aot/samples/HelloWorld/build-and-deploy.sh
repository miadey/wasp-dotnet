#!/usr/bin/env bash
# One-shot build + post-process + deploy for HelloWorld.
# Run from the repo root: ./samples/HelloWorld/build-and-deploy.sh
set -euo pipefail

REPO=$(cd "$(dirname "$0")/../.." && pwd)
SAMPLE="$REPO/samples/HelloWorld"
OUT_RAW="$SAMPLE/bin/Release/net10.0/wasi-wasm/publish/HelloWorld.wasm"
OUT_RENAMED=$(mktemp -t wasp-renamed.XXXXXX.wasm)
OUT_FINAL="$SAMPLE/HelloWorld.canister.wasm"

# 1. Build inside Linux x64 container (NativeAOT-LLVM has no Mac arm64 host).
docker run --rm --platform linux/amd64 \
  -v "$REPO:/work" -v wasp-nuget:/nuget \
  wasp-dotnet-build:latest \
  bash -c "cd samples/HelloWorld && dotnet build -c Release /p:IlcLlvmTarget=wasm32-wasi"

# 2. Rename canister_<kind>__<name> exports to use a literal space.
"$REPO/tools/icp-publish/icp-publish.sh" "$OUT_RAW" "$OUT_RENAMED"

# 3. Replace WASI imports with no-op stubs (IC has no wasi_snapshot_preview1).
"$REPO/tools/wasi-stub/target/release/wasi-stub" "$OUT_RENAMED" "$OUT_FINAL"
rm -f "$OUT_RENAMED"

# 4. Reinstall — explicit --wasm avoids dfx's stale-upload behavior.
cd "$REPO"
dfx canister install hello --mode reinstall --yes --wasm "$OUT_FINAL"

echo
echo "deployed: $(shasum -a 256 "$OUT_FINAL" | cut -d' ' -f1)"
echo
echo "exports:"
wasm-tools print "$OUT_FINAL" | grep -E '^\s*\(export "canister'
