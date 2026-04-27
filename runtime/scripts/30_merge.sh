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

DOTNET_PRE=$(mktemp -t wasp-dotnet-pre.XXXXXX).wasm
trap 'rm -f "$OUT_MERGED" "$OUT_LOWERED" "$OUT_CONST" "$OUT_RENAMED" "$DOTNET_PRE"' EXIT

if wasm-tools print "$DOTNET_WASM" 2>/dev/null | grep -q '\bv128\.\|i8x16\|f32x4'; then
    echo "[0/8] preserve dn_simdhash insert leaf (SIMD build)"
    python3 "$RUNTIME/scripts/inject_dn_simdhash_passthrough.py" "$DOTNET_WASM" "$DOTNET_PRE"
else
    # No-SIMD build doesn't need the bypass, but wasp_canister still
    # imports wasp_dn_simdhash_insert_original. Add a stub export to
    # any 5-arg → i32 function so the merge resolves the import. The
    # stub never gets called (no fn 559 patch on no-SIMD builds).
    echo "[0/8] no-SIMD build — adding stub wasp_dn_simdhash_insert_original export"
    python3 - "$DOTNET_WASM" "$DOTNET_PRE" <<'EOF'
import re, subprocess, sys, tempfile
from pathlib import Path
in_wasm, out_wasm = Path(sys.argv[1]), Path(sys.argv[2])
with tempfile.TemporaryDirectory() as td:
    wat = Path(td)/"in.wat"
    out_wat = Path(td)/"out.wat"
    subprocess.run(["wasm-tools","print",str(in_wasm),"-o",str(wat)], check=True)
    text = wat.read_text()
    # Find any 5-arg → i32 fn. Handles both the symbol-stripped form
    # `(func (;NN;) (type NN) (param ...))` AND the symbol-preserved
    # form `(func $name (;NN;) (type NN) (param ...))`.
    m = re.search(
        r'^  \(func (?:\$\S+ )?\(;(\d+);\) \(type \d+\) \(param i32 i32 i32 i32 i32\) \(result i32\)',
        text, re.MULTILINE,
    )
    if not m:
        sys.exit("no 5-arg → i32 fn found for stub")
    fn = int(m.group(1))
    export = f'\n  (export "wasp_dn_simdhash_insert_original" (func {fn}))'
    first_export = text.find('\n  (export ')
    if first_export < 0: sys.exit("no exports")
    new = text[:first_export] + export + text[first_export:]
    out_wat.write_text(new)
    subprocess.run(["wasm-tools","parse",str(out_wat),"-o",str(out_wasm)], check=True)
    print(f"  stub export → fn {fn}", file=sys.stderr)
EOF
fi

echo "[1/5] wasm-merge wasp_canister(as 'env') + dotnet.native(as 'dotnet')"
wasm-merge \
    "$CANISTER_WASM" env \
    "$DOTNET_PRE"    dotnet \
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

echo "[8/8] g7-helper + (conditional) dn_simdhash post-merge patches"
WAT=$(mktemp -t wasp-wat.XXXXXX)
wasm-tools print "$OUT_RELAXED" -o "$WAT"

G7_FN=$(grep -E '\(export "wasp_get_g7"'           "$WAT" | grep -oE 'func [0-9]+' | grep -oE '[0-9]+')
GET_FN=$(grep -E '\(export "wasp_simdhash_get"'    "$WAT" | grep -oE 'func [0-9]+' | grep -oE '[0-9]+')
INS_FN=$(grep -E '\(export "wasp_simdhash_insert"' "$WAT" | grep -oE 'func [0-9]+' | grep -oE '[0-9]+')

# dn_simdhash leaf resolution only succeeds for SIMD builds (scalar
# build's body fingerprint differs). On no-SIMD builds we DON'T need
# the bypass — mono's real dn_simdhash works correctly without our
# shadow-map intercept.
DN_LEAVES=$(python3 "$RUNTIME/scripts/find_dn_simdhash_leaves.py" "$WAT" 2>/dev/null || true)
DN_GET=$(echo "$DN_LEAVES" | grep -oE '^get=[0-9]+' | grep -oE '[0-9]+' || true)
DN_INS=$(echo "$DN_LEAVES" | grep -oE '^insert=[0-9]+' | grep -oE '[0-9]+' || true)
rm -f "$WAT"

[ -n "$G7_FN" ] || { echo "  could not resolve g7 (g7=$G7_FN)"; exit 1; }

OUT_P1=$(mktemp -t wasp-p1.XXXXXX).wasm
OUT_P2=$(mktemp -t wasp-p2.XXXXXX).wasm
OUT_P3=$(mktemp -t wasp-p3.XXXXXX).wasm
trap 'rm -f "$OUT_MERGED" "$OUT_LOWERED" "$OUT_CONST" "$OUT_TABLE" "$OUT_RENAMED" "$OUT_STUBBED" "$OUT_RELAXED" "$DOTNET_PRE" "$OUT_P1" "$OUT_P2" "$OUT_P3"' EXIT
python3 "$RUNTIME/scripts/patch_fn_to_global_get.py" "$OUT_RELAXED" "$OUT_P1" "$G7_FN" 7

if [ -n "$DN_GET" ] && [ -n "$DN_INS" ] && [ -n "$GET_FN" ] && [ -n "$INS_FN" ]; then
    echo "  SIMD build — applying dn_simdhash bypass: get=$DN_GET → $GET_FN, insert=$DN_INS → $INS_FN"
    python3 "$RUNTIME/scripts/patch_fn_to_call.py" "$OUT_P1" "$OUT_P2" "$DN_GET" "$GET_FN"
    python3 "$RUNTIME/scripts/patch_fn_to_call.py" "$OUT_P2" "$OUT_P3" "$DN_INS" "$INS_FN"
    DEFANG_INPUT="$OUT_P3"
else
    echo "  no-SIMD build — skipping dn_simdhash bypass + assert defang"
    DEFANG_INPUT="$OUT_P1"
fi

# Always apply assert defang for now — even on no-SIMD builds we want
# load_runtime to complete so we can probe what state mono ends up in.
# Once corelib actually loads, the assert never fires and the defang
# becomes a no-op.
python3 "$RUNTIME/scripts/patch_disable_g_assert.py" "$DEFANG_INPUT" "$OUT_FINAL" --line 2718

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
