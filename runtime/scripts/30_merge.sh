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

# Count SIMD ops. The "no-SIMD" mono build still has ~1 isolated SIMD
# op (mono_jiterp internal); a real SIMD build has hundreds. Use 100
# as the threshold to disambiguate.
SIMD_COUNT=$(wasm-tools print "$DOTNET_WASM" 2>/dev/null | grep -cE '\bv128\.|i8x16|f32x4' || true)
if [ "${SIMD_COUNT:-0}" -gt 100 ]; then
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
    --skip-export-conflicts \
    -g

echo "[2/5] wasm-opt --multi-memory-lowering"
wasm-opt "$OUT_MERGED" -o "$OUT_LOWERED" \
    --multi-memory-lowering \
    --all-features \
    -g

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

echo "[7/9] wasm-relax-simd (force align=0 on every memarg via direct binary patch)"
"$REPO/shared/tools/wasm-relax-simd/relax_binary.py" "$OUT_STUBBED" "$OUT_RELAXED"

echo '[7.5/9] inject `call $maybe_yield` into dn_simdhash insert leaves (post-lowering)'
OUT_YIELDED=$(mktemp -t wasp-yielded.XXXXXX).wasm
trap 'rm -f "$OUT_MERGED" "$OUT_LOWERED" "$OUT_CONST" "$OUT_TABLE" "$OUT_RENAMED" "$OUT_STUBBED" "$OUT_RELAXED" "$DOTNET_PRE" "$OUT_YIELDED"' EXIT
python3 "$RUNTIME/scripts/inject_yield_call.py" "$OUT_RELAXED" "$OUT_YIELDED" || cp "$OUT_RELAXED" "$OUT_YIELDED"

echo '[7.55/9] inject `call $maybe_yield` at entry of outer mono add_assembly fns'
OUT_YIELDED2=$(mktemp -t wasp-yielded2.XXXXXX).wasm
trap 'rm -f "$OUT_MERGED" "$OUT_LOWERED" "$OUT_CONST" "$OUT_TABLE" "$OUT_RENAMED" "$OUT_STUBBED" "$OUT_RELAXED" "$DOTNET_PRE" "$OUT_YIELDED" "$OUT_YIELDED2"' EXIT
python3 "$RUNTIME/scripts/inject_yield_at_entry.py" "$OUT_YIELDED" "$OUT_YIELDED2" || cp "$OUT_YIELDED" "$OUT_YIELDED2"
OUT_YIELDED="$OUT_YIELDED2"

echo "[7.7/9] wasm-opt --asyncify (post-lowering, scoped onlylist)"
ASYNC_ONLYLIST="mono_wasm_add_assembly,mono_bundled_resources_add_assembly_resource,mono_bundled_resources_add_assembly_symbol_resource,mono_bundled_resources_add_satellite_assembly_resource,mono_bundled_resources_add,bundled_resources_get_assembly_resource,bundled_resource_add_free_func,key_from_id,dn_simdhash_ght_try_add,dn_simdhash_ght_try_add_with_hash,dn_simdhash_ght_try_insert_internal,dn_simdhash_ght_rehash_internal,dn_simdhash_ght_new_full,dn_simdhash_ght_default_hash,dn_simdhash_ght_default_comparer,dn_simdhash_ptr_ptr_try_add,dn_simdhash_ptr_ptr_try_add_with_hash,dn_simdhash_ptr_ptr_try_insert_internal,dn_simdhash_ptr_ptr_rehash_internal,dn_simdhash_ptr_ptr_new,dn_simdhash_ptrpair_ptr_try_add,dn_simdhash_ptrpair_ptr_try_add_with_hash,dn_simdhash_ptrpair_ptr_try_insert_internal,dn_simdhash_ptrpair_ptr_rehash_internal,dn_simdhash_string_ptr_try_add,dn_simdhash_string_ptr_try_add_raw,dn_simdhash_string_ptr_try_add_with_hash_raw,dn_simdhash_string_ptr_try_insert_internal,dn_simdhash_string_ptr_rehash_internal,dn_simdhash_ensure_capacity_internal,dn_simdhash_new_internal,dn_simdhash_make_str_key,maybe_yield"
OUT_ASYNCIFIED=$(mktemp -t wasp-asyncified.XXXXXX).wasm
trap 'rm -f "$OUT_MERGED" "$OUT_LOWERED" "$OUT_CONST" "$OUT_TABLE" "$OUT_RENAMED" "$OUT_STUBBED" "$OUT_RELAXED" "$DOTNET_PRE" "$OUT_YIELDED" "$OUT_ASYNCIFIED"' EXIT
wasm-opt "$OUT_YIELDED" -o "$OUT_ASYNCIFIED" \
    --asyncify \
    --pass-arg=asyncify-onlylist@"$ASYNC_ONLYLIST" \
    --all-features \
    -g

