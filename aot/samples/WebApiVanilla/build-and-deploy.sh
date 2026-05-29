#!/usr/bin/env bash
# M4.S9.5 — build + deploy stock-template WebAPI canister.
set -euo pipefail

REPO=$(cd "$(dirname "$0")/../../.." && pwd)
SAMPLE="$REPO/aot/samples/WebApiVanilla"
OUT_RAW="$SAMPLE/bin/Release/net10.0/wasi-wasm/publish/WebApiVanilla.wasm"
OUT_FINAL="$SAMPLE/WebApiVanilla.canister.wasm"

echo ">>> docker build (NativeAOT-LLVM wasm32-wasi)"
docker run --rm --platform linux/amd64 \
  -v "$REPO:/work" -v wasp-nuget:/nuget \
  wasp-dotnet-build:latest \
  bash -c "cd aot/samples/WebApiVanilla && dotnet build -c Release /p:IlcLlvmTarget=wasm32-wasi"

TMP=$(mktemp -t wasp-wa.XXXXXX.wasm)
TMP2=$(mktemp -t wasp-wa.XXXXXX.wasm)

echo ">>> icp-publish + wasi-stub + wasm-opt"
"$REPO/shared/tools/icp-publish/icp-publish.sh" "$OUT_RAW" "$TMP"
"$REPO/shared/tools/wasi-stub/target/release/wasi-stub" "$TMP" "$TMP2"
wasm-opt -Oz --converge \
    --enable-bulk-memory --enable-multivalue --enable-reference-types \
    --enable-simd --enable-nontrapping-float-to-int --enable-sign-ext \
    "$TMP2" -o "$OUT_FINAL"
rm -f "$TMP" "$TMP2"

echo
echo ">>> sizes"
echo "raw:    $(wc -c < "$OUT_RAW") bytes"
echo "final:  $(wc -c < "$OUT_FINAL") bytes  ($(awk "BEGIN { printf \"%.2f\", $(wc -c < "$OUT_FINAL") / 1048576 }") MB)"

if ! curl -sf http://127.0.0.1:4944/api/v2/status > /dev/null 2>&1; then
  echo
  echo ">>> dfx is not running on 4944. Start it in another shell:"
  echo "    cd $REPO/aot && dfx start --background"
  exit 2
fi

cd "$REPO/aot"
echo
echo ">>> dfx canister create + install"
dfx canister create webapivanilla 2>/dev/null || true
dfx canister install webapivanilla --mode reinstall --yes --wasm "$OUT_FINAL"

ID=$(dfx canister id webapivanilla)
URL_BASE="http://${ID}.raw.localhost:4944"

echo
echo ">>> acceptance"
echo "GET  /weatherforecast"
curl -s -o /tmp/wf.json -w "  status=%{http_code} time=%{time_total}\n" "$URL_BASE/weatherforecast"
echo "  body=$(head -c 200 /tmp/wf.json)"
echo
echo "POST /weatherforecast"
curl -s -X POST -H "content-type: application/json" \
    -d '{"date":"2026-06-01","temperatureC":25,"summary":"Warm"}' \
    -w "  status=%{http_code} time=%{time_total}\n" \
    "$URL_BASE/weatherforecast"
