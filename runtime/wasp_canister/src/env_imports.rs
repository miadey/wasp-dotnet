//! `env_imports.rs` — Rust implementations of the 75 functions that
//! Microsoft's pre-built `dotnet.native.wasm` imports from the wasm `env`
//! module.
//!
//! **What this file is.** In a browser, the JS host (`dotnet.runtime.js`)
//! defines these 75 callbacks and supplies them to the wasm engine via
//! the WebAssembly `imports` object under the `env` key. We are not in a
//! browser — we are inside an Internet Computer canister — so we provide
//! these callbacks in Rust instead. After `wasm-merge` joins our compiled
//! `wasp_canister.wasm` with `dotnet.native.wasm`, the linker resolves
//! every `(import "env" "<name>")` against the matching `#[no_mangle]`
//! `pub extern "C" fn <name>` defined here.
//!
//! **Conservative-stubs philosophy.** The Phase A goal is *runs at all*,
//! not *full POSIX*. Most syscalls return `-ENOSYS` (-38) and are upgraded
//! later as managed code starts hitting them. Time/heap/abort are wired
//! to real `ic0` primitives because Mono needs them on every invocation.
//! C++ EH and Mono jiterpreter hooks trap or no-op — managed exceptions
//! and JIT are out-of-scope for v0.1.
//!
//! **Signatures are exact.** Each function below was generated from the
//! output of `wasm-tools print runtime/inputs/dotnet.native.wasm`. The
//! type indices in the wasm match exactly: change a parameter and
//! `wasm-merge` will refuse the link with a type mismatch.
//!
//! Categories (counts must sum to 75):
//!
//! | Category | Count |
//! |---|---|
//! | Linux-style syscalls (`__syscall_*`) | 30 |
//! | C++ exception ABI                    | 7  |
//! | Time / date                          | 8  |
//! | mmap / msync                         | 3  |
//! | Heap                                 | 2  |
//! | Process abort / exit                 | 3  |
//! | Mono interpreter & jiterpreter hooks | 7  |
//! | JS interop / debugger / locale       | 15 |
//! | **Total**                            | **75** |
//!
//! (The plan listed "32 syscalls" — actual count from this `.NET 10.0.202`
//! build is 30. The plan also listed `__cxa_*` as 5 but the import
//! surface has 7 EH-related entries including `invoke_vi` and
//! `llvm_eh_typeid_for`. Numbers reconciled to what's actually on disk.)

#![allow(clippy::missing_safety_doc)]
#![allow(non_upper_case_globals)]
#![allow(unused_variables)]

use core::sync::atomic::{AtomicU64, Ordering};

// ---------------------------------------------------------------------------
// IC system-API bindings. We declare the small subset of `ic0` we need
// directly rather than pull in `ic-cdk`'s wrappers, because these stubs
// must remain callable from contexts where ic-cdk's executor is not set up
// (e.g. during very-early Mono startup).
// ---------------------------------------------------------------------------

#[link(wasm_import_module = "ic0")]
extern "C" {
    fn time() -> u64;
    fn debug_print(src: u32, size: u32);
    fn trap(src: u32, size: u32) -> !;
}

/// Trap with a static string. Used by `abort`, `exit`, EH, and any
/// other path that should be unreachable in v0.1.
#[inline(never)]
fn ic_trap(msg: &str) -> ! {
    unsafe { trap(msg.as_ptr() as u32, msg.len() as u32) }
}

/// Forward a static string to `ic0.debug_print`. Used by Mono's logging
/// hooks even if the message pointers/sizes Mono passes are ignored.
#[inline(never)]
fn ic_debug_print(msg: &str) {
    unsafe { debug_print(msg.as_ptr() as u32, msg.len() as u32) }
}

/// Try to forward an arbitrary (ptr, len) buffer that Mono hands us to
/// `ic0.debug_print`. Caller swears the buffer is valid for `len` bytes.
#[inline(never)]
/// Slice-friendly version of `ic_debug_print_buf` callable from sibling
/// modules without raw pointer arithmetic. Avoids the format machinery
/// (println!) which the table-merge pass can't lower.
pub fn ic_debug_print_bytes(bytes: &[u8]) {
    if !bytes.is_empty() {
        unsafe { debug_print(bytes.as_ptr() as u32, bytes.len() as u32) }
    }
}

unsafe fn ic_debug_print_buf(ptr: u32, len: u32) {
    if ptr != 0 && len != 0 {
        debug_print(ptr, len);
    }
}

// errno values — Linux ABI, what musl/Emscripten return when failing.
const ENOSYS: i32 = -38; // function not implemented
const EBADF: i32 = -9; // bad file descriptor
const EACCES: i32 = -13; // permission denied
const ENOENT: i32 = -2; // no such file or directory

