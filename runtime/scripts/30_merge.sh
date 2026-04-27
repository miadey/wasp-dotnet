#!/usr/bin/env bash
# 30_merge.sh — combine wasp_canister.wasm + dotnet.native.wasm into a
# single canister wasm.
#
# Pipeline (Phase A complete, all 6 stages green):
#   1. wasm-merge          — name wasp_canister "env" so dotnet's env
#                             imports resolve; --skip-export-conflicts
#                             keeps one "memory" export
#   2. wasm-opt --multi-memory-lowering
#                          — fuse wasp_canister's memory + dotnet's
#                             memory into a single memory
#   3. wasm-const-lower    — inline extended-const data/element offsets
#   4. wasm-table-merge    — drop wasp_canister's unused table so ICP
#                             accepts the single-table requirement
#   5. icp-publish.sh      — rename `canister_<kind>__<name>` exports
#   6. wasi-stub           — no-op stubs for leftover wasi imports
#
# Output passes wasm-tools validate, dfx canister install succeeds,
# and the canister_init runs (Phase A v1.0 GREEN).

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

echo "[3/6] wasm-const-lower (inline extended-const data/element offsets)"
"$CONST_LOWER" "$OUT_LOWERED" "$OUT_CONST"

OUT_TABLE=$(mktemp -t wasp-table.XXXXXX).wasm
trap 'rm -f "$OUT_MERGED" "$OUT_LOWERED" "$OUT_CONST" "$OUT_TABLE" "$OUT_RENAMED"' EXIT

echo "[4/6] wasm-table-merge (drop unused table so ICP accepts single-table)"
"$REPO/shared/tools/wasm-table-merge/merge.py" "$OUT_CONST" "$OUT_TABLE"

echo "[5/6] icp-publish (rename canister exports to use literal space)"
"$ICP_PUBLISH" "$OUT_TABLE" "$OUT_RENAMED"

OUT_STUBBED=$(mktemp -t wasp-stubbed.XXXXXX).wasm
trap 'rm -f "$OUT_MERGED" "$OUT_LOWERED" "$OUT_CONST" "$OUT_TABLE" "$OUT_RENAMED" "$OUT_STUBBED"' EXIT

echo "[6/7] wasi-stub (replace leftover wasi imports)"
"$WASI_STUB" "$OUT_RENAMED" "$OUT_STUBBED"

OUT_RELAXED=$(mktemp -t wasp-relaxed.XXXXXX).wasm
trap 'rm -f "$OUT_MERGED" "$OUT_LOWERED" "$OUT_CONST" "$OUT_TABLE" "$OUT_RENAMED" "$OUT_STUBBED" "$OUT_RELAXED"' EXIT

echo "[7/8] wasm-relax-simd (force align=0 on every memarg via direct binary patch)"
"$REPO/shared/tools/wasm-relax-simd/relax_binary.py" "$OUT_STUBBED" "$OUT_RELAXED"

echo "[8/8] dn_simdhash + g7-helper post-merge patches"
# Locate the three function indices we need to rewrite. Indices shift
# between builds (wasm-merge re-numbers everything), so re-derive them
# from the current artifact's exports.
WAT=$(mktemp -t wasp-wat.XXXXXX)
wasm-tools print "$OUT_RELAXED" -o "$WAT"
G7_FN=$(grep -E '\(export "wasp_get_g7"'        "$WAT" | grep -oE 'func [0-9]+' | grep -oE '[0-9]+')
GET_FN=$(grep -E '\(export "wasp_simdhash_get"'    "$WAT" | grep -oE 'func [0-9]+' | grep -oE '[0-9]+')
INS_FN=$(grep -E '\(export "wasp_simdhash_insert"' "$WAT" | grep -oE 'func [0-9]+' | grep -oE '[0-9]+')
rm -f "$WAT"
[ -n "$G7_FN" ] && [ -n "$GET_FN" ] && [ -n "$INS_FN" ] || {
    echo "  could not resolve all wasp shim fn indices (g7=$G7_FN get=$GET_FN ins=$INS_FN)"
    exit 1
}
# dn_simdhash get/insert leaves are stable across builds (they live
# inside dotnet.native.wasm which we don't recompile): fn 1024 is the
# 2-arg dn_simdhash_get_value_or_default leaf, fn 559 is the 5-arg
# dn_simdhash_insert_or_replace leaf.
OUT_P1=$(mktemp -t wasp-p1.XXXXXX).wasm
OUT_P2=$(mktemp -t wasp-p2.XXXXXX).wasm
trap 'rm -f "$OUT_MERGED" "$OUT_LOWERED" "$OUT_CONST" "$OUT_TABLE" "$OUT_RENAMED" "$OUT_STUBBED" "$OUT_RELAXED" "$OUT_P1" "$OUT_P2"' EXIT
python3 "$RUNTIME/scripts/patch_fn_to_global_get.py" "$OUT_RELAXED" "$OUT_P1" "$G7_FN" 7
python3 "$RUNTIME/scripts/patch_fn_to_call.py"      "$OUT_P1"      "$OUT_P2" 1024 "$GET_FN"
python3 "$RUNTIME/scripts/patch_fn_to_call.py"      "$OUT_P2"      "$OUT_FINAL" 559 "$INS_FN"

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
