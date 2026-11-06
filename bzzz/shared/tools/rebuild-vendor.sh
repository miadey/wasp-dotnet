#!/usr/bin/env bash
# rebuild-vendor.sh — idempotent rebuild of the Cecil-rewritten framework
# DLLs that Wasp.AspNetCore / Wasp.AspNetCore.Blazor.Server vendor under
# their respective Vendor/ directories.
#
# Closes the manual prereq documented in build-and-deploy.sh:
#     "run: dotnet run --project shared/tools/Wasp.CircuitHostWeaver"
#
# Always runs the weavers inside the wasp-build docker container against
# the Linux .NET SDK's IL-only framework DLLs. macOS framework DLLs ship
# with ReadyToRun native sections that Mono.Cecil refuses to write back.
# The Vendor/ output is platform-independent IL.
#
# Skips work if the vendored DLL already exists and is newer than this
# script + the weaver sources; pass --force to rebuild unconditionally.
#
# Tracks gh #89 (M4.S9.3).

set -euo pipefail

REPO=$(cd "$(dirname "$0")/../.." && pwd)
DOCKER_IMAGE="${WASP_BUILD_IMAGE:-wasp-build}"
FORCE=0
for a in "$@"; do
    case "$a" in
        --force|-f) FORCE=1 ;;
        *) echo "unknown arg: $a" >&2; exit 2 ;;
    esac
done

# Sanity: docker is available + image built.
if ! command -v docker >/dev/null 2>&1; then
    echo "FATAL: docker not on PATH; install Docker Desktop or set WASP_BUILD_IMAGE" >&2
    exit 2
fi
if ! docker image inspect "$DOCKER_IMAGE" >/dev/null 2>&1; then
    echo "FATAL: docker image '$DOCKER_IMAGE' not found locally" >&2
    echo "       build it via: docker build -f aot/docker/Dockerfile.build -t $DOCKER_IMAGE aot/docker" >&2
    exit 2
fi

# All paths below are container paths. The repo is bind-mounted at /work.
FRAMEWORK_CONTAINER=$(docker run --rm --platform linux/amd64 "$DOCKER_IMAGE" \
    bash -c 'ls -d /usr/share/dotnet/shared/Microsoft.AspNetCore.App/*/ | sort -V | tail -1')
FRAMEWORK_CONTAINER="${FRAMEWORK_CONTAINER%/}"
echo "[rebuild-vendor] framework (container): $FRAMEWORK_CONTAINER"

# Helper: skip-or-run. Existence-only by default; --force always rebuilds.
# We avoid mtime-vs-source freshness because Mac/Linux clones don't preserve
# weaver-source mtimes and would re-build every invocation otherwise.
needs_rebuild() {
    local dst="$1"
    [ "$FORCE" -eq 1 ] && return 0
    [ ! -f "$dst" ]
}

# Helper: run a weaver inside the container, mounting the repo as /work.
run_weaver_in_docker() {
    local label="$1"; local weaver_proj="$2"; local src_container="$3"; local dst_repo_rel="$4"
    shift 4
    local dst_host="$REPO/$dst_repo_rel"
    if ! needs_rebuild "$dst_host"; then
        echo "[rebuild-vendor] $label: up-to-date ($(basename "$dst_host"))"
        return
    fi
    echo "[rebuild-vendor] $label: rewriting → $dst_repo_rel"
    mkdir -p "$(dirname "$dst_host")"
    docker run --rm --platform linux/amd64 \
        -v "$REPO:/work" -v wasp-nuget:/nuget \
        "$DOCKER_IMAGE" \
        bash -c "cd /work && dotnet run --project shared/tools/$weaver_proj -- '$src_container' '/work/$dst_repo_rel' $*"
}

# 1. RenderTreeWeaver: Components.dll — AppendElement/Text/Markup/...
run_weaver_in_docker \
    "RenderTreeWeaver" \
    "Wasp.RenderTreeWeaver" \
    "$FRAMEWORK_CONTAINER/Microsoft.AspNetCore.Components.dll" \
    "aot/Wasp.AspNetCore/Vendor/Microsoft.AspNetCore.Components.dll"

