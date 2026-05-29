#!/usr/bin/env bash
# Build CircuitOnIc to wasm32-wasi via the Linux ILC docker container,
# post-process (icp-publish + wasi-stub + wasm-opt), then deploy via dfx.
#
# Prereqs:
#   - dfx running locally (dfx start --clean --background)
#   - wasp-dotnet-build:latest docker image built
#   - shared/tools/icp-publish, shared/tools/wasi-stub built
#   - wasm-opt on PATH
#   - aot/Wasp.AspNetCore.Blazor.Server/Vendor/Microsoft.AspNetCore.Components.Server.dll
#     present (regenerate via:
#     dotnet run --project shared/tools/Wasp.CircuitHostWeaver
#     if stale or missing)

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$SCRIPT_DIR/../../.." && pwd)"
cd "$REPO"

VENDOR_DLL="aot/Wasp.AspNetCore.Blazor.Server/Vendor/Microsoft.AspNetCore.Components.Server.dll"
if [ ! -f "$VENDOR_DLL" ]; then
  echo "[circuitonic] regenerating vendored Components.Server.dll via weaver..."
  dotnet run --project shared/tools/Wasp.CircuitHostWeaver
fi

echo "[circuitonic] AOT-compiling for wasm32-wasi via docker..."
docker run --rm --platform linux/amd64 -v "$REPO:/work" -v wasp-nuget:/nuget \
  wasp-dotnet-build:latest \
  bash -c "cd /work/aot/samples/CircuitOnIc && dotnet build -c Release /p:IlcLlvmTarget=wasm32-wasi"

RAW=aot/samples/CircuitOnIc/bin/Release/net10.0/wasi-wasm/publish/CircuitOnIc.wasm
OUT=aot/samples/CircuitOnIc/CircuitOnIc.canister.wasm
TMP=$(mktemp -t wasp-co.XXXXXX.wasm)
TMP2=$(mktemp -t wasp-co.XXXXXX.wasm)

echo "[circuitonic] icp-publish $RAW -> $TMP..."
shared/tools/icp-publish/icp-publish.sh "$RAW" "$TMP"

echo "[circuitonic] wasi-stub $TMP -> $TMP2..."
shared/tools/wasi-stub/target/release/wasi-stub "$TMP" "$TMP2"

echo "[circuitonic] wasm-opt $TMP2 -> $OUT..."
wasm-opt -Oz --converge \
  --enable-bulk-memory \
  --enable-multivalue \
  --enable-reference-types \
  --enable-simd \
  --enable-nontrapping-float-to-int \
  --enable-sign-ext \
  "$TMP2" -o "$OUT"

echo "[circuitonic] Deploying backend canister..."
cd aot
dfx canister create circuitonic 2>/dev/null || true
dfx canister install circuitonic --mode reinstall --yes \
  --wasm samples/CircuitOnIc/CircuitOnIc.canister.wasm

CID=$(dfx canister id circuitonic)
echo "[circuitonic] Canister id: $CID"
echo "[circuitonic] Open: http://$CID.raw.localhost:4944/"
