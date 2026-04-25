# Phase A retrospective — first end-to-end attempt

**Status:** the build pipeline works through every step we designed, and
surfaces one real validation issue at the final step. ICP install fails;
all upstream stages pass.

## What worked ✅

| Stage | Outcome |
|---|---|
| Extract Microsoft inputs (#5) | `runtime/inputs/dotnet.native.wasm` (3.0 MB), `System.Private.CoreLib.dll` (1.7 MB), `boot.json` (688 KB) — all from a `dotnet publish` of the Phase 4 BlazorChat sample, SDK 10.0.202 |
| Stub 75 env imports (#6) | `env_imports.rs` — 75 `#[no_mangle] extern "C"` functions; compiles clean for wasm32-unknown-unknown; all signatures verified against `wasm-tools print` of the actual import section |
| Stub 10 wasi imports (#7) | `wasi_imports.rs` — handled by our existing `shared/tools/wasi-stub` Rust binary post-merge |
| Mono FFI declarations (#8) | `mono_embed.rs` — 24 extern decls covering `mono_wasm_load_runtime`, `mono_wasm_assembly_load`, `mono_wasm_invoke_jsexport`, etc. |
| `lib.rs` canister entry points (#9) | `#[init]`, `#[query] hello`, `#[update] upload_chunk`, `#[query] list_assemblies` — all build, all show up as canister exports post-merge |
| `wasm-merge` resolves env imports (#10, partial) | After naming wasp_canister as `env`, **all 75 env imports drop to 0**. dotnet.native's references to `env::abort` etc. successfully bind to our Rust stubs |
| Multi-memory lowering | `wasm-opt --multi-memory-lowering` correctly fuses the two memory sections into one |
| Canister export naming | `icp-publish.sh` correctly renames `canister_query__hello` → `canister_query hello` (with literal space) |
| wasi-stub final pass | All 10 leftover wasi imports get no-op stubs |

## The blocker ❌

`wasm-tools validate` and `dfx canister install` both reject the final
3.8 MB merged wasm with:

```
error: constant expression required: global.get of mutable global
       (at offset 0x2e60d4)
```

### Root cause

`dotnet.native.wasm` is built by Microsoft against the **WebAssembly
extended-const proposal** (`--enable-extended-const`), which permits
data-segment offsets like `(data (global.get $heap_base) ...)` even when
`$heap_base` is mutable. ICP's `wasmtime` validator does **not** enable
this proposal yet, so post-merge data-segment offsets that survived
unchanged become invalid.

Specifically: dotnet.native.wasm has roughly 10–20 data segments whose
init exprs are `global.get $__memory_base` (mutable). After `wasm-merge`
these are reachable in the merged module and the validator complains.

### Fix candidates (Phase A iteration 2)

1. **Pre-process `dotnet.native.wasm`** with a binaryen pass that
   converts `global.get $base + i32.const offset` into a single
   `i32.const (base+offset)` literal. May require knowing the static
   base, which is fixed at link time anyway.
2. **Replace mutable globals with immutable ones** in dotnet.native.wasm
   via wasm-tools: dump WAT, sed `(global $... (mut i32) ...)` →
   `(global $... i32 ...)` for the offset globals only, re-parse.
3. **Wait for ICP to enable wasm-extended-const** in wasmtime. DFINITY
   tracks wasm proposal support; this one is at Stage 4 / Phase 5 in
   wasm WG terms. Likely 6+ months from now.
4. **Use `wasm-opt --strip --remove-unused-module-elements`** + manual
   pass to inline the const exprs. Worth experimenting with.

Recommend candidate 1 or 2 — both are surgical wat transforms a Rust
helper could do in <100 lines. Add as a new build-pipeline step
between wasm-merge and multi-memory-lowering.

## Bottom-line numbers

| Metric | Value |
|---|---|
| `wasp_canister.wasm` (Rust shim) | 552 KB |
| `dotnet.native.wasm` (Microsoft) | 3.0 MB |
| post-merge | 3.59 MB |
| post-multi-memory-lowering | 3.84 MB |
| post-wasi-stub | ~3.6 MB |
| imports remaining | 7 ic0 (correct) + 0 env + 0 wasi |
| canister exports | `canister_init`, `canister_query hello`, `canister_query list_assemblies`, `canister_update upload_chunk` ✓ |
| time from `cargo build` to merged wasm | ~5 sec |

## GO/NO-GO verdict

**GO with one more iteration.** The architecture works. The blocker is a
known wasm-proposal compatibility issue with a clear fix path (one of
the 4 candidates above). Estimate 3–5 days to land the
extended-const-lowering pass and get the e2e `dfx canister call wasp
hello` working. After that, Phase B (the friendly C# CDK) can begin.

## Next concrete steps

1. New issue: "wasm-extended-const lowering pass — rewrite mutable
   `global.get` data offsets to literal `i32.const`" (label: runtime,
   priority/critical)
2. Phase A is **not** closed yet — issue #11 stays open until the
   blocker is resolved and a managed `Console.WriteLine` reaches `dfx
   canister logs`.
3. Phase A retro doc (issue #12) is this file. Closing #12.

— retro committed alongside the merge-pipeline scripts in commit
   $(git rev-parse --short HEAD)
