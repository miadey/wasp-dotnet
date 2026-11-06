# Getting started and contributing

Start from the [repository overview](README.md). This guide assumes a fresh
clone of `miadey/wasp-dotnet` and a shell at its root unless a command changes
directory. You do not need a production controller identity for compilation
or host tests.

## First-day route

1. Clone the repository and identify your target: framework (`aot/`), full
   social application (`bzzz/`), or runtime research (`runtime/`).
2. Read [Architecture](docs/ARCHITECTURE.md) and, for application work,
   [Bzzz](bzzz/README.md).
3. Install the toolchain below and run the [host tests](docs/TESTING.md).
   Compare against the documented baseline, including the existing failure.
4. Build HelloWorld without deployment, following the root README. Then test
   it on a new local canister.
5. For Bzzz, build its own `wasi-stub` helper and follow its local install
   instructions. Public-page checks and authenticated/encrypted flows have
   different prerequisites.
6. Make a small change, run the relevant checks, and open a focused PR.

```bash
git clone https://github.com/miadey/wasp-dotnet.git
cd wasp-dotnet
git switch -c codex/short-description
```

A fresh clone contains Bzzz sources directly; no submodule initialization is
needed. The original developer's checkout may still contain a nested
`bzzz/.git` from its earlier extraction. In that checkout, perform repository
commits from the **outer repository root**, so changes are published to
`wasp-dotnet`. The nested metadata is not distributed by GitHub.

## Toolchain

| Tool | Used for | Version information in this repo |
| --- | --- | --- |
| .NET SDK 10 | Host tests, Razor/browser WASM builds | Projects target `net10.0`; host tests checked with SDK `10.0.202` |
| Docker, Linux x64 containers | NativeAOT compilation, including on Apple Silicon | Image `wasp-dotnet-build:latest`, defined by the Dockerfile |
| Rust/Cargo | `wasi-stub` | The current CI installs stable Rust |
| `wasm-tools` | Wasm rewriting/validation | CI pins `1.220.0` |
| `ic-wasm` | Shrinking/post-processing | CI pins `0.9.1` |
| Binaryen | `wasm-opt`; `wasm-merge` for runtime research | No repository-wide version pin |
| `dfx` | Local replica and canister management | No repository-wide version pin |
| Node.js/npm | Decoder cross-check and HTTP acceptance harness | Use a Node version with built-in `fetch` (18+); no repo-wide pin |

Install tools through their normal distribution channels. These are the
repository's current dependency choices, not a claim about the latest releases.
For matching host Wasm tools through Cargo:

```bash
cargo install wasm-tools --version 1.220.0 --locked
cargo install ic-wasm --version 0.9.1 --locked
```

Verify the tools needed for your task:

```bash
dotnet --version
docker version
cargo --version
wasm-tools --version
ic-wasm --version
wasm-opt --version
dfx --version
node --version
```

The [Dockerfile](aot/docker/Dockerfile.build) installs .NET 10 and WASI SDK 24.0
and wraps the linker to discard component-type arguments. The image uses the
floating `sdk:10.0` base tag, so record the image ID when comparing builds.
[AOT NuGet configuration](aot/NuGet.config) includes public experimental .NET
feeds. The canister projects pin preview compiler packages; do not casually
upgrade these or regenerate the vendored assemblies as part of unrelated work.

```bash
docker build --platform linux/amd64 -t wasp-dotnet-build:latest \
  -f aot/docker/Dockerfile.build aot/docker
docker volume create wasp-nuget
cargo build --release --manifest-path shared/tools/wasi-stub/Cargo.toml
```

Bzzz needs an additional helper at its own path:

```bash
cargo build --release --manifest-path bzzz/shared/tools/wasi-stub/Cargo.toml
```

The root `aot/tools` entry is a tracked symlink to `../shared/tools`. Preserve
symlinks when copying the tree. This setup is written for macOS/Linux shells.

## Working with framework copies

The root framework and Bzzz's copied framework are separate source trees.
For a reusable fix, review both versions and explicitly port the necessary
changes. Do not replace the whole Bzzz tree with the root demo: their application
services and embedded assets differ.

The checked-in `Vendor/*.dll` assemblies are intentional compiler workarounds.
Their source rewriters live in `shared/tools/Wasp.RenderTreeWeaver` and
`shared/tools/Wasp.CircuitHostWeaver`. See the [architecture guide](docs/ARCHITECTURE.md).
They are required build inputs, not cleanup candidates.

`shared/tools/rebuild-vendor.sh` defaults to the separate image name `wasp-build`;
set `WASP_BUILD_IMAGE=wasp-dotnet-build:latest` when using the image above. Its
Bzzz copy still includes steps for components outside Bzzz's reduced tree; it
is not part of the first-build instructions. Review its targets before use.

## Local deployment

- Run `dfx` in the directory containing the intended `dfx.json`: `aot/` for
  demos, `bzzz/aot/` for Bzzz, `runtime/` for research.
- Both AOT configurations use port 4944. Reuse a suitable local replica rather
  than starting conflicting instances. Record which project and identity you use.
- First install of an empty canister: `--mode install`. Subsequent state-preserving
  code replacement: `--mode upgrade`. `reinstall` is for disposable test state.
- The original sample scripts have different deployment behavior. Bzzz builds
  only by default; several demos deploy immediately, and HelloWorld reinstalls.
- Do not copy mainnet credentials or a collaborator's private keys into the repo.
  GitHub contribution access and canister-controller access are separate.

For an existing deployment, coordinate the target, identity, state compatibility,
and recovery procedure with its maintainer. Do not use a mainnet deployment as
an onboarding smoke test. The Bzzz guide describes the existing target and its
stable-memory contract.

## Validation and PR workflow

Use the commands in [Testing](docs/TESTING.md). Run checks appropriate to the
changed area, and report **what ran, what passed/failed/skipped, and what was not
validated**. Host tests do not exercise `ic0` inside a real replica.

Before pushing:

```bash
git diff --check
git status --short
```

Keep C# formatting consistent with neighboring code; use `dotnet format` with
an explicit project if needed (there is no root solution). Use `cargo fmt` in
the relevant Rust project. Preserve shell `set -euo pipefail` and resolve paths
from the script, not a developer-specific home directory.

A PR should describe the problem, changed behavior, validation, and any data
migration or deployment impact. Link the relevant issue for substantial work.
Do not describe an old test result or a skipped canister section as a new pass.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| `wasi-stub` not found | Build the helper under the same tree as the script: root or `bzzz/` |
| `ic-wasm` / `wasm-tools` / `wasm-opt` not found | These run on the host after Docker compilation |
| Missing framework DLL or Razor/compiler error | Check the committed Vendor assemblies, preview compiler pins, and [unsupported APIs](aot/Wasp.AspNetCore/UNSUPPORTED.md) |
| New Bzzz canister rejects `upgrade` | Set `WASP_DEPLOY_MODE=install` for the first local install |
| Bzzz page renders but encrypted DMs fail locally | Read the Internet Identity and vetKD configuration limits in its guide |
| A host test fails on `BatchId` normalization | Compare with the documented existing router-test failure |
| Root demo changes do not affect Bzzz | You edited the other framework/application copy |
| Runtime fails during Mono boot | Read [runtime status](runtime/README.md); this is an experimental path |

The RNG scan scripts currently hard-code `/Users/miadey/dev/csharp`; the Bzzz
copy therefore scans the outer demo on the original machine. They are not a
portable Bzzz validation gate until their source-root handling is corrected.
