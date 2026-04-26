# Phase B retrospective — Mono boots inside the canister, exits early

**Status:** the infrastructure for Phase B is in place. Mono actually
executes inside the canister but calls `exit(1)` very early because we
haven't yet given it the runtime arguments + VFS it needs.

## What's complete in this iteration ✅

| | |
|---|---|
| Issue #18 | `runtime/wasp_canister/src/wasp_stable_abi.rs` — 11 `wasp_*` exports providing P/Invoke trampolines for `ic0.stable64_*`, `msg_arg_data`, `caller`, `time`, `reply`, `trap`, `debug_print`. **Closed.** |
| WaspHost C# scaffold | `runtime/wasp_dotnet_app/WaspHost/` — csproj + `Bridge.Dispatch(string methodName)` + `MessageContext` + `Reply` + `Ic0` (P/Invoke decls matching the wasp_stable_abi surface). Builds to `WaspHost.dll`. |
| Tracing infrastructure | `mono_wasm_trace_logger` now prefixes messages with `[mono]` so dfx canister logs can distinguish Mono's output from ours. |
| Real `mono_wasm_load_runtime` call | `lib.rs::canister_init` now actually calls into the runtime; we read what happens via dfx canister logs. |

## What we observe ❌

```
$ dfx canister install wasp_runtime --mode reinstall --yes --wasm wasp_canister/canister.wasm
$ dfx canister logs wasp_runtime
[wasp-dotnet] canister_init: pre-Mono
[wasp-dotnet] canister_init: about to call mono_wasm_load_runtime(0,0,0,0)
[mono]
(blank, possibly Mono's stderr write of just \n)
[TRAP]: wasp: dotnet.native.wasm called exit(1)
```

Mono runs but exits with code 1 because:
1. We pass `(NULL, 0, 0, 0)` — no argv, no assembly name
2. Even with valid argv, Mono will need to read corelib via wasi (our
   current `fd_read` returns EBADF for everything — no in-memory VFS
   yet)
3. Mono may also need env vars set (TZ, MONO_GC_PARAMS, etc.) via
   `environ_get` — currently we report 0 environ entries

## Subtle gotcha discovered