// ===========================================================================
// 1. Linux-style syscalls (30 functions)
// ---------------------------------------------------------------------------
// Emscripten exposes Linux syscalls under names like `__syscall_openat`,
// matching their POSIX numbers. Mono uses these for its in-engine VFS,
// thread-local storage, and a handful of stat() calls during assembly
// loading. For Phase A we return -ENOSYS for everything and let Mono's
// own fallback paths kick in (it has them — Blazor doesn't have a real
// filesystem either). Future issues add real implementations backed by an
// in-memory VFS over stable memory.
// ===========================================================================

/// faccessat — return 0 for files we know about (mono uses access()
/// to check existence; some code paths use this instead of stat).
#[no_mangle]
pub extern "C" fn __syscall_faccessat(_dirfd: i32, path: i32, _mode: i32, _flags: i32) -> i32 {
    unsafe {
        let p = crate::dotnet_to_abs(path as u32);
        let mut len = 0;
        while *p.add(len) != 0 && len < 200 { len += 1; }
        let mut buf = [0u8; 256];
        let prefix = b"[faccessat] ";
        let mut i = 0;
        for &b in prefix { buf[i] = b; i += 1; }
        for &b in core::slice::from_raw_parts(p, len) {
            if i < buf.len() { buf[i] = b; i += 1; }
        }
        ic_debug_print_bytes(&buf[..i]);
        if super::vfs::lookup(p).is_some() { 0 } else { ENOENT }
    }
}

/// Browser: change current directory. Canister: -ENOSYS.
#[no_mangle]
pub extern "C" fn __syscall_chdir(_path: i32) -> i32 {
    ENOSYS
}

/// Browser: change file mode. Canister: -ENOSYS.
#[no_mangle]
pub extern "C" fn __syscall_chmod(_path: i32, _mode: i32) -> i32 {
    ENOSYS
}

/// Browser: change mode of open fd. Canister: -ENOSYS.
#[no_mangle]
pub extern "C" fn __syscall_fchmod(_fd: i32, _mode: i32) -> i32 {
    ENOSYS
}

/// Browser: file control (locks, flags). Canister: -ENOSYS.
#[no_mangle]
pub extern "C" fn __syscall_fcntl64(_fd: i32, _cmd: i32, _arg: i32) -> i32 {
    ENOSYS
}

/// Browser: open a file at directory. Canister: -ENOENT (no FS).
#[no_mangle]
pub extern "C" fn __syscall_openat(_dirfd: i32, path: i32, _flags: i32, _mode: i32) -> i32 {
    // Mono passes `path` as a DOTNET-RELATIVE pointer (its compiled
    // wasm uses `g7 + ptr` for memory access). Translate before reading.
    unsafe {
        let p = crate::dotnet_to_abs(path as u32);
        let mut len = 0usize;
        let mut all_printable = true;
        while *p.add(len) != 0 && len < 256 {
            let b = *p.add(len);
            if !(0x20..0x7f).contains(&b) { all_printable = false; }
            len += 1;
        }
        if len > 0 && all_printable {
            let mut buf = [0u8; 280];
            let prefix = b"[openat] ";
            let mut i = 0;
            for &b in prefix { buf[i] = b; i += 1; }
            for &b in core::slice::from_raw_parts(p, len) {
                if i >= buf.len() { break; }
                buf[i] = b;
                i += 1;
            }
            ic_debug_print_bytes(&buf[..i]);
        }
        let fd = super::vfs::open_path(p);
        if fd < 0 { -2 /* -ENOENT */ } else { fd }
    }
}

/// Browser: device-specific control. Canister: -ENOSYS.
#[no_mangle]
pub extern "C" fn __syscall_ioctl(_fd: i32, _request: i32, _arg: i32) -> i32 {
    ENOSYS
}

/// stat by open fd — route through VFS so g_file_test / fopen can
/// see the file's size for our virtual /managed/*.dll paths.
#[no_mangle]
pub extern "C" fn __syscall_fstat64(fd: i32, statbuf: i32) -> i32 {
    unsafe {
        let buf_abs = crate::dotnet_to_abs(statbuf as u32) as *mut u8;
        if super::vfs::stat_fd(fd, buf_abs) == 0 { 0 } else { EBADF }
    }
}

/// stat by path — route through VFS. Returns 0 + populated statbuf
/// for virtual paths, -ENOENT otherwise.
#[no_mangle]
pub extern "C" fn __syscall_stat64(path: i32, statbuf: i32) -> i32 {
    unsafe {
        let p = crate::dotnet_to_abs(path as u32);
        let mut len = 0;
        while *p.add(len) != 0 && len < 200 { len += 1; }
        let mut buf = [0u8; 256];
        let prefix = b"[stat] ";
        let mut i = 0;
        for &b in prefix { buf[i] = b; i += 1; }
        for &b in core::slice::from_raw_parts(p, len) {
            if i < buf.len() { buf[i] = b; i += 1; }
        }
        let buf_abs = crate::dotnet_to_abs(statbuf as u32) as *mut u8;
        let r = super::vfs::stat_path(p, buf_abs);
        let suffix: &[u8] = if r == 0 { b" -> OK" } else { b" -> ENOENT" };
        for &b in suffix {
            if i < buf.len() { buf[i] = b; i += 1; }
        }
        ic_debug_print_bytes(&buf[..i]);
        if r == 0 { 0 } else { ENOENT }
    }
}

