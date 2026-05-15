#!/usr/bin/env bash
# Issue #54 (M2.3) deliverable. Builds, deploys, exercises the
# Counter-via-StableCell SSR sample.
set -euo pipefail

REPO=$(cd "$(dirname "$0")/../.." && pwd)
SAMPLE="$REPO/samples/RazorOnIc"
OUT_RAW="$SAMPLE/bin/Release/net10.0/wasi-wasm/publish/RazorOnIc.wasm"
OUT_RENAMED=$(mktemp -t wasp-razoronic.XXXXXX.wasm)
OUT_FINAL="$SAMPLE/RazorOnIc.canister.wasm"

# Pre-build the Mono.Cecil weaver and rewrite Microsoft.AspNetCore.Components.dll
# if the vendored copy is stale (older than the weaver or the input DLL).
# See aot/Wasp.AspNetCore/UNSUPPORTED.md > Razor SSR for what this fixes.
WEAVER_PROJ="$REPO/../shared/tools/Wasp.RenderTreeWeaver"
WEAVER="$WEAVER_PROJ/bin/Release/net10.0/render-tree-weaver"
VENDOR_DLL="$REPO/Wasp.AspNetCore/Vendor/Microsoft.AspNetCore.Components.dll"
INPUT_DLL="$REPO/../runtime/inputs/bcl/Microsoft.AspNetCore.Components.dll"

if [ ! -x "$WEAVER" ] || [ "$WEAVER_PROJ/Program.cs" -nt "$WEAVER" ]; then
  echo ">>> building Wasp.RenderTreeWeaver"
  (cd "$WEAVER_PROJ" && dotnet build -c Release > /dev/null) || { echo "weaver build failed"; exit 1; }
fi

if [ ! -f "$VENDOR_DLL" ] || [ "$INPUT_DLL" -nt "$VENDOR_DLL" ] || [ "$WEAVER" -nt "$VENDOR_DLL" ]; then
  echo ">>> weaving Microsoft.AspNetCore.Components.dll (Append* in-place fix)"
  mkdir -p "$(dirname "$VENDOR_DLL")"
  "$WEAVER" "$INPUT_DLL" "$VENDOR_DLL" || { echo "weaver run failed"; exit 1; }
fi

echo ">>> docker build (NativeAOT-LLVM wasm32-wasi)"
docker run --rm --platform linux/amd64 \
  -v "$REPO:/work" -v wasp-nuget:/nuget \
  wasp-dotnet-build:latest \
  bash -c "cd samples/RazorOnIc && dotnet build -c Release /p:IlcLlvmTarget=wasm32-wasi"

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
dfx canister create razoronic 2>/dev/null || true
dfx canister install razoronic --mode reinstall --yes --wasm "$OUT_FINAL"

ID=$(dfx canister id razoronic)
URL_BASE="http://${ID}.localhost:4944"

echo
echo ">>> acceptance tests"

PASS=0; FAIL=0
trap 'echo; echo "PASS=$PASS  FAIL=$FAIL"' EXIT

# 1. GET / returns HTML with counter
resp=$(curl -sS -i "$URL_BASE/")
st=$(printf '%s\n' "$resp" | head -1 | awk '{print $2}')
body=$(printf '%s\n' "$resp" | awk 'BEGIN{seen=0} /^\r?$/{seen=1; next} seen{print}')
if [ "$st" = "200" ] && echo "$body" | grep -qF "Counter value:" && echo "$body" | grep -qF "Bump counter"; then
  printf "  PASS  %-40s  %s\n" "GET / returns HTML + counter" "$st"
  PASS=$((PASS+1))
else
  printf "  FAIL  %-40s  status=%s\n" "GET / returns HTML + counter" "$st"
  FAIL=$((FAIL+1))
fi

# 2. Read current counter
initial=$(curl -sS "$URL_BASE/_counter-value")
echo "  >> initial counter = $initial"

# 3. Bump counter via POST
resp=$(curl -sS -i -X POST "$URL_BASE/counter/bump")
st=$(printf '%s\n' "$resp" | head -1 | awk '{print $2}')
loc=$(printf '%s\n' "$resp" | grep -i '^location:' | awk '{print $2}' | tr -d '\r\n')
if [ "$st" = "303" ] && [ "$loc" = "/" ]; then
  printf "  PASS  %-40s  %s -> %s\n" "POST /counter/bump -> 303 /" "$st" "$loc"
  PASS=$((PASS+1))
else
  printf "  FAIL  %-40s  status=%s loc=%s\n" "POST /counter/bump -> 303 /" "$st" "$loc"
  FAIL=$((FAIL+1))
fi

# 4. Counter incremented
after=$(curl -sS "$URL_BASE/_counter-value")
echo "  >> after-bump counter = $after"
if [ "$after" -gt "$initial" ]; then
  printf "  PASS  %-40s  %s -> %s\n" "Counter incremented via StableCell" "$initial" "$after"
  PASS=$((PASS+1))
else
  printf "  FAIL  %-40s  %s -> %s\n" "Counter incremented via StableCell" "$initial" "$after"
  FAIL=$((FAIL+1))
fi

# 5. /_plain control test — proves response body pipe works
resp=$(curl -sS "$URL_BASE/_plain")
if [ "$resp" = "plain text from canister" ]; then
  printf "  PASS  %-40s\n" "/_plain control endpoint"
  PASS=$((PASS+1))
else
  printf "  FAIL  %-40s  got=%s\n" "/_plain control endpoint" "$resp"
  FAIL=$((FAIL+1))
fi

# Verify Razor SSR is actually rendering (not just returning 200 with empty body).
resp=$(curl -sS "$URL_BASE/")
if echo "$resp" | grep -qF "<strong style=" && echo "$resp" | grep -qF "Razor's <code>HtmlRenderer</code>"; then
  printf "  PASS  %-40s\n" "Razor SSR renders <App> via HtmlRenderer"
  PASS=$((PASS+1))
else
  printf "  FAIL  %-40s  (response did not contain expected Razor-rendered HTML)\n" "Razor SSR renders <App>"
  FAIL=$((FAIL+1))
fi

echo
if [ "$FAIL" = "0" ]; then
  echo ">>> ALL ACCEPTANCE CHECKS PASSED"
  exit 0
else
  echo ">>> $FAIL ACCEPTANCE CHECKS FAILED"
  exit 1
fi
