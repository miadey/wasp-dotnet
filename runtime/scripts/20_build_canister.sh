#!/usr/bin/env bash
# 20_build_canister.sh — cargo-build the Rust ic-cdk shim that hosts
# Microsoft's dotnet.native.wasm. Output:
#   runtime/wasp_canister/target/wasm32-unknown-unknown/release/wasp_canister.wasm
#
# Followed by 30_merge.sh to fuse with dotnet.native.wasm.

set -euo pipefail

REPO=$(cd "$(dirname "$0")/../.." && pwd)
RUNTIME=$REPO/runtime
CARGO_MANIFEST=$RUNTIME/wasp_canister/Cargo.toml

[ -f "$CARGO_MANIFEST" ] || {
    echo "missing $CARGO_MANIFEST" >&2
    exit 1
}

# Ensure the wasm target is installed; rustup will no-op if already present.
if command -v rustup >/dev/null; then
    rustup target add wasm32-unknown-unknown >/dev/null
fi

echo "[build] cargo build --release --target wasm32-unknown-unknown -p wasp_canister"
cargo build \
    --manifest-path "$CARGO_MANIFEST" \
    --release \
    --target wasm32-unknown-unknown \
    -p wasp_canister

OUT=$RUNTIME/wasp_canister/target/wasm32-unknown-unknown/release/wasp_canister.wasm
SIZE=$(wc -c < "$OUT" | tr -d ' ')
echo "built: ${SIZE} bytes → $OUT"