/// stat at directory fd — same as stat_path (we ignore dirfd for our
/// flat absolute-path VFS).
#[no_mangle]
pub extern "C" fn __syscall_newfstatat(_dirfd: i32, path: i32, statbuf: i32, _flags: i32) -> i32 {
    unsafe {
        let p = crate::dotnet_to_abs(path as u32);
        let mut len = 0;
        while *p.add(len) != 0 && len < 200 { len += 1; }
        let mut buf = [0u8; 256];
        let prefix = b"[newfstatat] ";
        let mut i = 0;
        for &b in prefix { buf[i] = b; i += 1; }
        for &b in core::slice::from_raw_parts(p, len) {
            if i < buf.len() { buf[i] = b; i += 1; }
        }
        ic_debug_print_bytes(&buf[..i]);
        let buf_abs = crate::dotnet_to_abs(statbuf as u32) as *mut u8;
        if super::vfs::stat_path(p, buf_abs) == 0 { 0 } else { ENOENT }
    }
}

/// lstat (no symlink follow) — same path as stat for our VFS.
#[no_mangle]
pub extern "C" fn __syscall_lstat64(path: i32, statbuf: i32) -> i32 {
    unsafe {
        let p = crate::dotnet_to_abs(path as u32);
        let mut len = 0;
        while *p.add(len) != 0 && len < 200 { len += 1; }
        let mut buf = [0u8; 256];
        let prefix = b"[lstat] ";
        let mut i = 0;
        for &b in prefix { buf[i] = b; i += 1; }
        for &b in core::slice::from_raw_parts(p, len) {
            if i < buf.len() { buf[i] = b; i += 1; }
        }
        ic_debug_print_bytes(&buf[..i]);
        let buf_abs = crate::dotnet_to_abs(statbuf as u32) as *mut u8;
        if super::vfs::stat_path(p, buf_abs) == 0 { 0 } else { ENOENT }
    }
}

/// Browser: truncate file by fd. Canister: -EBADF. Note 64-bit length.
#[no_mangle]
pub extern "C" fn __syscall_ftruncate64(_fd: i32, _length: i64) -> i32 {
    EBADF
}

/// Browser: get current working directory. Canister: -ENOSYS.
#[no_mangle]
pub extern "C" fn __syscall_getcwd(_buf: i32, _size: i32) -> i32 {
    ENOSYS
}

/// Browser: mkdir at directory fd. Canister: -EACCES.
#[no_mangle]
pub extern "C" fn __syscall_mkdirat(_dirfd: i32, _path: i32, _mode: i32) -> i32 {
    EACCES
}

/// Browser: file-system advice. Canister: 0 (advisory, no-op is correct).
#[no_mangle]
pub extern "C" fn __syscall_fadvise64(_fd: i32, _offset: i64, _len: i64, _advice: i32) -> i32 {
    0
}

/// Browser: read directory entries. Canister: -EBADF.
#[no_mangle]
pub extern "C" fn __syscall_getdents64(_fd: i32, _buf: i32, _count: i32) -> i32 {
    EBADF
}

/// Browser: read symlink. Canister: -ENOENT.
#[no_mangle]
pub extern "C" fn __syscall_readlinkat(_dirfd: i32, _path: i32, _buf: i32, _bufsize: i32) -> i32 {
    ENOENT
}

/// Browser: rename file. Canister: -ENOSYS.
#[no_mangle]
pub extern "C" fn __syscall_renameat(_olddirfd: i32, _oldpath: i32, _newdirfd: i32, _newpath: i32) -> i32 {
    ENOSYS
}

/// Browser: remove directory. Canister: -ENOSYS.
#[no_mangle]
pub extern "C" fn __syscall_rmdir(_path: i32) -> i32 {
    ENOSYS
}

/// Browser: stat the filesystem behind an fd. Canister: -EBADF.
#[no_mangle]
pub extern "C" fn __syscall_fstatfs64(_fd: i32, _size: i32, _buf: i32) -> i32 {
    EBADF
}

/// Browser: create a symlink. Canister: -ENOSYS.
#[no_mangle]
pub extern "C" fn __syscall_symlink(_target: i32, _linkpath: i32) -> i32 {
    ENOSYS
}

/// Browser: unlink (delete) a path. Canister: -ENOSYS.
#[no_mangle]
pub extern "C" fn __syscall_unlinkat(_dirfd: i32, _path: i32, _flags: i32) -> i32 {
    ENOSYS
}

/// Browser: update access/modification timestamps. Canister: -ENOSYS.
#[no_mangle]
pub extern "C" fn __syscall_utimensat(_dirfd: i32, _path: i32, _times: i32, _flags: i32) -> i32 {
    ENOSYS
}

