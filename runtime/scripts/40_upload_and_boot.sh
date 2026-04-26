#!/usr/bin/env bash
# 40_upload_and_boot.sh — chunk-upload corelib, all required BCL
# dependency dlls, and WaspHost.dll into the canister, then call
# boot(). After Phase B Mono needs the assemblies in heap memory
# (not include_bytes!) to avoid wasm-merge layout traps.
#
# Usage: scripts/40_upload_and_boot.sh

set -euo pipefail

REPO=$(cd "$(dirname "$0")/../.." && pwd)
RUNTIME=$REPO/runtime
INPUTS=$RUNTIME/inputs

CORELIB=$INPUTS/System.Private.CoreLib.dll
BCL_DIR=$INPUTS/bcl_extracted
WASPHOST=$INPUTS/WaspHost.dll

[ -f "$CORELIB" ]   || { echo "missing $CORELIB"   >&2; exit 1; }
[ -d "$BCL_DIR" ]   || { echo "missing $BCL_DIR (re-run scripts/05_extract_bcl.sh)" >&2; exit 1; }
[ -f "$WASPHOST" ]  || { echo "missing $WASPHOST"  >&2; exit 1; }

cd "$RUNTIME"

# Build --name/--file pairs in deterministic load order:
#   1. corelib first
#   2. BCL dependencies (alpha-sorted)
#   3. user app dll last
ARGS=(--name "System.Private.CoreLib.dll" --file "$CORELIB")
for f in $(ls "$BCL_DIR"/*.dll | sort); do
    ARGS+=(--name "$(basename "$f")" --file "$f")
done
ARGS+=(--name "WaspHost.dll" --file "$WASPHOST")

exec python3 "$REPO/runtime/scripts/upload_and_boot.py" "${ARGS[@]}"
