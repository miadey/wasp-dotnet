#!/usr/bin/env bash
# build-canisters.sh — build every AOT canister sample end-to-end.
#
# Used by the GitHub Actions CI workflow (.github/workflows/ci.yml) and by
# contributors who want to reproduce the CI build locally. It performs the
# same steps that each sample's build-and-deploy.sh does, minus the dfx
# install at the end.
#
# Prereqs (host):
#   - docker, with the wasp-dotnet-build:latest image already built from
#     aot/docker/Dockerfile.build
#   - wasm-tools and ic-wasm on PATH (used by icp-publish.sh and validation)
#   - shared/tools/wasi-stub already cargo-built in release mode
#
# Env knobs:
#   REPO_ROOT            — repo root (default: git rev-parse from this script)
#   NUGET_VOLUME         — docker volume name for the NuGet cache
#                          (default: wasp-nuget)
#   SIZE_BUDGET_BYTES    — per-canister code-section budget
#                          (default: 10 MiB = 10485760)
#   SAMPLES              — space-separated sample list to override the default
#
# Exit codes:
#   0  all samples built, validated, and within budget
#   1  a build, validate, or budget step failed

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="${REPO_ROOT:-$(cd "$SCRIPT_DIR/../.." && pwd)}"
AOT_DIR="$REPO_ROOT/aot"
WASI_STUB_BIN="${WASI_STUB_BIN:-$REPO_ROOT/shared/tools/wasi-stub/target/release/wasi-stub}"
ICP_PUBLISH="$REPO_ROOT/shared/tools/icp-publish/icp-publish.sh"
NUGET_VOLUME="${NUGET_VOLUME:-wasp-nuget}"
SIZE_BUDGET_BYTES="${SIZE_BUDGET_BYTES:-10485760}"   # 10 MiB
DOCKER_IMAGE="${DOCKER_IMAGE:-wasp-dotnet-build:latest}"

# Default canister sample list (BlazorChat is intentionally excluded — it
# is a Blazor WebAssembly static-site sample, not a canister).
DEFAULT_SAMPLES="HelloWorld Counter HelloWeb HelloFetch HelloChat"
SAMPLES="${SAMPLES:-$DEFAULT_SAMPLES}"

# ---------------------------------------------------------------------------

log()  { printf '\033[1;34m[build-canisters]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[build-canisters]\033[0m %s\n' "$*" >&2; }
fail() { printf '\033[1;31m[build-canisters]\033[0m %s\n' "$*" >&2; exit 1; }

require() {
  command -v "$1" >/dev/null 2>&1 || fail "required tool not on PATH: $1"
}

require docker
require wasm-tools
require ic-wasm

[[ -x "$WASI_STUB_BIN" ]] || fail "wasi-stub binary not found at $WASI_STUB_BIN — build it with: cargo build --release --manifest-path shared/tools/wasi-stub/Cargo.toml"
[[ -x "$ICP_PUBLISH"   ]] || fail "icp-publish.sh not executable at $ICP_PUBLISH"

# Make sure the docker volume exists so the cache mount succeeds even on
# a clean runner.
docker volume inspect "$NUGET_VOLUME" >/dev/null 2>&1 \
  || docker volume create "$NUGET_VOLUME" >/dev/null

# Verify the build image exists locally — we never pull/build it here; that
# is the caller's responsibility (see ci.yml or the docker/ readme).
docker image inspect "$DOCKER_IMAGE" >/dev/null 2>&1 \
  || fail "docker image $DOCKER_IMAGE not found locally — build it from aot/docker/Dockerfile.build first"

# ---------------------------------------------------------------------------

# code_section_size <wasm-file>
# Prints the byte size of the (code) section by counting bytes in
# `wasm-tools dump`'s code-section line. Falls back to total file size if
# the dump format changes (still meaningful as an upper bound).
code_section_size() {
  local wasm="$1"
  local size
  # `wasm-tools dump` prints a line like:
  #   0x000123 | ... | code section
  # but a more robust path is `wasm-tools objdump`-style via parsing the
  # section table. For portability we use `wasm-tools strip --keep-section`
  # plus stat as a fallback. Simplest reliable trick: parse `wasm-tools
  # print --skeleton` is not stable either — so we walk sections with
  # python via `wasm-tools` JSON when available, else use file size.
  size=$(wasm-tools dump "$wasm" 2>/dev/null \
    | awk '/code section/ {getline; print $0}' \
    | head -n1 \
    | grep -oE 'size: [0-9]+' \
    | awk '{print $2}' || true)
  if [[ -z "${size:-}" ]]; then
    # Fallback: total wasm size. Conservative — any over-budget result here
    # still indicates a real problem.
    size=$(stat -f%z "$wasm" 2>/dev/null || stat -c%s "$wasm")
  fi
  echo "$size"
}

build_one() {
  local sample="$1"
  local sample_dir="$AOT_DIR/samples/$sample"
  local raw="$sample_dir/bin/Release/net10.0/wasi-wasm/publish/${sample}.wasm"
  local renamed
  renamed=$(mktemp -t "wasp-${sample}.XXXXXX.wasm")
  local final="$sample_dir/${sample}.canister.wasm"

  [[ -d "$sample_dir" ]] || fail "sample dir missing: $sample_dir"

  log "[$sample] dotnet build (NativeAOT-LLVM, wasm32-wasi)"
  docker run --rm --platform linux/amd64 \
    -v "$AOT_DIR:/work" \
    -v "$NUGET_VOLUME:/nuget" \
    "$DOCKER_IMAGE" \
    bash -c "cd samples/$sample && dotnet build -c Release /p:IlcLlvmTarget=wasm32-wasi"

  [[ -f "$raw" ]] || fail "[$sample] expected build output not found: $raw"

  log "[$sample] icp-publish (rename exports + shrink)"
  "$ICP_PUBLISH" "$raw" "$renamed"

  log "[$sample] wasi-stub (replace WASI imports)"
  "$WASI_STUB_BIN" "$renamed" "$final"
  rm -f "$renamed"

  log "[$sample] wasm-tools validate"
  wasm-tools validate "$final"

  local size code_size
  size=$(stat -f%z "$final" 2>/dev/null || stat -c%s "$final")
  code_size=$(code_section_size "$final")
  log "[$sample] final wasm: $final ($size bytes total, code section ~$code_size bytes)"

  if (( code_size > SIZE_BUDGET_BYTES )); then
    fail "[$sample] code section $code_size bytes exceeds budget of $SIZE_BUDGET_BYTES bytes (10 MiB). See .github/workflows/SIZE_BUDGET.md"
  fi
}

# ---------------------------------------------------------------------------

failed=()
for sample in $SAMPLES; do
  if ! build_one "$sample"; then
    failed+=("$sample")
  fi
done

if (( ${#failed[@]} > 0 )); then
  fail "samples failed: ${failed[*]}"
fi

log "all samples built, validated, and within size budget"
