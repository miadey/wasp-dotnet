#!/usr/bin/env bash
# Build the Bzzz canister (social super-app: chat substrate + servers/forums/
# feeds/DMs/vetKD) to wasm32-wasi via docker, post-process (icp-publish +
# wasi-stub + wasm-opt). BUILD-ONLY by default; deploy is opt-in.
#
# This repo (bzzz) has NO Blazor WebAssembly sub-apps — the TetrisWasm/CrmWasm
# embeds were dropped when Bzzz was split out of the wasp-dotnet monorepo. So
# there is no host-side `dotnet publish` step before the docker AOT build.
#
# Usage:
#   ./build-and-deploy.sh                 # build the wasm only (default)
#   WASP_DEPLOY=local ./build-and-deploy.sh   # build + install to LOCAL replica
#   WASP_DEPLOY=ic    ./build-and-deploy.sh   # build + UPGRADE razoronic on mainnet
#                                              # (--mode upgrade; preserves stable data)

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$SCRIPT_DIR/../../.." && pwd)"
cd "$REPO"

echo "[bzzz] AOT-compiling for wasm32-wasi via docker..."
docker run --rm --platform linux/amd64 -v "$REPO:/work" -v wasp-nuget:/nuget \
  wasp-dotnet-build:latest \
  bash -c "cd /work/aot/samples/BlazorWasp && dotnet build -c Release /p:IlcLlvmTarget=wasm32-wasi"

RAW=aot/samples/BlazorWasp/bin/Release/net10.0/wasi-wasm/publish/BlazorWasp.wasm
OUT=aot/samples/BlazorWasp/BlazorWasp.canister.wasm
TMP=$(mktemp -t wasp-bz.XXXXXX.wasm)
TMP2=$(mktemp -t wasp-bz.XXXXXX.wasm)

echo "[bzzz] icp-publish $RAW -> $TMP..."
shared/tools/icp-publish/icp-publish.sh "$RAW" "$TMP"

echo "[bzzz] wasi-stub $TMP -> $TMP2..."
shared/tools/wasi-stub/target/release/wasi-stub "$TMP" "$TMP2"

echo "[bzzz] wasm-opt $TMP2 -> $OUT..."
wasm-opt -Oz --converge \
  --enable-bulk-memory \
  --enable-multivalue \
  --enable-reference-types \
  --enable-simd \
  --enable-nontrapping-float-to-int \
  --enable-sign-ext \
  "$TMP2" -o "$OUT"

echo "[bzzz] built $OUT"
ls -la "$OUT"
shasum -a 256 "$OUT"

DEPLOY="${WASP_DEPLOY:-}"
if [[ -z "$DEPLOY" ]]; then
  echo "[bzzz] build-only (set WASP_DEPLOY=local or WASP_DEPLOY=ic to deploy)."
  exit 0
fi

# Default to upgrade so deploys keep canister stable state (chat rooms +
# messages, servers, DMs, key directory). razoronic holds LIVE user data —
# NEVER reinstall it. Override with WASP_DEPLOY_MODE=reinstall only on a
# throwaway local canister.
MODE="${WASP_DEPLOY_MODE:-upgrade}"
cd aot
if [[ "$DEPLOY" == "ic" ]]; then
  echo "[bzzz] Deploying to mainnet razoronic (--mode $MODE)..."
  DFX_WARNING=-mainnet_plaintext_identity dfx canister --network ic install razoronic \
    --mode "$MODE" --yes --wasm samples/BlazorWasp/BlazorWasp.canister.wasm
  echo "[bzzz] razoronic upgraded. id: $(dfx canister --network ic id razoronic)"
else
  echo "[bzzz] Deploying to LOCAL replica (--mode $MODE)..."
  dfx canister create blazorwasp 2>/dev/null || true
  dfx canister install blazorwasp --mode "$MODE" --yes \
    --wasm samples/BlazorWasp/BlazorWasp.canister.wasm
  CID=$(dfx canister id blazorwasp)
  echo "[bzzz] Canister id: $CID"
  echo "[bzzz] Open: http://$CID.localhost:4944/"
fi
