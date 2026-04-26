#!/usr/bin/env bash
# Build WaspHost.dll and stage it into runtime/inputs/ for the
# canister-side build pipeline to bundle.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT_DLL="$HERE/bin/Release/net10.0/WaspHost.dll"
DEST="$HERE/../../inputs/WaspHost.dll"

dotnet build -c Release "$HERE/WaspHost.csproj"

if [[ ! -f "$OUT_DLL" ]]; then
    echo "ERROR: build did not produce $OUT_DLL" >&2
    exit 1
fi

mkdir -p "$(dirname "$DEST")"
cp "$OUT_DLL" "$DEST"
echo "copied $OUT_DLL -> $DEST"
