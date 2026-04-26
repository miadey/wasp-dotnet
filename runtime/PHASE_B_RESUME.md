# Phase B resume plan

Standalone plan to pick up Phase B work after clearing the session.
Last commit: `12fdfc2` "Phase B: PIN-POINT trap to dn_simdhash function pointers"

## TL;DR

We're hosting Microsoft's `dotnet.native.wasm` (the .NET 10 Mono runtime)
inside an ICP canister via a Rust shim + wasm-merge. **Mono boots and
executes inside the canister.** The single remaining blocker is a
deterministic trap on the 2nd `mono_wasm_add_assembly` call. We've
pinpointed it to `dn_simdhash` calling the wrong function via
`call_indirect` after wasm-merge — likely because function pointers
embedded in dotnet's static data section weren't relocated. The
concrete fix is a new wasm-tools pass; this plan walks through writing
and validating it.

## Verify current state (5 minutes)

```bash
cd /Users/miadey/dev/csharp/runtime

# 1. Build pipeline still works
./scripts/20_build_canister.sh
./scripts/30_merge.sh
# Expected: "merged canister: ~5 MB", "wasm-tools validate: VALID"

# 2. Canister installs and basic queries work
dfx canister install wasp_runtime --mode reinstall --yes \
    --wasm wasp_canister/canister.wasm
dfx canister call wasp_runtime ping     # → (blob "pong")
dfx canister call wasp_runtime hello    # → (blob "booted=false assemblies=0")

# 3. Reproduce the trap
dfx canister call wasp_runtime synth_add  # → success
dfx canister call wasp_runtime synth_add  # → trap "heap out of bounds"
```

## What's already shipped (committed in `runtime/`)

- 7-stage build pipeline: cargo → wasm-merge → multi-memory-lowering →
  wasm-const-lower → wasm-table-merge → icp-publish → wasi-stub →
  wasm-relax-simd-binary
- `runtime/scripts/webcil_to_dll.py` — extracts real PE .dll bytes from
  publish webcil-wrapped .wasm files (works on all 33 BlazorChat
  publish artifacts; output is in `runtime/inputs/bcl_extracted/`)
- `runtime/scripts/inject_dump_global_7.py` — hijacks the
  `canister_query ping` export to inject inline wat reading any global
  and reply with hex
- `runtime/scripts/inject_call_trace.py` — instruments any wasm
  function with `debug_print` before every internal `call N`
- `shared/tools/wasm-relax-simd/relax_binary.py` — direct binary
  Code-section walker, can be a template for the relocation pass
- Custom binary protocol replacing candid (no_std + custom panic
  handler + Mono-malloc-backed Rust allocator)
- Per-update orchestration endpoints: `register_one`, `synth_add`,
  `static_add`

## The actual bug (definitively proven this session)

`mono_wasm_add_assembly` (fn 1273 in our merged wasm) calls
`bundled_resources_get_assembly_resource` (fn 5663) which calls
`bundled_resources_get` (fn 5311) which calls
`dn_simdhash_ght_get_value_or_default` (fn 1019).

fn 1019 contains:

```wat
local.get 1                         ;; key
global.get 7
local.get 0                         ;; simdhash struct ptr
i32.add
i32.load offset=44 align=1          ;; HASH FUNCTION POINTER from struct
call_indirect (type 6)              ;; INDIRECT CALL
local.set 4                         ;; store hash result
```

The hash function pointer was stored in the simdhash struct at
construction time when `dn_simdhash_ght_new_full(hash_fn, equal_fn,
...)` ran in `mono_bundled_resources_add`. The `hash_fn` argument
was a static C function-pointer reference
(`&bundled_resources_resource_id_hash`).

In wasm, function pointers are TABLE INDICES. Microsoft compiled
dotnet.native.wasm with specific table indices. After our
`wasm-merge` + `wasm-table-merge` pipeline reordered the table, the
indices stored in dotnet's static data section MAY no longer point at
the correct functions.

Trap evidence (from `inject_call_trace.py` runs):

| Call | synth_add #1 (works) | synth_add #2 (traps) |
|---|---|---|
| fn 1273 sites 0-2 (strlen, strncasecmp, strdup) | ✓ | ✓ |
| fn 1273 site 9 (call 5663 = lookup) | ✓ | ✓ |
| fn 5663 site 0 (call 5311 = simdhash get) | returns NULL ✓ | returns non-NULL ✗ |
| fn 1273 site 10 (call 8299 = g_new0) | runs ✓ | NOT REACHED |
| fn 1273 site 14 (call 5646 = g_assertion_message) | NOT REACHED | runs → trap |

So on call #2, `dn_simdhash_ght_get_value_or_default("Asm1")` returns
the entry that was stored under "Asm0" — confirming the hash function
is broken. Both keys normalize differently via `key_from_id` (verified
by reading fn 2862), so the bug is downstream in fn 1019's call_indirect.

## What's NOT the bug (already eliminated)

| Hypothesis | Test | Result |
|---|---|---|
| webcil-wrapped corelib | extracted real PE | Mono got further but still trapped |
| AOT mode mismatch | strings `dotnet.native.wasm` | not pure-AOT, has interpreter |
| MONO_DEBUG bad value | empty string + traced parser | no-op confirmed |
| SIMD alignment | binary-relaxed all 122,578 memargs | same trap pattern |
| mono malloc OOM | 100-iter alloc/free pre-call | passes |
| Stack pointer reset | one IC update per add_assembly | same trap |
| Pointer convention (subtract global_7) | tested both directions | same trap |
| Memory pressure | pre-grow 256 MiB or none | same trap |
| Trace logger ABI | no-op'd it | same trap |
| Pointer provenance | static data, malloc'd, persistent Vec | same trap |