/// Browser: socket connect. Canister: -ENOSYS (no networking via syscall).
#[no_mangle]
pub extern "C" fn __syscall_connect(_a: i32, _b: i32, _c: i32, _d: i32, _e: i32, _f: i32) -> i32 {
    ENOSYS
}

/// Browser: socket recvfrom. Canister: -ENOSYS.
#[no_mangle]
pub extern "C" fn __syscall_recvfrom(_a: i32, _b: i32, _c: i32, _d: i32, _e: i32, _f: i32) -> i32 {
    ENOSYS
}

/// Browser: socket sendto. Canister: -ENOSYS.
#[no_mangle]
pub extern "C" fn __syscall_sendto(_a: i32, _b: i32, _c: i32, _d: i32, _e: i32, _f: i32) -> i32 {
    ENOSYS
}

/// Browser: create a socket. Canister: -ENOSYS.
#[no_mangle]
pub extern "C" fn __syscall_socket(_a: i32, _b: i32, _c: i32, _d: i32, _e: i32, _f: i32) -> i32 {
    ENOSYS
}

// ===========================================================================
// 2. C++ exception-handling ABI (7 functions)
// ---------------------------------------------------------------------------
// Microsoft's wasm build links libc++abi for native C++ EH used by parts of
// Mono itself. Emscripten's exception ABI funnels through these symbols.
//
// TODO(v0.2): managed exceptions are unsupported in v0.1 — code that
// throws will trap the canister. To support them we need either real
// libunwind-style stack traversal or `wasm-eh` proposal support, neither
// available on the IC today.
// ===========================================================================

/// Browser: throw a C++ exception. Canister: trap.
#[no_mangle]
pub extern "C" fn __cxa_throw(_thrown: i32, _tinfo: i32, _dest: i32) {
    ic_trap("wasp: __cxa_throw — managed/native exceptions unsupported in v0.1");
}

/// Browser: invoke a function pointer through Emscripten's SJLJ trampoline
/// for a `(i32) -> ()` callee, catching any exception. Canister: trap on
/// invocation — we have no SJLJ runtime.
#[no_mangle]
pub extern "C" fn invoke_vi(_index: i32, _arg: i32) {
    ic_trap("wasp: invoke_vi — Emscripten SJLJ trampoline unsupported");
}

/// Browser: find catch handler for an in-flight exception. Canister: trap.
#[no_mangle]
pub extern "C" fn __cxa_find_matching_catch_3(_tinfo: i32) -> i32 {
    ic_trap("wasp: __cxa_find_matching_catch_3 — EH unsupported");
}

/// Browser: map a typeinfo pointer to LLVM's catch-clause id. Canister:
/// returning 0 is the "no match" sentinel; safe enough for code that
/// never actually throws.
#[no_mangle]
pub extern "C" fn llvm_eh_typeid_for(_tinfo: i32) -> i32 {
    0
}

/// Browser: enter a catch block. Canister: trap.
#[no_mangle]
pub extern "C" fn __cxa_begin_catch(_exc: i32) -> i32 {
    ic_trap("wasp: __cxa_begin_catch — EH unsupported");
}

/// Browser: leave a catch block. Canister: trap.
#[no_mangle]
pub extern "C" fn __cxa_end_catch() {
    ic_trap("wasp: __cxa_end_catch — EH unsupported");
}

/// Browser: re-raise the current exception. Canister: trap.
#[no_mangle]
pub extern "C" fn __resumeException(_exc: i32) {
    ic_trap("wasp: __resumeException — EH unsupported");
}

// ===========================================================================
// 3. Time / date (8 functions)
// ---------------------------------------------------------------------------
// Mono asks "what time is it?" constantly — DateTime.UtcNow, GC tuning,
// DateTime.Now in user code. We route everything to `ic0.time()` (which
// returns nanoseconds since the UNIX epoch as u64) and convert as needed.
// `_emscripten_get_now_is_monotonic` returns 1 because IC time is
// monotonic non-decreasing within an execution.
// ===========================================================================

/// Browser: `Date.now()` in ms. Canister: ic0.time() ns / 1e6.
#[no_mangle]
pub extern "C" fn emscripten_date_now() -> f64 {
    let ns = unsafe { time() };
    (ns as f64) / 1_000_000.0
}

/// Browser: 1 if `performance.now()` is monotonic. Canister: 1.
#[no_mangle]
pub extern "C" fn _emscripten_get_now_is_monotonic() -> i32 {
    1
}

/// Browser: `performance.now()` (ms, fractional). Canister: same as
/// `emscripten_date_now`; IC time is the only clock we have.
#[no_mangle]
pub extern "C" fn emscripten_get_now() -> f64 {
    let ns = unsafe { time() };
    (ns as f64) / 1_000_000.0
}

/// Browser: clock resolution in ms. Canister: 1 ms.
#[no_mangle]
pub extern "C" fn emscripten_get_now_res() -> f64 {
    1.0
}

