#!/usr/bin/env bash
# Build BlazorWasp (gh #118 render-as-query) to wasm32-wasi via docker,
# post-process (icp-publish + wasi-stub + wasm-opt), then deploy via dfx.

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$SCRIPT_DIR/../../.." && pwd)"
cd "$REPO"

echo "[blazorwasp] AOT-compiling for wasm32-wasi via docker..."
docker run --rm --platform linux/amd64 -v "$REPO:/work" -v wasp-nuget:/nuget \
  wasp-dotnet-build:latest \
  bash -c "cd /work/aot/samples/BlazorWasp && dotnet build -c Release /p:IlcLlvmTarget=wasm32-wasi"

RAW=aot/samples/BlazorWasp/bin/Release/net10.0/wasi-wasm/publish/BlazorWasp.wasm
OUT=aot/samples/BlazorWasp/BlazorWasp.canister.wasm
TMP=$(mktemp -t wasp-bw.XXXXXX.wasm)
TMP2=$(mktemp -t wasp-bw.XXXXXX.wasm)

echo "[blazorwasp] icp-publish $RAW -> $TMP..."
shared/tools/icp-publish/icp-publish.sh "$RAW" "$TMP"

echo "[blazorwasp] wasi-stub $TMP -> $TMP2..."
shared/tools/wasi-stub/target/release/wasi-stub "$TMP" "$TMP2"

echo "[blazorwasp] wasm-opt $TMP2 -> $OUT..."
wasm-opt -Oz \
  --enable-bulk-memory \
  --enable-multivalue \
  --enable-reference-types \
  --enable-simd \
  --enable-nontrapping-float-to-int \
  --enable-sign-ext \
  "$TMP2" -o "$OUT"

echo "[blazorwasp] Deploying..."
cd aot
dfx canister create blazorwasp 2>/dev/null || true
dfx canister install blazorwasp --mode reinstall --yes \
  --wasm samples/BlazorWasp/BlazorWasp.canister.wasm

CID=$(dfx canister id blazorwasp)
echo "[blazorwasp] Canister id: $CID"
echo "[blazorwasp] Open: http://$CID.raw.localhost:4944/"
echo "[blazorwasp] Or canonical: http://$CID.localhost:4944/"
