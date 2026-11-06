# Canister size budget

The [CI workflow](ci.yml) passes `SIZE_BUDGET_BYTES=10485760` (10 MiB) to
[build-canisters.sh](build-canisters.sh). The script attempts to estimate the
code section from `wasm-tools dump` and falls back to the total file size if
parsing yields no value. This is a repository regression budget, not the current
protocol limit of every ICP subnet.

The default checked samples are HelloWorld, Counter, HelloWeb, HelloFetch, and
HelloChat. Bzzz and advanced ASP.NET examples are not in that list. A successful
budget check does not establish their size or installability.

## Reproduce from the repository root

```bash
docker build --platform linux/amd64 -t wasp-dotnet-build:latest \
  -f aot/docker/Dockerfile.build aot/docker
cargo build --release --manifest-path shared/tools/wasi-stub/Cargo.toml
bash .github/workflows/build-canisters.sh
```

The host also needs `wasm-tools` and `ic-wasm`; see
[toolchain setup](../../CONTRIBUTING.md#toolchain). To investigate one sample:

```bash
SAMPLES=HelloWorld bash .github/workflows/build-canisters.sh
wasm-tools dump aot/samples/HelloWorld/HelloWorld.canister.wasm
```

If size increases, inspect trimming warnings, new dependencies, feature switches,
and compiler-version changes. Compare the same source/configuration and record
the Docker image ID. Avoid replacing a deployed artifact during this investigation.

For a temporary diagnostic run only:

```bash
SIZE_BUDGET_BYTES=20971520 bash .github/workflows/build-canisters.sh
```

A permanent change requires updating the workflow and documenting the measured
reason. Increasing this budget does not change any IC platform limit.
