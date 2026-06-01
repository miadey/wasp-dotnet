#!/usr/bin/env bash
# Build BlazorWasp (gh #118 render-as-query) to wasm32-wasi via docker,
# post-process (icp-publish + wasi-stub + wasm-opt), then deploy via dfx.

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$SCRIPT_DIR/../../.." && pwd)"
cd "$REPO"

# Publish the Blazor WebAssembly sub-apps first on the host (the
# docker image doesn't ship the Blazor WASM SDK targets). Each
# project's publish output is picked up by BlazorWasp.csproj's
# <EmbeddedResource> glob and embedded into the canister wasm.
#
# Clean the publish dir first: `dotnet publish` does NOT remove stale
# output, so each build's fingerprinted assemblies (dotnet.native.<hash>,
# System.Private.CoreLib.<hash>, …) pile up and the embed glob bakes ALL
# of them into the wasm — ~8-9 MB of dead weight. Wipe so only the
# current build's files are embedded.
echo "[blazorwasp] publishing TetrisWasm (Blazor WebAssembly)..."
rm -rf aot/samples/TetrisWasm/bin/Release/net10.0/publish
dotnet publish aot/samples/TetrisWasm/TetrisWasm.csproj -c Release --nologo --verbosity quiet
echo "[blazorwasp] publishing CrmWasm (Blazor WebAssembly + Fluent UI)..."
rm -rf aot/samples/CrmWasm/bin/Release/net10.0/publish
dotnet publish aot/samples/CrmWasm/CrmWasm.csproj -c Release --nologo --verbosity quiet

# We embed ONLY the brotli (.br) copy of each sub-app asset (served with
# Content-Encoding: br). The csproj glob would SILENTLY DROP any file that
# lacks a .br sibling — assert none do, so a future publish change fails loud.
echo "[blazorwasp] asserting every sub-app asset has a .br sibling..."
for app in TetrisWasm:tetris CrmWasm:crm; do
  d="aot/samples/${app%%:*}/bin/Release/net10.0/publish/wwwroot/${app##*:}"
  miss=$(cd "$d" && find . -type f ! -name '*.br' ! -name '*.gz' | while read -r f; do [ -f "$f.br" ] || echo "$f"; done)
  if [ -n "$miss" ]; then echo "FATAL: files without a .br sibling in $d (would be dropped from the wasm):"; echo "$miss"; exit 1; fi
done

echo "[blazorwasp] AOT-compiling for wasm32-wasi via docker..."
docker run --rm --platform linux/amd64 -v "$REPO:/work" -v wasp-nuget:/nuget \
  wasp-dotnet-build:latest \
  bash -c "cd /work/aot/samples/BlazorWasp && dotnet build -c Release /p:IlcLlvmTarget=wasm32-wasi"

RAW=aot/samples/BlazorWasp/bin/Release/net10.0/wasi-wasm/publish/BlazorWasp.wasm
OUT=aot/samples/BlazorWasp/BlazorWasp.canister.wasm
TMP=$(mktemp -t wasp-bw.XXXXXX.wasm)
TMP2=$(mktemp -t wasp-bw.XXXXXX.wasm)

echo "[blazorwasp] icp-publish $RAW -> $TMP..."
shared/tools/icp-publish/icp-publish.sh "$RAW" "$TMP"

echo "[blazorwasp] wasi-stub $TMP -> $TMP2..."
shared/tools/wasi-stub/target/release/wasi-stub "$TMP" "$TMP2"

echo "[blazorwasp] wasm-opt $TMP2 -> $OUT..."
wasm-opt -Oz --converge \
  --enable-bulk-memory \
  --enable-multivalue \
  --enable-reference-types \
  --enable-simd \
  --enable-nontrapping-float-to-int \
  --enable-sign-ext \
  "$TMP2" -o "$OUT"

# Default to upgrade so iterative builds keep canister state (chat
# rooms + messages, pixel canvas, counter). Override with
# WASP_DEPLOY_MODE=reinstall when a schema bump requires a clean slate.
MODE="${WASP_DEPLOY_MODE:-upgrade}"
echo "[blazorwasp] Deploying (--mode $MODE)..."
cd aot
dfx canister create blazorwasp 2>/dev/null || true
dfx canister install blazorwasp --mode "$MODE" --yes \
  --wasm samples/BlazorWasp/BlazorWasp.canister.wasm

CID=$(dfx canister id blazorwasp)
echo "[blazorwasp] Canister id: $CID"
echo "[blazorwasp] Open: http://$CID.raw.localhost:4944/"
echo "[blazorwasp] Or canonical: http://$CID.localhost:4944/"
