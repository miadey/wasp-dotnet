#!/usr/bin/env bash
set -euo pipefail

REPO=$(cd "$(dirname "$0")/../.." && pwd)
SAMPLE="$REPO/samples/HelloFetch"
OUT_RAW="$SAMPLE/bin/Release/net10.0/wasi-wasm/publish/HelloFetch.wasm"
OUT_RENAMED=$(mktemp -t wasp-hellofetch.XXXXXX.wasm)
OUT_FINAL="$SAMPLE/HelloFetch.canister.wasm"

docker run --rm --platform linux/amd64 \
  -v "$REPO:/work" -v wasp-nuget:/nuget \
  wasp-dotnet-build:latest \
  bash -c "cd samples/HelloFetch && dotnet build -c Release /p:IlcLlvmTarget=wasm32-wasi" >/dev/null

"$REPO/tools/icp-publish/icp-publish.sh" "$OUT_RAW" "$OUT_RENAMED"
"$REPO/tools/wasi-stub/target/release/wasi-stub" "$OUT_RENAMED" "$OUT_FINAL"
rm -f "$OUT_RENAMED"

cd "$REPO"
dfx canister install hellofetch --mode reinstall --yes --wasm "$OUT_FINAL"

echo
echo "deployed: $(shasum -a 256 "$OUT_FINAL" | cut -d' ' -f1)"
echo
echo "exports:"
wasm-tools print "$OUT_FINAL" | grep -E '^\s*\(export "(canister|wasp_outcall)'
