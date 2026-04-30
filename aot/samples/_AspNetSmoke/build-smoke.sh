#!/usr/bin/env bash
# Issue #44 smoke test driver. Runs the same toolchain HelloWeb uses, captures
# trim warnings, .wasm size, per-function instruction histogram, and WASI
# import surface. Does NOT deploy.
set -uo pipefail

REPO=$(cd "$(dirname "$0")/../.." && pwd)
SAMPLE_REL="samples/_AspNetSmoke"
SAMPLE="$REPO/$SAMPLE_REL"
LOG="$SAMPLE/build.log"
TRIM="$SAMPLE/trim-warnings.txt"
REPORT="$SAMPLE/RESULTS.md"

mkdir -p "$SAMPLE/bin"

echo ">>> running dotnet build inside wasp-dotnet-build:latest"
docker run --rm --platform linux/amd64 \
  -v "$REPO:/work" -v wasp-nuget:/nuget \
  wasp-dotnet-build:latest \
  bash -c "cd $SAMPLE_REL && dotnet build -c Release /p:IlcLlvmTarget=wasm32-wasi 2>&1" \
  > "$LOG"
BUILD_RC=$?

# Extract trim warnings (IL2xxx, IL3xxx) from the build log.
grep -E 'IL[0-9]{4}' "$LOG" | sort -u > "$TRIM" || true
TRIM_COUNT=$(wc -l < "$TRIM" | tr -d ' ')

WASM_PATH=$(find "$SAMPLE/bin" -name 'AspNetSmoke.wasm' -path '*/publish/*' 2>/dev/null | head -1)
if [ -z "$WASM_PATH" ]; then
  WASM_PATH=$(find "$SAMPLE/bin" -name '*.wasm' 2>/dev/null | head -1)
fi

{
  echo "# Issue #44 — AspNet smoke results"
  echo
  echo "Build exit code: \`$BUILD_RC\`"
  echo
  echo "Trim warnings (unique): **$TRIM_COUNT**"
  echo
  if [ -n "$WASM_PATH" ] && [ -f "$WASM_PATH" ]; then
    SIZE=$(wc -c < "$WASM_PATH" | tr -d ' ')
    SIZE_MB=$(awk "BEGIN { printf \"%.2f\", $SIZE / 1048576 }")
    echo "Wasm artifact: \`$(realpath --relative-to="$REPO" "$WASM_PATH" 2>/dev/null || echo "$WASM_PATH")\`"
    echo "Wasm size: **${SIZE} bytes (${SIZE_MB} MB)**"
    echo
    echo "## WASI imports"
    echo
    echo '```'
    wasm-tools print "$WASM_PATH" 2>/dev/null | grep -E '\(import "wasi' | sort -u || echo "(none or wasm-tools missing)"
    echo '```'
    echo
    echo "## Top 20 functions by body size"
    echo
    echo '```'
    wasm-tools print "$WASM_PATH" 2>/dev/null \
      | awk '
        /^\s*\(func \$/ { fn = $0; size = 0; in_fn = 1; next }
        in_fn { size += length($0) }
        /^\s*\)/ && in_fn { print size, fn; in_fn = 0 }
      ' | sort -rn | head -20 || echo "(parse failed)"
    echo '```'
    echo
  else
    echo "**No wasm artifact produced** — see build.log for details"
  fi
  echo
  echo "## Build log tail"
  echo
  echo '```'
  tail -50 "$LOG"
  echo '```'
} > "$REPORT"

cat "$REPORT"
exit "$BUILD_RC"
