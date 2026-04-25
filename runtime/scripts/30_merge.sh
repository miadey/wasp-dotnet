#!/usr/bin/env bash
# 30_merge.sh — combine wasp_canister.wasm + dotnet.native.wasm into a
# single canister wasm.
#
# Pipeline (Phase A v0.3 — what the e2e attempts have validated):
#   1. wasm-merge --skip-export-conflicts
#        — name wasp_canister "env" so dotnet.native's env imports
#          resolve against our exports (else they stay unresolved)
#        — --skip-export-conflicts drops dotnet.native's "memory"
#          export, keeping wasp_canister's
#        — --all-features so dotnet's bulk-memory / sign-ext / etc.
#          pass binaryen's validator
#   2. wasm-opt --multi-memory-lowering
#        — fuses wasp_canister's memory(0) + dotnet's memory(1) into
#          a single memory ICP's wasmtime accepts
#   3. wasm-const-lower
#        — replaces extended-const data/element offsets like
#          (offset global.get $g i32.const N i32.add) with literal
#          (offset i32.const initial[$g]+N). ICP wasmtime doesn't
#          accept the wasm extended-const proposal yet.
#   4. icp-publish.sh
#        — rename `canister_<kind>__<name>` → "canister_<kind> <name>"
#   5. wasi-stub
#        — replace the 10 leftover wasi_snapshot_preview1 imports with
#          no-ops / debug_print routes
#
# CURRENT BLOCKER (issue #34): even after all 5 stages above, ICP
# rejects the canister with "table count too high at 2" — wasm-merge
# can't combine the two function tables and binaryen has no
# multi-table-lowering pass. Solving that closes #11.

set -euo pipefail

REPO=$(cd "$(dirname "$0")/../.." && pwd)
RUNTIME=$REPO/runtime

CANISTER_WASM=$RUNTIME/wasp_canister/target/wasm32-unknown-unknown/release/wasp_canister.wasm
DOTNET_WASM=$RUNTIME/inputs/dotnet.native.wasm

OUT_DIR=$RUNTIME/wasp_canister
OUT_FINAL=$OUT_DIR/canister.wasm

OUT_MERGED=$(mktemp -t wasp-merged.XXXXXX).wasm
OUT_LOWERED=$(mktemp -t wasp-lowered.XXXXXX).wasm
OUT_CONST=$(mktemp -t wasp-const.XXXXXX).wasm
OUT_RENAMED=$(mktemp -t wasp-renamed.XXXXXX).wasm
trap 'rm -f "$OUT_MERGED" "$OUT_LOWERED" "$OUT_CONST" "$OUT_RENAMED"' EXIT

# ---- preflight ----------------------------------------------------------

[ -f "$CANISTER_WASM" ] || { echo "missing $CANISTER_WASM — run scripts/20_build_canister.sh first" >&2; exit 1; }
[ -f "$DOTNET_WASM" ]   || { echo "missing $DOTNET_WASM — run scripts/10_publish_app.sh first" >&2; exit 1; }

command -v wasm-merge >/dev/null || { echo "wasm-merge not on PATH (brew install binaryen)" >&2; exit 1; }
command -v wasm-opt   >/dev/null || { echo "wasm-opt not on PATH (brew install binaryen)" >&2; exit 1; }
command -v wasm-tools >/dev/null || { echo "wasm-tools not on PATH" >&2; exit 1; }
command -v python3    >/dev/null || { echo "python3 not on PATH" >&2; exit 1; }

ICP_PUBLISH=$REPO/shared/tools/icp-publish/icp-publish.sh
WASI_STUB=$REPO/shared/tools/wasi-stub/target/release/wasi-stub
CONST_LOWER=$REPO/shared/tools/wasm-const-lower/lower.py
[ -x "$ICP_PUBLISH" ] || { echo "missing or non-exec: $ICP_PUBLISH" >&2; exit 1; }
[ -x "$WASI_STUB" ]   || { echo "missing $WASI_STUB — build it with: cargo build --release --manifest-path $REPO/shared/tools/wasi-stub/Cargo.toml" >&2; exit 1; }
[ -x "$CONST_LOWER" ] || { echo "missing or non-exec: $CONST_LOWER" >&2; exit 1; }

echo "[1/5] wasm-merge wasp_canister(as 'env') + dotnet.native(as 'dotnet')"
wasm-merge \
    "$CANISTER_WASM" env \
    "$DOTNET_WASM"   dotnet \
    -o "$OUT_MERGED" \
    --all-features \
    --skip-export-conflicts

echo "[2/5] wasm-opt --multi-memory-lowering"
wasm-opt "$OUT_MERGED" -o "$OUT_LOWERED" \
    --multi-memory-lowering \
    --all-features

echo "[3/5] wasm-const-lower (inline extended-const data/element offsets)"
"$CONST_LOWER" "$OUT_LOWERED" "$OUT_CONST"

echo "[4/5] icp-publish (rename canister exports to use literal space)"
"$ICP_PUBLISH" "$OUT_CONST" "$OUT_RENAMED"

echo "[5/5] wasi-stub (replace leftover wasi imports)"
"$WASI_STUB" "$OUT_RENAMED" "$OUT_FINAL"

# ---- report -------------------------------------------------------------

SIZE=$(wc -c < "$OUT_FINAL" | tr -d ' ')
echo
echo "merged canister: ${SIZE} bytes → $OUT_FINAL"
echo
echo "imports remaining (should be ic0 only):"
wasm-tools print "$OUT_FINAL" | grep -oE '\(import "[^"]+"' | sort | uniq -c
echo
echo "canister_* exports:"
wasm-tools print "$OUT_FINAL" | grep -E '^\s*\(export "canister' || echo "  (none)"
echo
if wasm-tools validate "$OUT_FINAL" 2>/tmp/wasp-validate.err; then
    echo "✓ wasm-tools validate: VALID"
else
    echo "✗ wasm-tools validate failed:"
    cat /tmp/wasp-validate.err | head -3
fi
