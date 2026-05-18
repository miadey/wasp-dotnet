#!/usr/bin/env bash
# Build BlazorVanilla to wasm32-wasi via the Linux ILC docker container,
# post-process (icp-publish + wasi-stub + wasm-opt), then deploy via dfx.
#
# Prereqs:
#   - dfx running locally (dfx start --clean --background)
#   - wasp-dotnet-build:latest docker image built
#   - shared/tools/icp-publish, shared/tools/wasi-stub built
#   - wasm-opt on PATH

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$SCRIPT_DIR/../../.." && pwd)"
cd "$REPO"

echo "[blazorvanilla] AOT-compiling for wasm32-wasi via docker..."
docker run --rm --platform linux/amd64 -v "$REPO:/work" -v wasp-nuget:/nuget \
  wasp-dotnet-build:latest \
  bash -c "cd /work/aot/samples/BlazorVanilla && dotnet build -c Release /p:IlcLlvmTarget=wasm32-wasi"

RAW=aot/samples/BlazorVanilla/bin/Release/net10.0/wasi-wasm/publish/BlazorVanilla.wasm
OUT=aot/samples/BlazorVanilla/BlazorVanilla.canister.wasm
TMP=$(mktemp -t wasp-bv.XXXXXX.wasm)
TMP2=$(mktemp -t wasp-bv.XXXXXX.wasm)

echo "[blazorvanilla] icp-publish $RAW -> $TMP..."
shared/tools/icp-publish/icp-publish.sh "$RAW" "$TMP"

echo "[blazorvanilla] wasi-stub $TMP -> $TMP2..."
shared/tools/wasi-stub/target/release/wasi-stub "$TMP" "$TMP2"

echo "[blazorvanilla] wasm-opt $TMP2 -> $OUT..."
wasm-opt -Oz \
  --enable-bulk-memory \
  --enable-multivalue \
  --enable-reference-types \
  --enable-simd \
  --enable-nontrapping-float-to-int \
  --enable-sign-ext \
  "$TMP2" -o "$OUT"

echo "[blazorvanilla] Deploying..."
cd aot
dfx canister create blazorvanilla 2>/dev/null || true
dfx canister install blazorvanilla --mode reinstall --yes \
  --wasm samples/BlazorVanilla/BlazorVanilla.canister.wasm

CID=$(dfx canister id blazorvanilla)
echo "[blazorvanilla] Canister id: $CID"
echo "[blazorvanilla] Open: http://$CID.raw.localhost:4944/"