/// Browser: split UTC time into struct tm. Canister: no-op (TODO: parse
/// the i64 epoch seconds and write a tm struct at `tmptr`). Most managed
/// callers go through `_localtime_js` or compute fields themselves; this
/// path is hit only by a few BCL paths.
#[no_mangle]
pub extern "C" fn _gmtime_js(_time_low: i64, _tmptr: i32) {}

/// Browser: split local time into struct tm. Canister: no-op (we have no
/// local timezone — UTC is the only meaningful clock for a canister).
#[no_mangle]
pub extern "C" fn _localtime_js(_time_low: i64, _tmptr: i32) {}

/// Browser: refresh tzname / timezone / daylight. Canister: no-op
/// (everything stays at UTC defaults).
#[no_mangle]
pub extern "C" fn _tzset_js(_timezone: i32, _daylight: i32, _std_name: i32, _dst_name: i32) {}

/// Browser: format a time per `strftime(3)`. Canister: write 0 (signals
/// "not enough room") — managed code that needs strftime should use
/// .NET's pure-managed `DateTime.ToString` formatters anyway.
#[no_mangle]
pub extern "C" fn strftime(_s: i32, _maxsize: i32, _format: i32, _tm: i32) -> i32 {
    0
}

// ===========================================================================
// 4. mmap / msync (3 functions)
// ---------------------------------------------------------------------------
// Real mmap is rare in pure-managed code — the BCL prefers File.ReadAllBytes.
// We trap on any actual call. Future work: back with a malloc/free pair if
// it turns out Mono's GC config calls mmap during init.
// ===========================================================================

/// Browser: anonymous/file-backed mmap into the wasm linear memory.
/// Canister: trap (caller will rarely hit this path).
#[no_mangle]
pub extern "C" fn _mmap_js(
    _len: i32,
    _prot: i32,
    _flags: i32,
    _fd: i32,
    _offset: i64,
    _allocated: i32,
    _addr: i32,
) -> i32 {
    ic_trap("wasp: _mmap_js — mmap unsupported in v0.1");
}

/// Browser: undo a previous mmap. Canister: 0 (success — there was
/// nothing to unmap because we trapped on mmap).
#[no_mangle]
pub extern "C" fn _munmap_js(
    _addr: i32,
    _len: i32,
    _prot: i32,
    _flags: i32,
    _fd: i32,
    _offset: i64,
) -> i32 {
    0
}

/// Browser: flush an mmap region. Canister: 0.
#[no_mangle]
pub extern "C" fn _msync_js(
    _addr: i32,
    _len: i32,
    _prot: i32,
    _flags: i32,
    _fd: i32,
    _offset: i64,
) -> i32 {
    0
}

// ===========================================================================
// 5. Heap (2 functions)
// ---------------------------------------------------------------------------
// Mono drives wasm `memory.grow` indirectly through Emscripten's heap-resize
// callback. We invoke `core::arch::wasm32::memory_grow` directly. The
// "max heap" report is used by GC tuning; we report 4 GiB (wasm32 max)
// even though the real IC limit is lower — Mono just uses this to pick GC
// thresholds and a too-large value is harmless (it'll trigger growth more
// aggressively).
// ===========================================================================

const WASM_PAGE_SIZE: i32 = 65_536;
const MAX_HEAP_PAGES_REPORTED: i32 = 65_536; // 4 GiB / 64 KiB = 65536 pages

/// Browser: ask the JS host to grow the wasm linear memory to at least
/// `requested_size` bytes. Canister: round up to the nearest 64 KiB page,
/// call `memory.grow`, return 1 on success / 0 on failure (Emscripten
/// convention).
#[no_mangle]
pub extern "C" fn emscripten_resize_heap(requested_size: i32) -> i32 {
    ic_debug_print_bytes(b"[emscripten_resize_heap] called");
    let req = requested_size as u32;
    let page = WASM_PAGE_SIZE as u32;
    // current size in bytes
    let current_pages = core::arch::wasm32::memory_size(0) as u32;
    let current_bytes = current_pages * page;
    if req <= current_bytes {
        return 1;
    }
    let needed_bytes = req - current_bytes;
    let needed_pages = needed_bytes.div_ceil(page) as usize;
    let prev = core::arch::wasm32::memory_grow(0, needed_pages);
    if prev == usize::MAX { 0 } else { 1 }
}

/// Browser: report MAXIMUM_MEMORY (configured at link time). Canister:
/// claim 4 GiB so Mono's GC config uses generous thresholds.
#[no_mangle]
pub extern "C" fn emscripten_get_heap_max() -> i32 {
    // 4 GiB exceeds i32::MAX so saturate at i32::MAX (≈ 2 GiB). The Mono
    // runtime treats this as "headroom is plentiful" — actual growth is
    // gated by ic0 / wasm32 limits separately.
    i32::MAX
}

