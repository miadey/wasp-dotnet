#!/usr/bin/env bash
# icp-publish: turn a NativeAOT-LLVM .wasm into a deployable IC canister .wasm
# by renaming canister_<kind>__<name> exports to "canister_<kind> <name>"
# (with the literal space the IC interface spec requires) and stripping
# unused WASI imports.
#
# Usage: icp-publish.sh <input.wasm> <output.wasm>

set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <input.wasm> <output.wasm>" >&2
  exit 2
fi

IN="$1"
OUT="$2"
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

# 1. Print to text format
wasm-tools print "$IN" > "$TMP/in.wat"

# 2. Replace canister_query__name → "canister_query name" etc.
#    Underscore-pair → single space, in export declarations.
sed -E 's/\(export "canister_(query|update|composite_query)__([A-Za-z_][A-Za-z0-9_]*)"/(export "canister_\1 \2"/g' \
  "$TMP/in.wat" > "$TMP/renamed.wat"

# Sanity-check at least one rename happened (the input had at least one
# canister_*__* export to begin with).
if grep -qE '\(export "canister_(query|update|composite_query)__' "$TMP/in.wat"; then
  if ! grep -qE '\(export "canister_(query|update|composite_query) ' "$TMP/renamed.wat"; then
    echo "icp-publish: rename pass found candidates but produced no output" >&2
    exit 3
  fi
fi

# 3. Parse back to binary
wasm-tools parse "$TMP/renamed.wat" -o "$TMP/renamed.wasm"

# 4. Run ic-wasm shrink (drops unused funcs + debug info)
ic-wasm "$TMP/renamed.wasm" -o "$TMP/shrunk.wasm" shrink

# 5. Done — caller can apply ic-wasm metadata to embed candid:service if desired
cp "$TMP/shrunk.wasm" "$OUT"

echo "icp-publish: $(stat -f%z "$OUT" 2>/dev/null || stat -c%s "$OUT") bytes  →  $OUT"
echo "exports:"
wasm-tools print "$OUT" | grep -E '^\s*\(export "canister' || true
