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

## What's needed next (concrete, not vague)

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
