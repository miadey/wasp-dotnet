#!/usr/bin/env bash
# M4.S9.6 — first attempt at MVC + Razor Views on canister.
set -euo pipefail

REPO=$(cd "$(dirname "$0")/../../.." && pwd)
AOT="$REPO/aot"
SAMPLE="$AOT/samples/MvcVanilla"
OUT_RAW="$SAMPLE/bin/Release/net10.0/wasi-wasm/publish/MvcVanilla.wasm"
TMP1=$(mktemp -t wasp-mv.XXXXXX.wasm)
TMP2=$(mktemp -t wasp-mv.XXXXXX.wasm)
OUT_FINAL="$SAMPLE/MvcVanilla.canister.wasm"

# Pre-build Wasp.RenderTreeWeaver + weave Microsoft.AspNetCore.Components.dll
# if the vendored copy is stale.  (Razor view rendering depends on Components.)
WEAVER_PROJ="$REPO/shared/tools/Wasp.RenderTreeWeaver"
WEAVER="$WEAVER_PROJ/bin/Release/net10.0/render-tree-weaver"
VENDOR_DLL="$AOT/Wasp.AspNetCore/Vendor/Microsoft.AspNetCore.Components.dll"
INPUT_DLL="$REPO/runtime/inputs/bcl/Microsoft.AspNetCore.Components.dll"
if [ -f "$WEAVER_PROJ/Program.cs" ]; then
  if [ ! -x "$WEAVER" ] || [ "$WEAVER_PROJ/Program.cs" -nt "$WEAVER" ]; then
    echo ">>> building Wasp.RenderTreeWeaver"
    (cd "$WEAVER_PROJ" && dotnet build -c Release > /dev/null) || true
  fi
  if [ -f "$INPUT_DLL" ] && [ -x "$WEAVER" ]; then
    if [ ! -f "$VENDOR_DLL" ] || [ "$INPUT_DLL" -nt "$VENDOR_DLL" ] || [ "$WEAVER" -nt "$VENDOR_DLL" ]; then
      echo ">>> weaving Microsoft.AspNetCore.Components.dll"
      mkdir -p "$(dirname "$VENDOR_DLL")"
      "$WEAVER" "$INPUT_DLL" "$VENDOR_DLL" || true
    fi
  fi
fi

echo ">>> docker build (NativeAOT-LLVM wasm32-wasi)"
docker run --rm --platform linux/amd64 \
  -v "$REPO:/work" -v wasp-nuget:/nuget \
  -w /work/aot \
  wasp-dotnet-build:latest \
  bash -c "cd samples/MvcVanilla && dotnet build -c Release /p:IlcLlvmTarget=wasm32-wasi"

echo ">>> icp-publish + wasi-stub + wasm-opt"
"$REPO/shared/tools/icp-publish/icp-publish.sh" "$OUT_RAW" "$TMP1"
"$REPO/shared/tools/wasi-stub/target/release/wasi-stub" "$TMP1" "$TMP2"
wasm-opt -Oz --converge --enable-bulk-memory --enable-multivalue --enable-reference-types \
  --enable-simd --enable-nontrapping-float-to-int --enable-sign-ext \
  "$TMP2" -o "$OUT_FINAL"
rm -f "$TMP1" "$TMP2"

echo
echo ">>> sizes"
echo "raw:    $(wc -c < "$OUT_RAW") bytes"
echo "final:  $(wc -c < "$OUT_FINAL") bytes"

if ! curl -sf http://127.0.0.1:4944/api/v2/status > /dev/null 2>&1; then
  echo ">>> dfx not running on :4944. Start with: cd $AOT && dfx start --background"
  exit 2
fi

cd "$AOT"
dfx canister create mvcvanilla 2>/dev/null || true
dfx canister install mvcvanilla --mode reinstall --yes --wasm "$OUT_FINAL"

ID=$(dfx canister id mvcvanilla)
URL="http://${ID}.raw.localhost:4944"

echo
echo ">>> acceptance"
PASS=0; FAIL=0
trap 'echo; echo "PASS=$PASS FAIL=$FAIL"' EXIT

# 1. GET /
st=$(curl -s -o /tmp/mvc.idx.html -w "%{http_code}" "$URL/")
if [ "$st" = "200" ] && grep -qF "Welcome to MvcVanilla" /tmp/mvc.idx.html; then
  printf "  PASS  %-40s  %s\n" "GET / renders Home/Index" "$st"; PASS=$((PASS+1))
else
  printf "  FAIL  %-40s  status=%s\n" "GET / renders Home/Index" "$st"; FAIL=$((FAIL+1))
fi

# 2. GET /Home/Privacy
st=$(curl -s -o /tmp/mvc.priv.html -w "%{http_code}" "$URL/Home/Privacy")
if [ "$st" = "200" ] && grep -qF "Privacy Policy" /tmp/mvc.priv.html; then
  printf "  PASS  %-40s  %s\n" "GET /Home/Privacy" "$st"; PASS=$((PASS+1))
else
  printf "  FAIL  %-40s  status=%s\n" "GET /Home/Privacy" "$st"; FAIL=$((FAIL+1))
fi

# 3. GET /css/site.css
st=$(curl -s -o /tmp/mvc.css -w "%{http_code}" "$URL/css/site.css")
if [ "$st" = "200" ]; then
  printf "  PASS  %-40s  %s\n" "GET /css/site.css" "$st"; PASS=$((PASS+1))
else
  printf "  FAIL  %-40s  status=%s\n" "GET /css/site.css" "$st"; FAIL=$((FAIL+1))
fi

# 4. /_plain control endpoint
got=$(curl -s "$URL/_plain")
if [ "$got" = "plain text from MvcVanilla canister" ]; then
  printf "  PASS  %-40s\n" "/_plain control"; PASS=$((PASS+1))
else
  printf "  FAIL  %-40s  got=%s\n" "/_plain control" "$got"; FAIL=$((FAIL+1))
fi
