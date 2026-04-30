#!/usr/bin/env bash
# Issue #50 deliverable. Build, deploy, exercise 6 endpoint shapes.
set -euo pipefail

REPO=$(cd "$(dirname "$0")/../.." && pwd)
SAMPLE="$REPO/samples/AspNetCoreEndpoints"
OUT_RAW="$SAMPLE/bin/Release/net10.0/wasi-wasm/publish/AspNetCoreEndpoints.wasm"
OUT_RENAMED=$(mktemp -t wasp-aspnetendpoints.XXXXXX.wasm)
OUT_FINAL="$SAMPLE/AspNetCoreEndpoints.canister.wasm"

echo ">>> docker build (NativeAOT-LLVM wasm32-wasi)"
docker run --rm --platform linux/amd64 \
  -v "$REPO:/work" -v wasp-nuget:/nuget \
  wasp-dotnet-build:latest \
  bash -c "cd samples/AspNetCoreEndpoints && dotnet build -c Release /p:IlcLlvmTarget=wasm32-wasi"

echo ">>> icp-publish + wasi-stub"
"$REPO/tools/icp-publish/icp-publish.sh" "$OUT_RAW" "$OUT_RENAMED"
"$REPO/tools/wasi-stub/target/release/wasi-stub" "$OUT_RENAMED" "$OUT_FINAL"
rm -f "$OUT_RENAMED"

echo
echo ">>> sizes"
echo "raw:    $(wc -c < "$OUT_RAW") bytes"
echo "final:  $(wc -c < "$OUT_FINAL") bytes  ($(awk "BEGIN { printf \"%.2f\", $(wc -c < "$OUT_FINAL") / 1048576 }") MB)"

if ! curl -sf http://127.0.0.1:4944/api/v2/status > /dev/null 2>&1; then
  echo
  echo ">>> dfx start --background --clean"
  cd "$REPO"
  dfx start --background --clean
  for i in 1 2 3 4 5 6 7 8 9 10; do
    if curl -sf http://127.0.0.1:4944/api/v2/status > /dev/null 2>&1; then break; fi
    sleep 1
  done
fi

cd "$REPO"
echo
echo ">>> dfx canister create + install"
dfx canister create aspnetcoreendpoints 2>/dev/null || true
dfx canister install aspnetcoreendpoints --mode reinstall --yes --wasm "$OUT_FINAL"

ID=$(dfx canister id aspnetcoreendpoints)
URL_BASE="http://${ID}.localhost:4944"

echo
echo ">>> acceptance tests"

run_test() {
  local desc="$1"; shift
  local expected_status="$1"; shift
  local expected_body_substr="$1"; shift
  local resp status body
  resp=$(curl -sS -i "$@")
  status=$(printf '%s\n' "$resp" | head -1 | awk '{print $2}')
  body=$(printf '%s\n' "$resp" | awk 'BEGIN{seen=0} /^\r?$/{seen=1; next} seen{print}')
  if [ "$status" = "$expected_status" ] && (echo "$body" | grep -qF "$expected_body_substr" || [ -z "$expected_body_substr" ]); then
    printf "  ✓  %-50s  %s\n" "$desc" "$status $body" | head -c 120
    echo
    return 0
  else
    printf "  ✗  %-50s  GOT %s body=%s\n" "$desc" "$status" "$body" | head -c 200
    echo
    return 1
  fi
}

PASS=0
FAIL=0
trap 'echo; echo "PASS=$PASS  FAIL=$FAIL"' EXIT

run_test "GET /                       string"        200  "Hello from AspNetCoreEndpoints"      "$URL_BASE/"            && PASS=$((PASS+1)) || FAIL=$((FAIL+1))
run_test "GET /echo/world             route param"   200  "echo: world"                         "$URL_BASE/echo/world"  && PASS=$((PASS+1)) || FAIL=$((FAIL+1))
run_test "POST /note                  JSON body"     200  "got note 'urgent' priority=9"        -X POST -H "Content-Type: application/json" --data '{"title":"urgent","priority":9}' "$URL_BASE/note" && PASS=$((PASS+1)) || FAIL=$((FAIL+1))
run_test "GET /async                  async/yield"   200  "async ok"                            "$URL_BASE/async"       && PASS=$((PASS+1)) || FAIL=$((FAIL+1))
run_test "GET /json                   IResult JSON"  200  "from-json"                           "$URL_BASE/json"        && PASS=$((PASS+1)) || FAIL=$((FAIL+1))
run_test "DELETE /missing             404 IResult"   404  ""                                    -X DELETE "$URL_BASE/missing" && PASS=$((PASS+1)) || FAIL=$((FAIL+1))

echo
if [ "$FAIL" = "0" ]; then
  echo ">>> ✓ ALL 6 ENDPOINT SHAPES PASSED"
  exit 0
else
  echo ">>> ✗ $FAIL OF 6 FAILED"
  exit 1
fi