// ===========================================================================
// 6. Process abort / exit (3 functions)
// ---------------------------------------------------------------------------
// All three terminate the process in a browser. On the IC, the equivalent
// is `ic0.trap` — it rolls back the current message and surfaces the
// reason in the candid reply.
// ===========================================================================

/// Browser: assertion failure or unrecoverable runtime error. Canister: trap.
#[no_mangle]
pub extern "C" fn abort() -> ! {
    ic_trap("wasp: dotnet.native.wasm called abort()")
}

/// Browser: `process.exit(code)`. Canister: trap with the exit code.
#[no_mangle]
pub extern "C" fn exit(code: i32) -> ! {
    // Phase B debugging: include the exit code in the trap message so
    // `dfx canister logs` tells us which Mono failure path we hit.
    let mut buf = [0u8; 64];
    let n = format_exit_message(&mut buf, code);
    unsafe { trap(buf.as_ptr() as u32, n as u32) }
}

#[inline(never)]
fn format_exit_message(buf: &mut [u8], code: i32) -> usize {
    // Manual int→ASCII to avoid pulling format machinery (which may not
    // work in early canister_init context).
    let prefix = b"wasp: dotnet.native.wasm called exit(";
    let mut i = 0;
    for &b in prefix {
        buf[i] = b;
        i += 1;
    }
    // Write the integer (handle negative).
    let mut n = code as i64;
    if n < 0 {
        buf[i] = b'-';
        i += 1;
        n = -n;
    }
    let start = i;
    if n == 0 {
        buf[i] = b'0';
        i += 1;
    } else {
        let mut tmp = [0u8; 20];
        let mut t = 0;
        while n > 0 {
            tmp[t] = b'0' + (n % 10) as u8;
            t += 1;
            n /= 10;
        }
        while t > 0 {
            t -= 1;
            buf[i] = tmp[t];
            i += 1;
        }
    }
    let _ = start;
    buf[i] = b')';
    i += 1;
    i
}

/// Browser: same as `exit` but skip atexit handlers. Canister: trap.
#[no_mangle]
pub extern "C" fn emscripten_force_exit(_code: i32) {
    ic_trap("wasp: dotnet.native.wasm called emscripten_force_exit()");
}

// ===========================================================================
// 7. Mono interpreter / jiterpreter hooks (7 functions)
// ---------------------------------------------------------------------------
// "Jiterpreter" is Mono's wasm-targeting tier-up JIT. It synthesises new
// wasm functions at runtime by handing bytes back to the JS host, which
// then calls `WebAssembly.compile`. None of that works in a canister.
// We disable it by reporting "not ready" / "not supported" from each hook
// and Mono falls back to its pure interpreter (slower but correct).
// ===========================================================================

/// Browser: enqueue a generated trampoline for compilation. Canister: 0
/// (Mono treats 0 as "JIT unavailable, use interpreter").
#[no_mangle]
pub extern "C" fn mono_interp_tier_prepare_jiterpreter(
    _frame: i32,
    _method: i32,
    _ip: i32,
    _trampoline_id: i32,
    _data1: i32,
    _data2: i32,
    _data3: i32,
    _data4: i32,
) -> i32 {
    0
}

/// Browser: enter a JIT-compiled wasm "entry trampoline". Canister: 0
/// (JIT compilation never happens, so we never get called for real).
#[no_mangle]
pub extern "C" fn mono_interp_jit_wasm_entry_trampoline(
    _a: i32, _b: i32, _c: i32, _d: i32,
    _e: i32, _f: i32, _g: i32, _h: i32,
) -> i32 {
    0
}

/// Browser: call into a JIT-compiled jit-call trampoline. Canister: no-op.
#[no_mangle]
pub extern "C" fn mono_interp_invoke_wasm_jit_call_trampoline(
    _a: i32, _b: i32, _c: i32, _d: i32, _e: i32,
) {
}

/// Browser: JIT-compile a jit-call trampoline. Canister: no-op.
#[no_mangle]
pub extern "C" fn mono_interp_jit_wasm_jit_call_trampoline(
    _a: i32, _b: i32, _c: i32, _d: i32, _e: i32,
) {
}

/// Browser: drain the queue of pending jiterpreter compilations.
/// Canister: no-op (queue is always empty).
#[no_mangle]
pub extern "C" fn mono_interp_flush_jitcall_queue() {}

/// Browser: log an interpreter-method entry for the jiterpreter's
/// hot-path detector. Canister: no-op.
#[no_mangle]
pub extern "C" fn mono_interp_record_interp_entry(_method_handle: i32) {}

/// Browser: schedule a microtask for Mono's background sweep. Canister:
/// no-op (we run synchronously; a future phase can wire this to a
/// canister timer).
#[no_mangle]
pub extern "C" fn schedule_background_exec() {}

