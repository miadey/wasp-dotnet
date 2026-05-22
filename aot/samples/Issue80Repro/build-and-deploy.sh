#!/usr/bin/env bash
# One-shot build + post-process + deploy for Issue80Repro.
set -euo pipefail

REPO=$(cd "$(dirname "$0")/../.." && pwd)
SAMPLE="$REPO/samples/Issue80Repro"
OUT_RAW="$SAMPLE/bin/Release/net10.0/wasi-wasm/publish/Issue80Repro.wasm"
OUT_RENAMED=$(mktemp -t wasp-issue80repro.XXXXXX.wasm)
OUT_FINAL="$SAMPLE/Issue80Repro.canister.wasm"

docker run --rm --platform linux/amd64 \
  -v "$REPO:/work" -v wasp-nuget:/nuget \
  wasp-dotnet-build:latest \
  bash -c "cd samples/Issue80Repro && dotnet build -c Release /p:IlcLlvmTarget=wasm32-wasi"

"$REPO/tools/icp-publish/icp-publish.sh" "$OUT_RAW" "$OUT_RENAMED"
"$REPO/tools/wasi-stub/target/release/wasi-stub" "$OUT_RENAMED" "$OUT_FINAL"
rm -f "$OUT_RENAMED"

cd "$REPO"
dfx canister install issue80repro --mode reinstall --yes --wasm "$OUT_FINAL"

echo
echo "deployed: $(shasum -a 256 "$OUT_FINAL" | cut -d' ' -f1)"
echo
echo "exports:"
wasm-tools print "$OUT_FINAL" | grep -E '^\s*\(export "canister'
echo
ID=$(dfx canister id issue80repro)
echo "Try in your browser:"
echo "  http://${ID}.localhost:4943/"
echo "  http://${ID}.localhost:4943/hello"
echo "  http://${ID}.localhost:4943/count"
echo "  http://${ID}.localhost:4943/bump"
