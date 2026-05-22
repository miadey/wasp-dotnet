#!/usr/bin/env bash
# Convenience launcher for the vanilla acceptance harness.
# Assumes dfx is running (dfx start --background) and at least
# CircuitOnIc has been installed via build-and-deploy.sh.
#
# Tracks gh #93 (M4.S9.7).

set -euo pipefail

cd "$(dirname "$0")/../.."

if ! dfx ping 2>&1 | grep -q replica_health; then
    echo "dfx replica not reachable on default port. Start it with:" >&2
    echo "    cd $(pwd) && dfx start --background" >&2
    exit 2
fi

exec node aot/tests/vanilla-acceptance.mjs "$@"
