#!/usr/bin/env bash
# 30_merge.sh — combine wasp_canister.wasm + dotnet.native.wasm into a
# single canister wasm.
#
# Pipeline:
#   1. wasm-merge       — binaryen resolves wasp_canister's exports
#                          (env_imports + wasi_imports) against
#                          dotnet.native.wasm's same-named imports,
#                          and dotnet.native.wasm's mono_wasm_*
#                          exports against wasp_canister's env-module
#                          imports declared in mono_embed.rs.
#   2. icp-publish.sh   — rename `canister_<kind>__<name>` exports to
#                          the literal `canister_<kind> <name>` form
#                          the IC wire protocol expects.
#   3. wasi-stub        — replace any wasi imports the merge step
#                          didn't satisfy with no-op / trap stubs so
#                          the IC's wasm validator accepts the module.

set -euo pipefail

REPO=$(cd "$(dirname "$0")/../.." && pwd)
RUNTIME=$REPO/runtime

CANISTER_WASM=$RUNTIME/wasp_canister/target/wasm32-unknown-unknown/release/wasp_canister.wasm
DOTNET_WASM=$RUNTIME/inputs/dotnet.native.wasm

OUT_DIR=$RUNTIME/wasp_canister
OUT_FINAL=$OUT_DIR/canister.wasm

OUT_MERGED=$(mktemp -t wasp-merged.XXXXXX).wasm
OUT_RENAMED=$(mktemp -t wasp-renamed.XXXXXX).wasm
trap 'rm -f "$OUT_MERGED" "$OUT_RENAMED"' EXIT

# ---- preflight ----------------------------------------------------------

[ -f "$CANISTER_WASM" ] || {
    echo "missing $CANISTER_WASM" >&2
    echo "run runtime/scripts/20_build_canister.sh first" >&2
    exit 1
}
[ -f "$DOTNET_WASM" ] || {
    echo "missing $DOTNET_WASM" >&2
    echo "run runtime/scripts/10_publish_app.sh first" >&2
    exit 1
}

command -v wasm-merge >/dev/null || {
    echo "wasm-merge not on PATH (install binaryen: brew install binaryen)" >&2
    exit 1
}
command -v wasm-tools >/dev/null || {
    echo "wasm-tools not on PATH" >&2
    exit 1
}

ICP_PUBLISH=$REPO/shared/tools/icp-publish/icp-publish.sh
WASI_STUB=$REPO/shared/tools/wasi-stub/target/release/wasi-stub
[ -x "$ICP_PUBLISH" ] || { echo "missing or non-exec: $ICP_PUBLISH" >&2; exit 1; }
[ -x "$WASI_STUB" ] || {
    echo "missing $WASI_STUB" >&2
    echo "build it with: cargo build --release --manifest-path $REPO/shared/tools/wasi-stub/Cargo.toml" >&2
    exit 1
}

# ---- 1. wasm-merge ------------------------------------------------------

echo "[merge] wasm-merge wasp + dotnet → $OUT_MERGED"
wasm-merge \
    "$CANISTER_WASM" wasp \
    "$DOTNET_WASM"   dotnet \
    -o "$OUT_MERGED" \
    --enable-bulk-memory \
    --enable-mutable-globals \
    --enable-sign-ext \
    --enable-nontrapping-float-to-int \
    --enable-multivalue \
    --enable-reference-types

# ---- 2. icp-publish (canister_<kind>__name → "canister_<kind> name") -----

echo "[merge] icp-publish rename → $OUT_RENAMED"
"$ICP_PUBLISH" "$OUT_MERGED" "$OUT_RENAMED"

# ---- 3. wasi-stub (mop up leftover wasi imports) ------------------------

echo "[merge] wasi-stub → $OUT_FINAL"
"$WASI_STUB" "$OUT_RENAMED" "$OUT_FINAL"

# ---- report -------------------------------------------------------------

SIZE=$(wc -c < "$OUT_FINAL" | tr -d ' ')
echo
echo "merged canister: ${SIZE} bytes → $OUT_FINAL"
echo
echo "canister_* exports:"
wasm-tools print "$OUT_FINAL" | grep -E '^\s*\(export "canister' | head
