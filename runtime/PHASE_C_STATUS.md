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

## Mono source confirmed (agent research)

`mono_assembly_load_corlib` source: `src/mono/mono/metadata/assembly.c:2675`
on release/10.0.

Tries in order:
1. `invoke_assembly_preload_hook(req.alc, aname, NULL)` where
   `aname = mono_assembly_name_new("System.Private.CoreLib")` (no .dll)
2. If `MONO_PATH` set: `load_in_path("System.Private.CoreLib.dll", ...)`
3. **Fallback**: `mono_assembly_request_open("System.Private.CoreLib.dll", ...)`
   — this is the bundle path
4. Webcil variants if `ENABLE_WEBCIL`
5. If still NULL: `g_assert(corlib)` at line 2718 → exit(1).

Bundled-resources lookup (`bundled-resources.c:78-107`) uses
`key_from_id` which strips known extensions (`.dll`, `.webcil`,
`.wasm`) and re-appends `dll`. Hash via `MurmurHash3_32_streaming`,
equality via plain `strcmp` — **case sensitive, no path stripping**.

`mono_wasm_add_assembly` (`src/mono/browser/runtime/driver.c:107`)
calls `mono_bundled_resources_add_assembly_resource(name, name, ...)`
with the name AS-IS — no normalization.

So canonical key = bare `<Name>.dll`. **We register
`"System.Private.CoreLib.dll"` already.** Name match should be
exact. The g_assert is firing for some OTHER reason than name
mismatch.

## Remaining hypotheses (after agent research)

1. **Cached bytes pointer staleness**: multi-memory-lowering's
   `emscripten_resize_heap` does `memory.copy` + `global.set 430` —
   it MOVES dotnet's memory base when growing. Pointers cached
   before a grow become stale (point to physically-moved data). Our
   `add1` caches `ADD1_CACHED_BYTES`; if a grow happens between the
   register_chunk-loop messages and `boot_mono`, mono dereferences
   the cached ptr at the wrong location.
2. **dn_simdhash table state corruption from asyncify**: asyncify
   instruments the bundled-resources insert chain. If the rewind
   doesn't perfectly restore some non-local state (e.g. the table's
   bucket-array ptr stored in a struct field), the bucket pointer
   itself might end up stale.
3. **Bundled-resources hook not actually installed**: mono's
   preload hook chain (`mono_install_assembly_preload_hook`) may
   not be wired up if some init step we skipped or chunked broke
   the hook installation.

## Probe pipeline diagnostic — round 2

Reproduced step-by-step: applying each post-asyncify patch script
individually (patch_fn_to_call asyncify_get_state, patch_fn_to_global_get
g7, patch_fn_to_global_get mem_base, patch_fn_return_zero pdb,
patch_fn_to_call monoeg_g_print, patch_fn_to_call probe→bundled_get)
ON A FRESH ASYNCIFIED WAT preserves probe_bundled_get count = 5.

But the ACTUAL `30_merge.sh` run produces canister.wasm with probe
count = 0. Latest run shows the pipeline STOPS partway with
`grep: ... No such file or directory` for a wasp-wat temp file —
likely a script bug introduced when adding the probe-patch step
(temp file cleanup trap fires too early, or one of the new variable
references is unset).

So the immediate fix is the script bug, not a wat-manipulation bug.
Need to:
1. Inspect `30_merge.sh` step [8/8] — verify `$WAT` is alive throughout
   all the `*_TOK` greps + the new probe patch.
2. Confirm the trap chain isn't deleting the WAT file before the
   probe-patch step needs it.
3. Re-run after fixing and confirm probe_bundled_get is in the final
   canister.wasm.

## Probe pipeline diagnostic (round 1 — superseded by round 2)

Stage-by-stage trace of `wasp_probe_bundled_get` and
`canister_query probe_bundled_get` exports:

- after wasm-merge: PRESENT
- after multi-memory-lowering: PRESENT
- after const-lower: PRESENT
- after table-merge: PRESENT
- after icp-publish (incl. ic-wasm shrink -k): PRESENT
- after wasi-stub: PRESENT
- after relax-simd: PRESENT
- after inject_yield_call + inject_yield_at_entry + 3× inject_arg_trace: PRESENT
- after wasm-opt --asyncify: PRESENT
- after wasm-tools print/parse round trip: PRESENT
- in final canister.wasm: **MISSING**

So one of the post-asyncify patch scripts (patch_fn_to_call for
asyncify_get_state placeholder, g7-helper, mem_base, mono_has_pdb_checksum
return-zero, monoeg_g_print → wasp_log_g_print hook, or the new
wasp_probe_bundled_get → bundled_resources_get_assembly_resource hook)
silently drops the probe export. Most likely the `text.find("\n  )\n", start)`
boundary detection in patch_fn_to_call.py / patch_fn_return_zero.py /
patch_fn_to_global_get.py finds the wrong closing paren and replaces
across function boundaries, eating later functions including the new
probe.

Next iteration: instrument the patch scripts to print line ranges of
each replacement, find the offending one, fix the boundary detection
to be unambiguous (e.g. require matching indent + check the next func
header is unaffected).

## Diagnostic step that would be most informative next

Write a `canister_update probe_bundled_get` that calls
`mono_bundled_resources_get_assembly_resource(name)` directly with
"System.Private.CoreLib.dll", AFTER register_chunk completes BUT
BEFORE boot_mono. Returns the looked-up value (NULL or a real
struct ptr). That definitively tells us whether registration took.

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
