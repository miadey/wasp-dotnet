#!/usr/bin/env bash
# Build CircuitOnIc to wasm32-wasi via the Linux ILC docker container,
# then deploy both canisters (backend + assets) via dfx.
#
# Prereqs:
#   - dfx running locally (dfx start --clean --background)
#   - aot/docker/Dockerfile built and tagged as wasp-build
#   - The Cecil weaver has produced
#     aot/Wasp.AspNetCore.Blazor.Server/Vendor/Microsoft.AspNetCore.Components.Server.dll
#     (run: dotnet run --project shared/tools/Wasp.CircuitHostWeaver)
#
# Status: as of 2026-05-16, CircuitHubFacade.StartCircuit calls
# CircuitFactory.CreateCircuitHostAsync with an empty ComponentDescriptor
# list. The circuit opens; no components render until
# ServerComponentDeserializer wiring lands. See docs/m4-progress.md.

set -euo pipefail
cd "$(dirname "$0")/../.."

echo "[circuitonic] AOT-compiling for wasi-wasm via docker..."
docker run --rm -v "$PWD:/src" -w /src wasp-build \
  bash -c "cd aot/samples/CircuitOnIc && dotnet publish -c Release -r wasi-wasm"

echo "[circuitonic] Post-link (transplant the wasm-relax-simd / wasm-table-merge / wasi-stub steps from RazorOnIc/build-and-deploy.sh once the AOT step succeeds)..."

echo "[circuitonic] dfx deploy circuitonic..."
dfx deploy circuitonic

BACKEND_ID=$(dfx canister id circuitonic)
echo "[circuitonic] Backend canister id: $BACKEND_ID"

echo "[circuitonic] Rebuilding asset-canister bootstrap with backend id..."
cd aot/samples/CircuitOnIc
dotnet build -c Release \
  -p:WaspAssetCanisterBackendId="$BACKEND_ID" \
  -p:WaspAssetCanisterGateway="wss://gateway.icws.io"

cd ../../..
echo "[circuitonic] dfx deploy circuitonic_assets..."
dfx deploy circuitonic_assets

ASSETS_ID=$(dfx canister id circuitonic_assets)
echo
echo "Both canisters deployed:"
echo "  backend (CircuitHost): $BACKEND_ID"
echo "  frontend (assets):     $ASSETS_ID"
echo
echo "Open: http://$ASSETS_ID.localhost:4944/"