// ===========================================================================
// 8. JS interop / debugger / locale (15 functions)
// ---------------------------------------------------------------------------
// These are how managed code reaches "out" of the wasm sandbox in a
// browser: invoke a JS function, marshal a Promise, log to the JS console,
// fire a debugger event. Inside a canister there is no JS to call — most
// either no-op (logging falls into `ic0.debug_print`) or trap (anything
// that genuinely needs JS interop is unsupported in v0.1).
//
// The two exceptions are:
//   * `mono_wasm_browser_entropy` — fill a buffer with secure random
//     bytes from `ic0.raw_rand` (TODO: raw_rand is async; for now we
//     fill with a deterministic counter and log a warning).
//   * `mono_wasm_trace_logger` / `mono_wasm_debugger_log` — route to
//     `ic0.debug_print` so canister logs surface managed traces.
// ===========================================================================

/// Browser: bind a JS import for the single-threaded ABI. Canister: trap
/// — JSImport-decorated managed code is unsupported.
#[no_mangle]
pub extern "C" fn mono_wasm_bind_js_import_ST(_arg: i32) -> i32 {
    ic_trap("wasp: mono_wasm_bind_js_import_ST — JSImport unsupported");
}

/// Browser: invoke a previously-bound JS import. Canister: trap.
#[no_mangle]
pub extern "C" fn mono_wasm_invoke_jsimport_ST(_function_handle: i32, _args_buffer: i32) {
    ic_trap("wasp: mono_wasm_invoke_jsimport_ST — JSImport unsupported");
}

/// Browser: drop the GC root for a CS-owned JS object. Canister: no-op
/// (we never created any such root).
#[no_mangle]
pub extern "C" fn mono_wasm_release_cs_owned_object(_handle: i32) {}

/// Browser: complete a managed Task tied to a JS Promise. Canister: no-op.
#[no_mangle]
pub extern "C" fn mono_wasm_resolve_or_reject_promise(_args_buffer: i32) {}

/// Browser: invoke an arbitrary JS function. Canister: trap.
#[no_mangle]
pub extern "C" fn mono_wasm_invoke_js_function(_function_handle: i32, _args_buffer: i32) {
    ic_trap("wasp: mono_wasm_invoke_js_function — JSImport unsupported");
}

/// Browser: cancel a managed Task tied to a JS Promise. Canister: no-op.
#[no_mangle]
pub extern "C" fn mono_wasm_cancel_promise(_task_holder_gc_handle: i32) {}

/// Browser: clear the JS console. Canister: no-op.
#[no_mangle]
pub extern "C" fn mono_wasm_console_clear() {}

/// Browser: query ICU locale data. Canister: -1 (Mono falls back to
/// invariant culture, which is what we want anyway).
#[no_mangle]
pub extern "C" fn mono_wasm_get_locale_info(
    _culture: i32,
    _culture_length: i32,
    _locale: i32,
    _locale_length: i32,
    _is_utf16: i32,
    _buffer: i32,
    _buffer_length: i32,
) -> i32 {
    -1
}

/// Browser: install a debugger breakpoint at the entry point. Canister: no-op.
#[no_mangle]
pub extern "C" fn mono_wasm_set_entrypoint_breakpoint(_method_token: i32) {}

/// Browser: emit a runtime trace line to the JS console. Canister:
/// forward the buffer to `ic0.debug_print`.
#[no_mangle]
pub extern "C" fn mono_wasm_trace_logger(
    log_domain: i32,
    log_level: i32,
    message: i32,
    _fatal: i32,
    _user_data: i32,
) {
    let _ = log_level; // unused
    // Mono ABI: `void wasm_trace_logger(char *log_domain, char *log_level,
    //                                    char *message, mono_bool fatal,
    //                                    void *user_data)` (5 args).
    // `message` is a NUL-terminated C string.
    //
    // Note: empirically `fatal` is observed as 4 (not 0/1) at runtime
    // — the argument may be a packed level enum; treat as opaque.
    unsafe {
        let mut buf = [0u8; 4200];
        let prefix = b"[mono] ";
        let mut i = 0;
        for &b in prefix { buf[i] = b; i += 1; }
        // Dump raw arg values so we can see what mono actually passes.
        // Many mono builds pass log args by raw mem_base-offset; some
        // by literal absolute pointer. Show both interpretations.
        let mb = crate::wasp_get_mem_base();
        // Print domain string (first arg) — usually a stable interned
        // identifier like "Mono", "GLib", "DOTNET" — gives subsystem.
        for &c in b"dom=" { if i < buf.len() { buf[i]=c; i+=1; } }
        if log_domain != 0 {
            let p = mb.wrapping_add(log_domain as u32) as *const u8;
            let mut len = 0;
            while len < 64 { let b = *p.add(len); if b == 0 { break; } len += 1; }
            for k in 0..len {
                if i >= buf.len() { break; }
                buf[i] = *p.add(k); i += 1;
            }
        }
        let mut tmp = [0u8; 64];
        let mut ti = 0;
        for &c in b" msg=" { tmp[ti] = c; ti += 1; }
        ti = crate::format_decimal(&mut tmp, ti, message as u64);
        for &c in b" mb=" { tmp[ti] = c; ti += 1; }
        ti = crate::format_decimal(&mut tmp, ti, mb as u64);
        for &c in b" | " { tmp[ti] = c; ti += 1; }
        for &b in &tmp[..ti] {
            if i >= buf.len() { break; }
            buf[i] = b; i += 1;
        }
        // Dump first 32 bytes at BOTH candidate addresses as hex so
        // we can SEE what's actually in memory.
        for (label, addr) in [
            (b"raw[" as &[u8], message as u32),
            (b"mb+[", mb.wrapping_add(message as u32)),
        ] {
            for &c in label { if i < buf.len() { buf[i]=c; i+=1; } }
            for &c in b"]: " { if i < buf.len() { buf[i]=c; i+=1; } }
            if addr == 0 {
                for &c in b"NULL" { if i < buf.len() { buf[i]=c; i+=1; } }
            } else {
                let p = addr as *const u8;
                for k in 0..32 {
                    let b = *p.add(k);
                    // hex byte + space
                    let hi = (b >> 4) & 0xF;
                    let lo = b & 0xF;
                    if i + 3 > buf.len() { break; }
                    buf[i] = if hi < 10 { b'0' + hi } else { b'a' + hi - 10 };
                    buf[i+1] = if lo < 10 { b'0' + lo } else { b'a' + lo - 10 };
                    buf[i+2] = b' ';
                    i += 3;
                }
            }
            for &c in b" | " { if i < buf.len() { buf[i]=c; i+=1; } }
        }
        ic_debug_print_bytes(&buf[..i]);
    }
}

