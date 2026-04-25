# Contributing to wasp-dotnet

Thanks for considering a contribution. wasp-dotnet is alpha; expect rough
edges and breaking changes. This guide covers the dev environment, the
two-story repo layout, and the PR process.

## Dev environment

You will need:

- **Docker** (Desktop on macOS / Engine on Linux). All wasm builds for the
  AOT story run inside the `wasp-dotnet-build` image because NativeAOT-LLVM
  has no macOS arm64 host package. Linux x64 hosts can build natively but
  Docker keeps everyone reproducible.
- **`dfx`** — install via `sh -ci "$(curl -fsSL https://internetcomputer.org/install.sh)"`.
  Used to spin up a local IC replica and deploy canisters.
- **Rust nightly** — `rustup toolchain install nightly`. The runtime story's
  `wasp_canister` and the `shared/tools/wasi-stub` helper both build with
  Cargo. Stable works for most things; nightly is needed for some unstable
  attributes used by ic-cdk.
- **Node 22** — for the Blazor front-end sample's tooling (`dfx generate`
  produces JS bindings that target this version).
- **`wasm-tools`** — `cargo install wasm-tools`.
- **`binaryen`** — `brew install binaryen` (provides `wasm-merge`, used by
  the runtime story).

One-time bootstrap:

```bash
git clone https://github.com/miadey/wasp-dotnet.git
cd wasp-dotnet
docker build -t wasp-dotnet-build:latest -f aot/docker/Dockerfile.build aot
(cd shared/tools/wasi-stub && cargo build --release)
```

## Build & deploy a sample

The AOT story has a one-shot build script per sample. See
`aot/samples/HelloWorld/build-and-deploy.sh` as the canonical example —
build inside Docker, post-process exports with `icp-publish.sh`, replace
WASI imports with `wasi-stub`, then `dfx canister install`.

```bash
dfx start --background --clean
dfx canister create hello
./aot/samples/HelloWorld/build-and-deploy.sh
```

## Run tests

There is no automated test suite yet. Verify changes by deploying a sample
and exercising it with `dfx canister call`:

```bash
dfx canister call hello greet '("world")'
dfx canister call counter increment
dfx canister call counter count
```

When changing a sample or a `Wasp.*` package, smoke-test the affected
samples before opening a PR. Automated PocketIC integration tests are on
the Phase B and Phase D milestones.

## Two stories — where to contribute

- **`aot/`** — shipped CDK that compiles C# directly into canister wasm via
  NativeAOT-LLVM. Best for: bug fixes in `Wasp.IcCdk`, source-generator
  improvements, new samples that fit within the trim-safe / no-`Reflection.Emit`
  constraint, IC HTTP / outcalls / WebSockets work.
- **`runtime/`** — in-progress path that re-hosts Microsoft's pre-built
  `dotnet.native.wasm` inside a Rust ic-cdk canister via `wasm-merge`. Best
  for: Rust shim work (the 75 `env` + 10 `wasi` imports), Mono embedding
  ABI, Candid-via-reflection, ASP.NET Core integration.

If you are unsure which story your contribution fits, file an issue first.

## Issue labels and milestones

Labels in use (`gh label list`):

- **Type:** `feature`, `bug`, `docs`, `chore`, `ci`, `build`, `release`,
  `sample`, `test`, `perf`, `spike`
- **Area:** `runtime`, `runtime-patch`
- **Priority:** `priority/critical`
- **Triage:** `good first issue`, `help wanted`, `question`, `duplicate`,
  `invalid`, `wontfix`

Milestones (`gh api repos/miadey/wasp-dotnet/milestones`):

- **P0: Bootstrap** — repo, top-level docs, CI for `aot/`
- **Phase A: Hello-world via wasm-merge** — single de-risking spike for the
  runtime story; one managed `Console.WriteLine` reaches `dfx canister logs`
- **Phase B: Friendly C# CDK** — `[CanisterQuery]`/`[CanisterUpdate]`
  ergonomics on top of the runtime; reflection-driven Candid; stable structures
- **Phase C: ASP.NET Core integration** — `IcServer : IServer`, asyncify
  chunking, streaming responses, `WaspIcHttpClient`
- **Phase D: Polish + mainnet** — perf table, NuGet 0.1.0-preview, mainnet
  sample deployments

New issues should pick a milestone and at least one type label.

## Code style

- **C#** — run `dotnet format` before pushing. The repo uses standard .NET
  conventions; the source generator emits braces-on-new-line. Prefer
  expression-bodied members where they improve clarity, not just to save
  lines.
- **Rust** — default `rustfmt` (`cargo fmt`). Run `cargo clippy --all-targets`
  and address warnings or justify them in the PR description.
- **Shell** — `set -euo pipefail` at the top of every script; absolute paths
  via `$(cd "$(dirname "$0")/.." && pwd)`.
- **Markdown** — wrap at ~80 columns where comfortable; do not hard-wrap
  fenced code blocks or tables.

## PR process

1. Open or claim an issue first for anything non-trivial. Drive-by fixes
   (typos, broken links, obvious bugs) can skip this.
2. Branch from `main`: `git checkout -b feat/short-description`.
3. Keep PRs focused. One logical change per PR; split mechanical
   refactors from behavior changes.
4. Run `dotnet format` (C#) and `cargo fmt` (Rust) on touched code.
5. Smoke-test the affected sample(s) with `dfx canister call`.
6. Reference the issue (`Fixes #N`) and call out anything reviewers should
   focus on.
7. Be responsive to review. Squash on merge.

## Code of conduct

Be kind. Assume good faith. If you see behavior that does not meet that bar,
email madey@me.com.