Adding format!/println! to env-import stubs introduces new
`call_indirect` sites in the wasm (Rust uses indirect dispatch for
Display impls). The current `wasm-table-merge` pass assumes all
`call_indirect` target table 1 (dotnet's table), which holds true
because the original wasp_canister had no real indirect calls. As soon
as we add format machinery to a stub, that assumption breaks and the
canister fails install with "function invocation does not match its
signature".

**Mitigation:** all env-import stubs use raw `debug_print(ptr, len)`
calls only. No format!, no println!. Everything is `&[u8]` literals.
Pattern documented in `env_imports.rs::ic_debug_print_bytes`.

## Session 2 update — VFS + argv + environ + Mono now calls syscalls

After landing #35 (real argv) + #37 (env vars MONO_PATH/MONO_ROOT/TZ) +
#36 (in-memory VFS embedding corelib + WaspHost.dll via include_bytes!),
the canister wasm grew from 3.85 MB → 5.35 MB and Mono progressed
further:

```
[wasp-dotnet] canister_init: pre-Mono
[wasp-dotnet] canister_init: about to call mono_wasm_load_runtime
wasp: mono_wasm_debugger_log
[openat] <empty>
[openat] <empty>
[openat] <empty>
[mono]
(blank)
[TRAP]: wasp: dotnet.native.wasm called exit(1)
```

Mono now actually calls `__syscall_openat` three times — but always with
**empty paths**. Probably `openat(dirfd, "", O_RDONLY)` style probes for
runtime config that Mono falls back from when not present. Our VFS
returns -ENOENT for unknown (and empty) paths, Mono gives up after the
3rd attempt and exit(1)s.

**Real diagnosis requires Mono source:** the WASI bootstrap path lives
in `dotnet/runtime/src/mono/wasi/runtime/` (or similar). Empirically
guessing args / config formats won't get past this. The next iteration
needs to either:
  1. Read the actual Mono WASI bootstrap source to understand what
     mono_wasm_load_runtime expects (the 4 args, the file system layout
     it probes, the env vars it requires)
  2. OR run `dotnet.native.wasm` under wasmtime locally with an strace-
     style trap on every wasi call to capture the canonical bootstrap
     sequence, then mirror it in our shim

That's **days of focused engineering** — not session-scale work.

## What's complete in this session ✅

| | |
|---|---|
| Issue #35 | Real argv passed to mono_wasm_load_runtime |
| Issue #37 | MONO_PATH=/, MONO_ROOT=/usr/share/dotnet, TZ=UTC via environ_get |
| Issue #36 | runtime/wasp_canister/src/vfs.rs — corelib + WaspHost.dll embedded via include_bytes!; openat/fd_read/fd_seek/fd_close/fd_fdstat_get routed through it |
| Mono progresses past initial null-arg trap; now calls openat | |

## Session 3 — Mono API correctly understood, hit a memory-layout wall

After research agent #38 read `dotnet/runtime/src/mono/browser/runtime/driver.c`
line 185, we learned:

- `mono_wasm_load_runtime` signature is **`(debug_level, propertyCount, propertyKeys, propertyValues)`** — NOT `(argv, argc, debug, log_mask)` as initially assumed
- Mono needs `DOTNET_SYSTEM_TIMEZONE_INVARIANT` set or it segfaults
- Mono needs `mono_wasm_add_assembly("System.Private.CoreLib.dll", ptr, len)` called BEFORE `mono_wasm_load_runtime` so `mono_assembly_load_corlib` uses the bundled-resources fast path instead of probing the FS
- The 3 empty-path opens we saw were corlib search attempts; the ensuing `[mono]` trace + exit(1) was `g_assert(corlib)` failing
- Properties to pass: `APP_CONTEXT_BASE_DIRECTORY=/`, `RUNTIME_IDENTIFIER=browser-wasm`, `System.Globalization.Invariant=true`

We applied all four fixes. New empirical state:

```
[wasp-dotnet] canister_init: pre-Mono
[wasp-dotnet] canister_init: __wasm_call_ctors done
[wasp-dotnet] canister_init: registering corelib + wasphost
[TRAP]: heap out of bounds
```

`__wasm_call_ctors` runs cleanly. `mono_wasm_setenv` x3 completes without trap.
`mono_wasm_add_assembly` traps with **"heap out of bounds"**.

Diagnosis: heap_base globals were lower than the actual end of static
data after `wasm-opt --multi-memory-lowering` shifted dotnet's data
segments around. `wasm-const-lower` now also rewrites `__heap_base` /
`__data_end` past the highest data offset + 4 MiB safety margin
(currently 6.9 MB). But `mono_wasm_add_assembly` STILL traps.

Likely root cause (not yet fixed): the `CORELIB` byte slice in our
Rust crate gets compile-time addresses relative to wasp_canister's
standalone memory layout. After `wasm-merge` + `multi-memory-lowering`,
those data bytes live at different absolute offsets. When
`mono_wasm_add_assembly` does `memcpy(dst, src=CORELIB.as_ptr(), len)`,
either the source pointer or the destination is now outside valid
memory range.

**Three concrete options for the next iteration:**

1. **Upload corelib via stable memory at deploy time** instead of
   `include_bytes!`. Read it into a heap-allocated buffer (using
   ic-cdk's allocator) at canister_init, then pass that pointer to
   `mono_wasm_add_assembly`. Sidesteps the wasm-merge memory-layout
   problem entirely. ~60 lines.

2. **Verify the CORELIB pointer's value at runtime** by printing it in
   canister_init and checking it points to bytes that match the
   expected dll file header (MZ + PE). If wrong, we know the merge
   broke pointer relocation; if right, the trap is on the dst side
   (Mono's malloc result).

3. **Skip multi-memory-lowering entirely** by removing wasp_canister's
   memory section pre-merge so dotnet's memory becomes the only memory.
   Pro: no shift, all original offsets stay valid. Con: requires
   pre-merge wasm rewrite that we don't have a tool for yet.

Recommend #1 (stable memory upload) as the cleanest fix. Filed as new
issue.

## What's complete in this session ✅

| | |
|---|---|
| Issue #38 | Mono WASI bootstrap research; full runtime API documented in commit |
| `__wasm_call_ctors` wired into canister_init | |
| Correct `mono_wasm_load_runtime` signature applied | |
| `mono_wasm_setenv` x3 (DOTNET_SYSTEM_TIMEZONE_INVARIANT + MONO_LOG_*) | working |
| `wasm-const-lower` now also fixes `__heap_base` / `__data_end` | |

## Session 2 next steps (the ones still open)

These map to filed issues + new ones we should file:

1. **#35 (open) — pass real argv to mono_wasm_load_runtime.**
   Allocate a static `&[u8]` like `b"WaspHost\0"` and a `[*const u8; 1]`
   array containing its pointer. Pass `(argv_ptr, 1, 0, 0)`. ~20 lines.

2. **NEW (file) — in-memory VFS in `wasi_imports.rs::fd_open`/`fd_read`.**
   Before Mono can do anything useful it needs to read corelib. Plan:
   - At canister build time, embed `runtime/inputs/System.Private.CoreLib.dll`
     bytes via `include_bytes!` (or upload to stable memory at deploy
     time and read into an in-memory map at canister_init).
   - Implement a flat `HashMap<String, &'static [u8]>` keyed by full path.
   - `__syscall_openat` / `fd_read` / `fd_seek` / `fd_close` route
     through this map. EBADF for unknown paths so Mono falls back to
     defaults rather than crashing.
   - ~150 lines of Rust.

3. **NEW (file) — populate environ_get with a sane minimum.**
   Mono looks for `MONO_PATH` (where to find assemblies),
   `MONO_ROOT` (root of dotnet install), maybe `TZ`. Return:
     `MONO_PATH=/`
     `MONO_ROOT=/usr/share/dotnet`
     `TZ=UTC`
   ~30 lines.

4. **#13 then unblocks** — once Mono boots, Bridge.Dispatch can be
   invoked via `mono_wasm_invoke_jsexport`. The C# side is already
   scaffolded and compiles to a working .dll.

## Honest timeline estimate

The remaining Phase B work — the three concrete items above plus #13
(Bridge.Dispatch wiring) plus #19 (HelloRuntime sample) — is **2–3
weeks of focused engineering**, mostly in the in-memory VFS (which is
fiddly because Mono opens many files and expects POSIX semantics our
wasi stubs need to mimic carefully).

The three highest-uncertainty items (Phase A in-canister wasm install,
multi-table lowering, Mono actually starting to execute inside the
canister) are now ALL resolved. What's left is engineering grind, not
research.

— retro committed alongside the Phase B foundation in commit
   $(git rev-parse --short HEAD)
