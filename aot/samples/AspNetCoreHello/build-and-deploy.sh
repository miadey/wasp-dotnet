#!/usr/bin/env bash
# One-shot build + post-process + deploy for AspNetCoreHello.
#
# Acceptance for issue #47: curl http://<canister-id>.localhost:<port>/ returns
# the literal "Hello from ASP.NET Core inside an IC canister!" string.
#
# Note on port: this project's dfx.json overrides the local network bind to
# 127.0.0.1:4944 so it doesn't collide with another dfx replica on 4943.
# See aot/dfx.json `networks.local.bind`.
set -euo pipefail

REPO=$(cd "$(dirname "$0")/../.." && pwd)
SAMPLE="$REPO/samples/AspNetCoreHello"
OUT_RAW="$SAMPLE/bin/Release/net10.0/wasi-wasm/publish/AspNetCoreHello.wasm"
OUT_RENAMED=$(mktemp -t wasp-aspnethello.XXXXXX.wasm)
OUT_FINAL="$SAMPLE/AspNetCoreHello.canister.wasm"

echo ">>> docker build (NativeAOT-LLVM wasm32-wasi)"
docker run --rm --platform linux/amd64 \
  -v "$REPO:/work" -v wasp-nuget:/nuget \
  wasp-dotnet-build:latest \
  bash -c "cd samples/AspNetCoreHello && dotnet build -c Release /p:IlcLlvmTarget=wasm32-wasi"

echo ">>> icp-publish (rename canister_query__name → 'canister_query name')"
"$REPO/tools/icp-publish/icp-publish.sh" "$OUT_RAW" "$OUT_RENAMED"

echo ">>> wasi-stub (no-op leftover wasi imports)"
"$REPO/tools/wasi-stub/target/release/wasi-stub" "$OUT_RENAMED" "$OUT_FINAL"
rm -f "$OUT_RENAMED"

echo
echo ">>> exports (sanity check)"
wasm-tools print "$OUT_FINAL" | grep -E '\(export "canister' || true

echo
echo ">>> sizes"
echo "raw:    $(wc -c < "$OUT_RAW") bytes"
echo "final:  $(wc -c < "$OUT_FINAL") bytes  ($(awk "BEGIN { printf \"%.2f\", $(wc -c < "$OUT_FINAL") / 1048576 }") MB)"

# Ensure dfx is running on 4944. The project's dfx.json fixes the bind, so
# `dfx start --background` (no --host flag) picks it up automatically.
if ! curl -sf http://127.0.0.1:4944/api/v2/status > /dev/null 2>&1; then
  echo
  echo ">>> dfx start --background (network local → 127.0.0.1:4944)"
  cd "$REPO"
  dfx start --background --clean
  # Give the replica a moment to come up.
  for i in 1 2 3 4 5 6 7 8 9 10; do
    if curl -sf http://127.0.0.1:4944/api/v2/status > /dev/null 2>&1; then break; fi
    sleep 1
  done
fi

cd "$REPO"
echo
echo ">>> dfx canister create + install"
dfx canister create aspnetcorehello 2>/dev/null || true
dfx canister install aspnetcorehello --mode reinstall --yes --wasm "$OUT_FINAL"

ID=$(dfx canister id aspnetcorehello)
echo
echo "deployed: $(shasum -a 256 "$OUT_FINAL" | cut -d' ' -f1)"
echo "canister id: $ID"
echo
echo ">>> acceptance test — curl /"
echo
URL="http://${ID}.localhost:4944/"
RESP=$(curl -sS "$URL" 2>&1)
echo "GET $URL"
echo "    $RESP"
echo
EXPECTED="Hello from ASP.NET Core inside an IC canister!"
if [ "$RESP" = "$EXPECTED" ]; then
  echo ">>> ✓ ACCEPTANCE PASSED"
  exit 0
else
  echo ">>> ✗ ACCEPTANCE FAILED — expected: $EXPECTED"
  exit 1
fi
