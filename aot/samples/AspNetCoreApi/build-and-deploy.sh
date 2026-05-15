#!/usr/bin/env bash
# Issue #51 deliverable. Build, deploy, exercise UseRouting + UseAuthorization
# + MapGroup + scoped DI.
set -euo pipefail

REPO=$(cd "$(dirname "$0")/../.." && pwd)
SAMPLE="$REPO/samples/AspNetCoreApi"
OUT_RAW="$SAMPLE/bin/Release/net10.0/wasi-wasm/publish/AspNetCoreApi.wasm"
OUT_RENAMED=$(mktemp -t wasp-aspnetapi.XXXXXX.wasm)
OUT_FINAL="$SAMPLE/AspNetCoreApi.canister.wasm"

echo ">>> docker build (NativeAOT-LLVM wasm32-wasi)"
docker run --rm --platform linux/amd64 \
  -v "$REPO:/work" -v wasp-nuget:/nuget \
  wasp-dotnet-build:latest \
  bash -c "cd samples/AspNetCoreApi && dotnet build -c Release /p:IlcLlvmTarget=wasm32-wasi"

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
  echo ">>> dfx is not running on 4944. Start it in another shell:"
  echo "    cd $REPO && dfx start --background"
  exit 2
fi

cd "$REPO"
echo
echo ">>> dfx canister create + install"
dfx canister create aspnetcoreapi 2>/dev/null || true
dfx canister install aspnetcoreapi --mode reinstall --yes --wasm "$OUT_FINAL"

ID=$(dfx canister id aspnetcoreapi)
URL_BASE="http://${ID}.localhost:4944"

echo
echo ">>> acceptance tests"

run_test() {
  local desc="$1"; shift
  local expected_status="$1"; shift
  local expected_body_substr="$1"; shift
  local resp st body
  resp=$(curl -sS -i "$@")
  st=$(printf '%s\n' "$resp" | head -1 | awk '{print $2}')
  body=$(printf '%s\n' "$resp" | awk 'BEGIN{seen=0} /^\r?$/{seen=1; next} seen{print}')
  if [ "$st" = "$expected_status" ] && (echo "$body" | grep -qF "$expected_body_substr" || [ -z "$expected_body_substr" ]); then
    printf "  PASS  %-50s  %s\n" "$desc" "$st $body" | head -c 140
    echo
    return 0
  else
    printf "  FAIL  %-50s  GOT %s body=%s\n" "$desc" "$st" "$body" | head -c 240
    echo
    return 1
  fi
}

PASS=0
FAIL=0
trap 'echo; echo "PASS=$PASS  FAIL=$FAIL"' EXIT

run_test "GET /api/echo/world         scoped IGreeter" 200  "hello world (greeter#"   "$URL_BASE/api/echo/world"  && PASS=$((PASS+1)) || FAIL=$((FAIL+1))
run_test "GET /api/scope              same-scope=True" 200  "same-scope=True"         "$URL_BASE/api/scope"       && PASS=$((PASS+1)) || FAIL=$((FAIL+1))
run_test "POST /api/save              JSON body"       200  "saved 'urgent' priority=9" \
    -X POST -H "Content-Type: application/json" --data '{"title":"urgent","priority":9}' "$URL_BASE/api/save" && PASS=$((PASS+1)) || FAIL=$((FAIL+1))
run_test "GET /api/secret  (no cookie)  -> 401"        401  ""                        "$URL_BASE/api/secret"      && PASS=$((PASS+1)) || FAIL=$((FAIL+1))
run_test "GET /api/secret  (cookie alice)"             200  "hello alice, the secret is 42" \
    -H "Cookie: user=alice" "$URL_BASE/api/secret" && PASS=$((PASS+1)) || FAIL=$((FAIL+1))

echo
if [ "$FAIL" = "0" ]; then
  echo ">>> ALL 5 ACCEPTANCE CHECKS PASSED"
  exit 0
else
  echo ">>> $FAIL OF 5 FAILED"
  exit 1
fi