/// Browser: free a Mono-owned method-data block. Canister: no-op (Rust
/// does not own the block; Mono allocated it inside its own heap).
#[no_mangle]
pub extern "C" fn mono_wasm_free_method_data(_method: i32, _data: i32, _data_length: i32) {}

/// Browser: getpid(). Canister: 1 (any non-zero stable value is fine —
/// Mono uses this for tempfile names which we don't honour anyway).
#[no_mangle]
pub extern "C" fn mono_wasm_process_current_pid() -> i32 {
    1
}

/// Browser: write a structured debugger-log payload. Canister: forward
/// the buffer to `ic0.debug_print`.
#[no_mangle]
pub extern "C" fn mono_wasm_debugger_log(_level: i32, message_handle: i32) {
    // The wire format is a UTF-16 string handle in browser; we can't
    // decode without more context, so just record that an event happened.
    let _ = message_handle;
    ic_debug_print("wasp: mono_wasm_debugger_log");
}

/// Browser: notify the debugger that an assembly was loaded. Canister: no-op.
#[no_mangle]
pub extern "C" fn mono_wasm_asm_loaded(
    _assembly_name: i32,
    _assembly_ptr: i32,
    _assembly_len: i32,
    _pdb_ptr: i32,
    _pdb_len: i32,
) {}

/// Browser: deliver the response to a previously-issued debugger command.
/// Canister: no-op (no debugger connected).
#[no_mangle]
pub extern "C" fn mono_wasm_add_dbg_command_received(
    _id: i32, _command_set: i32, _command: i32, _data: i32,
) {}

/// Browser: dispatch a debugger-agent message with associated data.
/// Canister: no-op.
#[no_mangle]
pub extern "C" fn mono_wasm_fire_debugger_agent_message_with_data(_message: i32, _length: i32) {}

/// Browser: set a Mono timer that fires at `due_time_ms`. Canister:
/// no-op for now — timers will be wired to the IC global timer in a
/// later issue.
#[no_mangle]
pub extern "C" fn mono_wasm_schedule_timer(_due_time_ms: i32) {}

// --- entropy ---------------------------------------------------------------

/// Counter used by `mono_wasm_browser_entropy` to produce non-repeating
/// (but **not** cryptographically secure) bytes when `ic0.raw_rand`
/// cannot be used (it's a `call` in async query/update context only).
/// A future issue replaces this with a real seeded CSPRNG fed from
/// `raw_rand` at canister init.
static ENTROPY_COUNTER: AtomicU64 = AtomicU64::new(0xA5A5_5A5A_DEAD_BEEF);

/// Browser: fill `buffer[..length]` with `crypto.getRandomValues`.
/// Canister: fill with bytes from a stateful u64 counter mixed with
/// `ic0.time()`. **NOT cryptographically secure** — see TODO above.
#[no_mangle]
pub extern "C" fn mono_wasm_browser_entropy(buffer: i32, length: i32) -> i32 {
    if buffer == 0 || length <= 0 {
        return -1;
    }
    let now = unsafe { time() };
    let mut state = ENTROPY_COUNTER.fetch_add(1, Ordering::Relaxed) ^ now;
    let dst = buffer as *mut u8;
    let len = length as usize;
    for i in 0..len {
        // SplitMix64 step
        state = state.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = state;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^= z >> 31;
        unsafe {
            dst.add(i).write(z as u8);
        }
    }
    0
}
