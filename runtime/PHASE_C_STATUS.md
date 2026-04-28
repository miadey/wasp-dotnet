# Phase C status — asyncify chunking + Mono runtime boot

## What works

**Asyncify chunking infrastructure is complete and proven.**

- `--asyncify-onlylist` constrains instrumentation to ~30 functions in the
  dn_simdhash insert/get + bundled-resources chain; bloat is ~60 KB
  (vs 10 MB unbounded), well under IC's 11 MB code section limit.
- Pipeline runs asyncify AFTER multi-memory-lowering (avoids the
  double-mem_base bug in lowering applied to asyncify-emitted loads/stores).
- Wasp imports asyncify control fns from `(import "asyncify" "<fn>")` —
  Binaryen auto-recognizes `start_unwind` / `stop_unwind` /
  `start_rewind` / `stop_rewind` and rewrites them to the internal
  `asyncify_*` runtime. `get_state` is the one fn we patch via a
  placeholder-export shim.
- Rewind handshake works via `maybe_yield`'s state==2 check.
- `register_chunk` is one-BCL-per-call. corelib (1.65 MB) registers
  across 8 messages.
- `boot_mono` runs through `mono_wasm_load_runtime` → `mini_init` →
  `mono_init` → `mono_class_load_from_name` (after assertion-defang).
  ASYNC_DISABLED bypasses maybe_yield during boot so state==1 doesn't
  leak past the asyncify chain.

## Where we're stuck

`mono_assembly_load_corlib` asserts. We saw two trap modes:

1. **With load_corlib defangs** (lines 2718, 2734, 2735, 339): mono
   thinks corelib loaded, progresses into `mono_class_load_from_name` →
   `mono_class_from_name_checked_aux` → `search_modules` →
   `mono_metadata_decode_row`, then traps because it's iterating over
   a half-loaded module with NULL pointers. Defang was a Pyrrhic victory.

2. **Without defang**: original `g_assert(corlib)` fires immediately.
   Mono couldn't actually find corelib in the bundled-resources table.

Arg-tracer (inject_arg_trace.py + wasp_log_str_ptr) shows both
`mono_assembly_request_open` and `bundled_resources_get_assembly_resource`
get called with `p=8130000 str=""` (uninitialized stack slot interpreted
as a string pointer). So mono never even reaches the point where it has
a real corelib name to look up — something earlier in the load_corlib
flow returns NULL/garbage to its caller.

## Top suspects for the remaining issue

1. **mono_wasm_load_runtime args malformed**: The TPA value ("/managed/
   System.Private.CoreLib.dll") may not be parsed correctly. Mono
   splits TRUSTED_PLATFORM_ASSEMBLIES on `;` and per-entry strips paths
   to extract the assembly name. If the parsing produces an empty
   name, every downstream lookup uses an empty name.

2. **mono_alc_get_default returning NULL**: If the default ALC isn't
   set up (init order issue), every assembly load returns NULL.

3. **Bundled-resources registration is in the wrong table**: We
   register via `mono_wasm_add_assembly` which puts the entry in
   `mono_bundled_resources_*` tables. mono_assembly_load_corlib may
   query a different table for "corelib".

4. **Name normalization mismatch**: Mono internally normalizes assembly
   names (case, suffix, etc.) before lookup. Our key
   "System.Private.CoreLib.dll\0" may not match the normalized lookup key.

## Diagnostic approach for next iteration

The most informative single hook would be **`mono_assembly_name_new`** — it's
called early in `load_corlib` to construct the assembly name from the
TPA path. Tracing its first arg shows exactly what string mono parsed.

Alternatively: `monoeg_g_strdup_printf` — captures the format-string
result that becomes the corelib filename.

## Files / scripts of note

- `runtime/scripts/30_merge.sh` — pipeline; defang currently disabled
- `runtime/scripts/inject_arg_trace.py` — single-arg tracer
- `runtime/scripts/inject_yield_at_entry.py` — entry maybe_yield injection
- `runtime/scripts/inject_yield_call.py` — leaf maybe_yield injection
- `runtime/scripts/patch_disable_g_assert.py` — assertion neutralizer
  (regex updated to handle named call refs)
- `runtime/scripts/patch_fn_to_call.py` / `patch_fn_to_global_get.py`
  / `patch_fn_return_zero.py` — wat-level fn-body patches
- `runtime/wasp_canister/src/lib.rs` — the canister; `dotnet_mem_offset`
  helper subtracts mem_base (correct for byte buffers); `dotnet_offset`
  subtracts g7 (correct for ALC slots).