# 2. CircuitHostWeaver --rewrite-typenamehash: Components.Endpoints.dll
run_weaver_in_docker \
    "CircuitHostWeaver (Endpoints)" \
    "Wasp.CircuitHostWeaver" \
    "$FRAMEWORK_CONTAINER/Microsoft.AspNetCore.Components.Endpoints.dll" \
    "aot/Wasp.AspNetCore/Vendor/Microsoft.AspNetCore.Components.Endpoints.dll" \
    --rewrite-typenamehash

# 3. CircuitHostWeaver: Components.Server.dll — visibility widening
run_weaver_in_docker \
    "CircuitHostWeaver (Server)" \
    "Wasp.CircuitHostWeaver" \
    "$FRAMEWORK_CONTAINER/Microsoft.AspNetCore.Components.Server.dll" \
    "aot/Wasp.AspNetCore.Blazor.Server/Vendor/Microsoft.AspNetCore.Components.Server.dll"

# 4. Patched LLVM JIT for gh #80 (PR #3259 cherry-picked onto the source
#    matching our ILC package). The shared library lives at
#    runtime/inputs/libclrjit_universal_wasm32_x64.patched-issue80.so;
#    we swap it into the docker nuget volume so subsequent wasm builds
#    use the fixed JIT. Idempotent — keeps a .stock backup so the swap
#    is reversible.
#
#    See aot/samples/Issue80Repro/REBUILD_JIT.md for how the .so was
#    produced and how to regenerate it for a future package version.
ILC_PKG="runtime.linux-x64.microsoft.dotnet.ilcompiler.llvm/10.0.0-alpha.1.25162.1"
JIT_FILE="libclrjit_universal_wasm32_x64.so"
PATCHED_JIT_HOST="$REPO/runtime/inputs/libclrjit_universal_wasm32_x64.patched-issue80.so"
PATCHED_SHA="eec1df0e9367758c5301543a9fc4940d9164d82497fd9d4b458ba13b5638a8d5"

if [ ! -f "$PATCHED_JIT_HOST" ]; then
    echo "[rebuild-vendor] WARN: $PATCHED_JIT_HOST missing — skipping JIT swap"
elif [ "$(shasum -a 256 "$PATCHED_JIT_HOST" | cut -d' ' -f1)" != "$PATCHED_SHA" ]; then
    echo "[rebuild-vendor] WARN: patched JIT sha mismatch — skipping swap"
else
    echo "[rebuild-vendor] JIT swap: checking docker nuget cache"
    docker run --rm --platform linux/amd64 \
        -v "$REPO:/work" -v wasp-nuget:/nuget \
        "$DOCKER_IMAGE" \
        bash -c "
            set -e
            target=/nuget/$ILC_PKG/tools/$JIT_FILE
            backup=\$target.stock
            patched=/work/runtime/inputs/$(basename "$PATCHED_JIT_HOST")
            if [ ! -f \"\$target\" ]; then
                echo '[rebuild-vendor]   no stock JIT in cache yet — first build will install it'
                exit 0
            fi
            installed_sha=\$(sha256sum \"\$target\" | cut -d' ' -f1)
            patched_sha=\$(sha256sum \"\$patched\" | cut -d' ' -f1)
            if [ \"\$installed_sha\" = \"\$patched_sha\" ]; then
                echo '[rebuild-vendor]   JIT already patched'
                exit 0
            fi
            if [ ! -f \"\$backup\" ]; then
                cp \"\$target\" \"\$backup\"
                echo '[rebuild-vendor]   saved stock JIT → '\"\$backup\"
            fi
            cp \"\$patched\" \"\$target\"
            echo '[rebuild-vendor]   installed patched JIT (#80 fix)'
        "
fi

echo "[rebuild-vendor] done"
