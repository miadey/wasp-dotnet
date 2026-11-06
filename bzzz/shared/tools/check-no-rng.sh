#!/usr/bin/env bash
set -euo pipefail

# check-no-rng.sh — Phase-1 crypto-foundations RNG safety guard.
#
# INVARIANT: all canister randomness must come from the browser
# (crypto.getRandomValues, injected JS) or the IC management raw_rand (async).
# System.Random / RandomNumberGenerator must NEVER produce live entropy in
# canister code. On wasm32-wasi RandomNumberGenerator is zeroed /
# PlatformNotSupported, and a time-seedless `new Random()` is non-deterministic
# across replicas — both silently break the canister's deterministic-replica
# model. This script greps the app + framework SOURCE for the forbidden
# patterns and FAILS the build if any reappear.
#
# Forbidden patterns:
#   - RandomNumberGenerator        (any use)
#   - new Random()                 (no seed → time-based, non-deterministic)
#   - Random.Shared                (shared time-seeded instance)
#
# Allowed (NOT flagged): `new Random(seed)` with an explicit deterministic seed
# (e.g. WeatherService seeds from Ic0.time() so every replica agrees).
#
# Fix a violation by sourcing entropy from:
#   - crypto.getRandomValues in injected JS (synchronous, browser-side), or
#   - the IC management canister raw_rand (asynchronous, canister-side).
#
# Companion guard: shared/tools/Wasp.CircuitHostWeaver/Program.cs neuters the
# framework's own RNG seeds in Microsoft.AspNetCore.Components.{Server,Endpoints}
# and logs each rewrite (grep build logs for "WASP-RNG-WEAVE:").

ROOT="/Users/miadey/dev/csharp"

# Search roots: the BlazorWasp app sources and the Wasp.* framework sources.
SEARCH_GLOBS=(
  "$ROOT/aot/samples/BlazorWasp"/*.cs
  "$ROOT/aot/Wasp."*/src
)

# Exclusions (these files legitimately contain a matched token):
#   - the weaver tool itself: it REWRITES RandomNumberGenerator sites, so its
#     source naturally names the pattern (and it is a host build tool, not
#     canister code).
#   - WeatherService.cs: the deterministic-seed demo `new Random(seed)`.
EXCLUDE_REGEX="(/Wasp\.CircuitHostWeaver/|/WeatherService\.cs$)"

found=0

scan() {
  local label="$1"
  local pattern="$2"
  # -r recurse (handles the .../src dir globs), -n line numbers, -E ERE.
  # --include limits recursion to .cs; harmless on the explicit *.cs file args.
  local hits
  hits="$(grep -rnE --include='*.cs' -- "$pattern" "${SEARCH_GLOBS[@]}" 2>/dev/null \
            | grep -vE -- "$EXCLUDE_REGEX" || true)"
  if [ -n "$hits" ]; then
    echo "FORBIDDEN: $label"
    printf '%s\n' "$hits" | sed 's/^/  /'
    echo
    found=1
  fi
}

echo "check-no-rng: scanning app + framework source for forbidden RNG patterns…"

scan "RandomNumberGenerator (zeroed / PlatformNotSupported on wasm32-wasi)" \
     "RandomNumberGenerator"

scan "new Random() without a seed (non-deterministic across replicas)" \
     "new[[:space:]]+Random[[:space:]]*\([[:space:]]*\)"

scan "Random.Shared (shared time-seeded RNG)" \
     "Random\.Shared"

if [ "$found" -ne 0 ]; then
  echo "check-no-rng: FAIL — canister randomness must come from crypto.getRandomValues"
  echo "             (injected JS, synchronous) or the IC management raw_rand (async)."
  exit 1
fi

echo "check-no-rng: OK — no forbidden RNG patterns found."
exit 0