## Next-session work plan (1-2 focused days)

### Step 1: Confirm the bug (2-3 hours)

Extend `inject_call_trace.py` to also instrument fn 1019 and capture
the call_indirect target value. Specifically, before the `call_indirect
(type 6)` in fn 1019, dup the function index, print it, then call.
That tells us EXACTLY which function the hash lookup is calling.

Run: 1st synth_add (succeeds — but no lookup happens since hash is
empty; need to verify by reading fn 1019's code path), then 2nd
synth_add (traps). The printed function index from synth_add #2's
hash call is the smoking gun.

Cross-reference that index with the element segment in the merged wasm
(`grep -E "^\s*\(elem " /tmp/canister.wat`) to determine what function
is at that index. If it's NOT
`bundled_resources_resource_id_hash` (which would be a
MurmurHash3-style function), the bug is confirmed.

### Step 2: Write the relocation pass

Create `shared/tools/wasm-fnptr-fixup/fixup.py`:

1. **Pre-merge analysis**: take `runtime/inputs/dotnet.native.wasm`
   alone, parse its element segment to determine:
   - Original function table indices for all funcs that get
     referenced as data (this is the hard part — wasm doesn't mark
     which data-section bytes are function pointers)

   Approach: use ELF-style relocations. dotnet.native.wasm was
   built by emscripten which produces a `linking` section with
   relocation info IF compiled with `-r` flag. Check via:
   `wasm-tools print runtime/inputs/dotnet.native.wasm | grep linking`

   If no linking section, fall back to:
   - Find every `i32.const N` in dotnet's CODE that's later passed
     to a function expected to receive a function pointer (signatures
     of dn_simdhash_ght_new_full, etc.)
   - Trace those constants back to where they're stored in static data
   - Or simpler: find every i32 in dotnet's data segment where
     0 < i32 < element_segment_size and the function at that index
     has a hash-function-like signature `(i32) → i32`

2. **Post-merge analysis**: parse merged canister.wasm's element
   segment and function-index renumbering to produce an old→new map

3. **Apply fixup**: scan the merged data section, for each candidate
   pointer-bearing offset, replace old index with new index

This is the genuinely hard step. Estimated 6-10 hours of careful work.

### Step 3: Verify the fix

After applying the fixup pass:

```bash
cd /Users/miadey/dev/csharp/runtime
dfx canister install wasp_runtime --mode reinstall --yes \
    --wasm wasp_canister/canister.wasm

# Should now succeed N times instead of failing on call 2
for i in 1 2 3 4 5; do
    dfx canister call wasp_runtime synth_add
done
```

If all 5 succeed, the relocation pass works. Then:

```bash
# Try the real thing: register all 35 BCL dlls + boot
./scripts/40_upload_and_boot.sh
```

If `boot` reaches `mono_wasm_load_runtime` AND Mono completes corlib
init (no `[mono] (empty)` + exit(1)), we have a fully booted Mono
inside an ICP canister. From there, `mono_wasm_assembly_load("WaspHost")`
should work, and `mono_wasm_invoke_jsexport` lets us call C# methods.

### Step 4 (if step 2 too hard): Workaround paths

Two genuine alternatives if the relocation pass turns out to be more
than 2 days of work:

**A. Replace dn_simdhash entirely**
Patch `dotnet.native.wasm` to redirect calls into
`mono_bundled_resources_add` to call OUR Rust implementation that
uses a simple linear scan instead of dn_simdhash. ~200 lines of Rust,
~1 day.

**B. Use a different wasm linker**
Try `wasm-ld` (LLVM linker) instead of binaryen's `wasm-merge`. wasm-ld
supports proper PIC linking with relocation. May avoid the bug
entirely. ~1 day to integrate, but risk that other things break.

## Tasks already filed in /Users/miadey/.claude/...

- #58: Write data-section function-pointer relocation pass
  (this is exactly the Step 2 above)

## Files most relevant to this work

| File | Purpose |
|---|---|
| `runtime/PHASE_B_RETRO.md` | full multi-session diagnostic history |
| `runtime/wasp_canister/src/lib.rs` | Rust shim with `register_one`, `synth_add` |
| `runtime/scripts/30_merge.sh` | the 7-stage pipeline |
| `runtime/scripts/inject_call_trace.py` | the diagnostic that pinpointed the bug |
| `shared/tools/wasm-relax-simd/relax_binary.py` | template for the relocation pass — same Code-section walker pattern |
| `runtime/inputs/dotnet.native.wasm` | the binary we're hosting |
| `runtime/inputs/System.Private.CoreLib.dll` | extracted via webcil_to_dll.py |
| `runtime/inputs/bcl_extracted/*.dll` | 33 trimmed BCL deps |

## Critical environment notes

- Build with cleaned env: `env -i HOME="$HOME" PATH="..." ./scripts/...`
  to avoid wasp-php-85 environment leakage
- dfx must be running locally (`dfx start --background --clean` from
  `runtime/` if not)
- The merged canister.wasm is ~5 MB and takes ~30s to install
- Each `register_one` is a separate IC update (~2s consensus latency)
- Trap reproducer: `synth_add` works once, fails second call

## Phase B success criterion

When `mono_wasm_load_runtime` returns successfully (no exit(1) trap)
and `mono_wasm_assembly_load("WaspHost")` returns a non-NULL value,
Phase B is complete. Then Phase C (Bridge.Dispatch + Candid encoding +
attribute-based routing) can begin.
