#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$SCRIPT_DIR/../../.." && pwd)"
cd "$REPO"

echo "[todoef] AOT-compiling for wasm32-wasi via docker..."
docker run --rm --platform linux/amd64 -v "$REPO:/work" -v wasp-nuget:/nuget \
  wasp-dotnet-build:latest \
  bash -c "cd /work/aot/samples/TodoEf && dotnet build -c Release /p:IlcLlvmTarget=wasm32-wasi"

RAW=aot/samples/TodoEf/bin/Release/net10.0/wasi-wasm/publish/TodoEf.wasm
OUT=aot/samples/TodoEf/TodoEf.canister.wasm
TMP=$(mktemp -t wasp-tef.XXXXXX.wasm)
TMP2=$(mktemp -t wasp-tef.XXXXXX.wasm)

echo "[todoef] icp-publish..."
shared/tools/icp-publish/icp-publish.sh "$RAW" "$TMP"
echo "[todoef] wasi-stub..."
shared/tools/wasi-stub/target/release/wasi-stub "$TMP" "$TMP2"
echo "[todoef] wasm-opt..."
wasm-opt -Oz \
  --enable-bulk-memory --enable-multivalue --enable-reference-types \
  --enable-simd --enable-nontrapping-float-to-int --enable-sign-ext \
  "$TMP2" -o "$OUT"

echo "[todoef] Deploying..."
cd aot
dfx canister create todoef 2>/dev/null || true
dfx canister install todoef --mode reinstall --yes --wasm samples/TodoEf/TodoEf.canister.wasm
echo "[todoef] http://$(dfx canister id todoef).raw.localhost:4944/"