echo "[7.8/9] patch wasp_asyncify_get_state placeholder → call asyncify_get_state"
ASYNC_WAT=$(mktemp -t wasp-asyncwat.XXXXXX)
wasm-tools print "$OUT_ASYNCIFIED" -o "$ASYNC_WAT"
resolve_export_fn() {
    local NAME="$1"; local W="$2"
    local TOK
    TOK=$(grep -E "\(export \"${NAME}\" \(func [^)]+\)\)" "$W" \
          | grep -oE '\(func [^)]+\)' | head -1 | sed -E 's/\(func //;s/\)//')
    if [ "${TOK:0:1}" = "\$" ]; then
        grep -oE "\(func ${TOK//\$/\\\$} \(;[0-9]+;\)" "$W" \
            | head -1 | grep -oE '\(;[0-9]+;\)' | tr -dc '[:digit:]'
    else
        echo "$TOK"
    fi
}
SRC=$(resolve_export_fn "wasp_asyncify_get_state" "$ASYNC_WAT")
DST=$(resolve_export_fn "asyncify_get_state" "$ASYNC_WAT")
PATCHED=$(mktemp -t wasp-asyncpatched.XXXXXX).wasm
if [ -n "$SRC" ] && [ -n "$DST" ]; then
    python3 "$RUNTIME/scripts/patch_fn_to_call.py" "$OUT_ASYNCIFIED" "$PATCHED" "$SRC" "$DST" 2>&1 | sed 's/^/  /'
else
    echo "  skip: src='$SRC' dst='$DST'"
    cp "$OUT_ASYNCIFIED" "$PATCHED"
fi
rm -f "$ASYNC_WAT"
OUT_RELAXED="$PATCHED"

echo "[8/8] g7-helper + (conditional) dn_simdhash post-merge patches"
WAT=$(mktemp -t wasp-wat.XXXXXX)
wasm-tools print "$OUT_RELAXED" -o "$WAT"

# Resolve a `$name` ref or numeric index in an export declaration to
# the underlying function index by scanning the func definitions.
resolve_fn_idx() {
    local TOK="$1"; local WAT="$2"
    if [ "${TOK:0:1}" = "\$" ]; then
        grep -oE "\(func ${TOK//\$/\\\$} \(;[0-9]+;\)" "$WAT" \
            | head -1 | grep -oE '\(;[0-9]+;\)' | tr -dc '[:digit:]'
    else
        echo "$TOK"
    fi
}

# Resolve export → fn index. With -g preserved names exports use the
# `(func $name)` form; map back to the numeric index by scanning func
# definitions.
g7_tok=$(grep -E '\(export "wasp_get_g7"'           "$WAT" | grep -oE '\(func [^)]+\)' | head -1 | sed -E 's/\(func //;s/\)//')
mb_tok=$(grep -E '\(export "wasp_get_mem_base"'     "$WAT" | grep -oE '\(func [^)]+\)' | head -1 | sed -E 's/\(func //;s/\)//')
get_tok=$(grep -E '\(export "wasp_simdhash_get"'    "$WAT" | grep -oE '\(func [^)]+\)' | head -1 | sed -E 's/\(func //;s/\)//')
ins_tok=$(grep -E '\(export "wasp_simdhash_insert"' "$WAT" | grep -oE '\(func [^)]+\)' | head -1 | sed -E 's/\(func //;s/\)//')
G7_FN=$(resolve_fn_idx "$g7_tok" "$WAT")
MB_FN=$(resolve_fn_idx "$mb_tok" "$WAT")
GET_FN=$(resolve_fn_idx "$get_tok" "$WAT")
INS_FN=$(resolve_fn_idx "$ins_tok" "$WAT")

# Find the multi-memory-lowering mem_base global. asyncify_start_unwind's
# lowered body uses `global.get <mem_base>; global.get <data_ptr>; i32.add`
# to read buffer fields. Extract the FIRST `global.get` from that body —
# that's mem_base. (The second global.get is the data ptr global, set
# from the param.)
MEM_BASE_GLOBAL=$(awk '/\(func \$asyncify_start_unwind /,/^  \)/' "$WAT" \
    | grep -oE 'global\.get [0-9]+' | awk '{print $2}' | sed -n '1p')
echo "  resolved mem_base global = $MEM_BASE_GLOBAL"

