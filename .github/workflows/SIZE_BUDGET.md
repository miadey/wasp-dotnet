# Canister size budget

CI enforces a **10 MiB (10,485,760 byte) per-canister code-section budget**
on every `*.canister.wasm` produced by the AOT pipeline. If a build exceeds
this limit the `aot-canisters` job fails with a message like:

```
[HelloChat] code section 11534336 bytes exceeds budget of 10485760 bytes (10 MiB).
```

## Why 10 MiB

The Internet Computer protocol allows much larger Wasm modules, but our
samples today are all comfortably under 1.5 MiB:

| sample      | approx. size |
| ----------- | ------------ |
| HelloWorld  | ~860 KB      |
| Counter     | ~1.0 MB      |
| HelloWeb    | ~930 KB      |
| HelloFetch  | ~1.0 MB      |
| HelloChat   | ~1.3 MB      |

A 10 MiB cap gives roughly **8x headroom** over the largest sample today —
enough room for organic growth, but small enough that an accidental
regression (a forgotten `<TrimMode>partial</TrimMode>`, a stray
reflection-using dependency, a debug build slipping through) is caught
loudly the moment it lands. Treat any single PR that pushes a sample past
the cap as a regression to investigate, not a budget to bump reflexively.

## Investigating a regression

When the budget check fails, the cause is almost always one of:

1. **Trimming got worse.** A new dependency, or a code change that uses
   reflection, can defeat NativeAOT's tree-shaking. Check the build
   warnings for `IL2026` / `IL2070` / `IL3050` — those are trim and AOT
   compatibility warnings.

2. **A new BCL surface area got pulled in.** `System.Text.Json`,
   `System.Net.Http`, and `System.Reflection.Emit` are common culprits —
   each can add several MB.

3. **Optimisation flags got dropped.** Confirm the failing build is
   running with `-c Release` and `/p:IlcLlvmTarget=wasm32-wasi`, and that
   no `ILCompilerArguments` override is forcing `--Od` or
   `--debuginfo:full`.

### Reproducing the size check locally

```bash
# Build the docker image once
docker build -t wasp-dotnet-build:latest aot/docker

# Build the wasi-stub tool once
cargo build --release --manifest-path shared/tools/wasi-stub/Cargo.toml

# Re-run the same script CI runs
bash .github/workflows/build-canisters.sh
```

To override the budget temporarily while diagnosing:

```bash
SIZE_BUDGET_BYTES=$((20 * 1024 * 1024)) bash .github/workflows/build-canisters.sh
```

### Inspecting what got bigger

```bash
# Section breakdown (code section is the one we cap)
wasm-tools dump aot/samples/HelloChat/HelloChat.canister.wasm | head -40

# Function-level breakdown — the largest functions first
wasm-tools objdump -j Code aot/samples/HelloChat/HelloChat.canister.wasm \
  | sort -k3 -n -r | head -30

# Diff against main to see which functions grew
git stash
bash .github/workflows/build-canisters.sh
cp aot/samples/HelloChat/HelloChat.canister.wasm /tmp/before.wasm
git stash pop
bash .github/workflows/build-canisters.sh
twiggy diff /tmp/before.wasm aot/samples/HelloChat/HelloChat.canister.wasm
```

### Raising the budget

If a real, justified increase is needed (for example, a new sample that
legitimately needs more code), bump `SIZE_BUDGET_BYTES` in
`.github/workflows/ci.yml` and update the table above in the same PR. The
PR description should explain *why* the new floor is the right one.