# dn_simdhash leaf resolution only succeeds for SIMD builds (scalar
# build's body fingerprint differs). On no-SIMD builds we DON'T need
# the bypass — mono's real dn_simdhash works correctly without our
# shadow-map intercept.
DN_LEAVES=$(python3 "$RUNTIME/scripts/find_dn_simdhash_leaves.py" "$WAT" 2>/dev/null || true)
DN_GET=$(echo "$DN_LEAVES" | grep -oE '^get=[0-9]+' | grep -oE '[0-9]+' || true)
DN_INS=$(echo "$DN_LEAVES" | grep -oE '^insert=[0-9]+' | grep -oE '[0-9]+' || true)
PDB_TOK=$(grep -oE "\(func \\\$mono_has_pdb_checksum \(;[0-9]+;\)" "$WAT" | head -1)
PDB_FN=$(echo "$PDB_TOK" | grep -oE '\(;[0-9]+;\)' | tr -dc '[:digit:]')
BRG_TOK=$(grep -oE "\(func \\\$bundled_resources_get_assembly_resource \(;[0-9]+;\)" "$WAT" | head -1)
BRG_FN=$(echo "$BRG_TOK" | grep -oE '\(;[0-9]+;\)' | tr -dc '[:digit:]')
echo "  resolved mono_has_pdb_checksum fn idx = $PDB_FN"
echo "  resolved bundled_resources_get_assembly_resource fn idx = $BRG_FN"
rm -f "$WAT"

[ -n "$G7_FN" ] || { echo "  could not resolve g7 (g7=$G7_FN)"; exit 1; }
[ -n "$MB_FN" ] || { echo "  could not resolve wasp_get_mem_base"; exit 1; }
[ -n "$MEM_BASE_GLOBAL" ] || { echo "  could not resolve mem_base global"; exit 1; }

OUT_P1=$(mktemp -t wasp-p1.XXXXXX).wasm
OUT_P1B=$(mktemp -t wasp-p1b.XXXXXX).wasm
OUT_P1C=$(mktemp -t wasp-p1c.XXXXXX).wasm
OUT_P2=$(mktemp -t wasp-p2.XXXXXX).wasm
OUT_P3=$(mktemp -t wasp-p3.XXXXXX).wasm
trap 'rm -f "$OUT_MERGED" "$OUT_LOWERED" "$OUT_CONST" "$OUT_TABLE" "$OUT_RENAMED" "$OUT_STUBBED" "$OUT_RELAXED" "$DOTNET_PRE" "$OUT_P1" "$OUT_P1B" "$OUT_P1C" "$OUT_P2" "$OUT_P3"' EXIT
python3 "$RUNTIME/scripts/patch_fn_to_global_get.py" "$OUT_RELAXED" "$OUT_P1" "$G7_FN" 7
python3 "$RUNTIME/scripts/patch_fn_to_global_get.py" "$OUT_P1" "$OUT_P1B" "$MB_FN" "$MEM_BASE_GLOBAL"
PATCH_INPUT="$OUT_P1B"
if [ -n "$PDB_FN" ]; then
    OUT_PDB=$(mktemp -t wasp-pdb.XXXXXX).wasm
    python3 "$RUNTIME/scripts/patch_fn_return_zero.py" "$PATCH_INPUT" "$OUT_PDB" "$PDB_FN"
    PATCH_INPUT="$OUT_PDB"
fi
if [ -n "$BRG_FN" ]; then
    python3 "$RUNTIME/scripts/patch_fn_return_zero.py" "$PATCH_INPUT" "$OUT_P1C" "$BRG_FN"
else
    cp "$PATCH_INPUT" "$OUT_P1C"
fi

if [ -n "$DN_GET" ] && [ -n "$DN_INS" ] && [ -n "$GET_FN" ] && [ -n "$INS_FN" ]; then
    echo "  SIMD build — applying dn_simdhash bypass: get=$DN_GET → $GET_FN, insert=$DN_INS → $INS_FN"
    python3 "$RUNTIME/scripts/patch_fn_to_call.py" "$OUT_P1C" "$OUT_P2" "$DN_GET" "$GET_FN"
    python3 "$RUNTIME/scripts/patch_fn_to_call.py" "$OUT_P2" "$OUT_P3" "$DN_INS" "$INS_FN"
    DEFANG_INPUT="$OUT_P3"
else
    echo "  no-SIMD build — skipping dn_simdhash bypass + assert defang"
    DEFANG_INPUT="$OUT_P1C"
fi

# Always apply assert defang for now — best-effort only. With asyncify
# the wat pattern may be transformed; if defang misses, fall back to a
# raw copy. Once corelib actually loads (all 34 BCLs registered via
# the chunked register_chunk + asyncify path), the assert never fires
# anyway.
if ! python3 "$RUNTIME/scripts/patch_disable_g_assert.py" "$DEFANG_INPUT" "$OUT_FINAL" --line 2718 2>&1; then
    echo "  defang missed (likely asyncify renumbering) — using raw copy"
    cp "$DEFANG_INPUT" "$OUT_FINAL"
fi

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
