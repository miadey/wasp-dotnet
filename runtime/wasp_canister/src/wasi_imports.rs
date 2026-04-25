// wasi_imports.rs — implementations of the 10 WASI snapshot_preview1
// functions that `dotnet.native.wasm` imports. After `wasm-merge`,
// these `#[no_mangle] extern "C"` exports of the Rust canister satisfy
// the corresponding `(import "wasi_snapshot_preview1" "...")` in the
// .NET runtime module.
//
// Signatures derived from `wasm-tools print dotnet.native.wasm`:
//
//   environ_sizes_get : (i32 i32) -> i32
//   environ_get       : (i32 i32) -> i32
//   fd_close          : (i32) -> i32
//   fd_write          : (i32 i32 i32 i32) -> i32
//   fd_read           : (i32 i32 i32 i32) -> i32
//   fd_sync           : (i32) -> i32
//   fd_seek           : (i32 i64 i32 i32) -> i32
//   fd_pread          : (i32 i32 i32 i64 i32) -> i32
//   fd_pwrite         : (i32 i32 i32 i64 i32) -> i32
//   fd_fdstat_get     : (i32 i32) -> i32
//
// WASI errno values we use (see wasi_snapshot_preview1 spec):
//   ESUCCESS = 0
//   EBADF    = 8
//   ENOSYS   = 52
//   ESPIPE   = 70
//
// Phase A v0.1 policy: route fd_write to fd 1/2 → ic0::debug_print so
// `Console.WriteLine` from managed code reaches `dfx canister logs`.
// All other ops return EBADF / ENOSYS / zero counts. A real VFS will
// land in Phase B alongside chunked assembly upload.

#![allow(clippy::missing_safety_doc)]

const ESUCCESS: i32 = 0;
const EBADF: i32 = 8;
const ENOSYS: i32 = 52;

const STDOUT: i32 = 1;
const STDERR: i32 = 2;

// ---------------------------------------------------------------------------
// environ_*
// ---------------------------------------------------------------------------

/// Tell the caller there are zero environment variables and they take
/// zero bytes. Mono calls this during runtime init and accepts an empty
/// environ.
#[no_mangle]
pub unsafe extern "C" fn environ_sizes_get(envc_out: i32, env_buf_size_out: i32) -> i32 {
    write_i32(envc_out, 0);
    write_i32(env_buf_size_out, 0);
    ESUCCESS
}

/// Nothing to copy because `environ_sizes_get` reported zero envs.
#[no_mangle]
pub unsafe extern "C" fn environ_get(_environ_out: i32, _environ_buf_out: i32) -> i32 {
    ESUCCESS
}

// ---------------------------------------------------------------------------
// fd_write / fd_read / fd_pwrite / fd_pread
// ---------------------------------------------------------------------------

/// `fd_write(fd, iovs, iovs_len, nwritten_out) -> errno`
///
/// `iovs` is a pointer to a sequence of `(buf_ptr: i32, buf_len: i32)`
/// records (each record is 8 bytes).
///
/// For fd=1 (stdout) and fd=2 (stderr) we concatenate the iov contents
/// into a single utf-8 string and emit it via `ic0::debug_print`. Any
/// other fd returns `EBADF` until the in-memory VFS lands.
#[no_mangle]
pub unsafe extern "C" fn fd_write(
    fd: i32,
    iovs_ptr: i32,
    iovs_len: i32,
    nwritten_out: i32,
) -> i32 {
    if fd != STDOUT && fd != STDERR {
        // TODO(phase-B): route to in-memory VFS once it exists.
        write_i32(nwritten_out, 0);
        return EBADF;
    }

    let mut total: i32 = 0;
    let mut buf: Vec<u8> = Vec::new();
    for i in 0..iovs_len {
        let iov_addr = (iovs_ptr as usize) + (i as usize) * 8;
        let p = read_i32(iov_addr as i32) as usize;
        let n = read_i32((iov_addr + 4) as i32) as usize;
        if n == 0 {
            continue;
        }
        let slice = core::slice::from_raw_parts(p as *const u8, n);
        buf.extend_from_slice(slice);
        total = total.saturating_add(n as i32);
    }

    if !buf.is_empty() {
        // Strip a single trailing newline so debug_print lines stay tidy.
        if buf.last() == Some(&b'\n') {
            buf.pop();
        }
        let s = String::from_utf8_lossy(&buf);
        ic_cdk::println!("{s}");
    }

    write_i32(nwritten_out, total);
    ESUCCESS
}

/// `fd_read` — no readable fds yet.
#[no_mangle]
pub unsafe extern "C" fn fd_read(
    _fd: i32,
    _iovs_ptr: i32,
    _iovs_len: i32,
    nread_out: i32,
) -> i32 {
    write_i32(nread_out, 0);
    EBADF
}

/// `fd_pwrite(fd, iovs, iovs_len, offset: i64, nwritten_out) -> errno`
#[no_mangle]
pub unsafe extern "C" fn fd_pwrite(
    _fd: i32,
    _iovs_ptr: i32,
    _iovs_len: i32,
    _offset: i64,
    nwritten_out: i32,
) -> i32 {
    write_i32(nwritten_out, 0);
    EBADF
}

/// `fd_pread(fd, iovs, iovs_len, offset: i64, nread_out) -> errno`
#[no_mangle]
pub unsafe extern "C" fn fd_pread(
    _fd: i32,
    _iovs_ptr: i32,
    _iovs_len: i32,
    _offset: i64,
    nread_out: i32,
) -> i32 {
    write_i32(nread_out, 0);
    EBADF
}

// ---------------------------------------------------------------------------
// fd_close / fd_sync / fd_seek / fd_fdstat_get
// ---------------------------------------------------------------------------

#[no_mangle]
pub unsafe extern "C" fn fd_close(_fd: i32) -> i32 {
    // Closing stdout/stderr is a no-op; closing anything else is an
    // unsupported fd because we don't track them yet.
    ESUCCESS
}

#[no_mangle]
pub unsafe extern "C" fn fd_sync(_fd: i32) -> i32 {
    ESUCCESS
}

/// `fd_seek(fd, offset: i64, whence: i32, newoffset_out: i32) -> errno`
#[no_mangle]
pub unsafe extern "C" fn fd_seek(
    _fd: i32,
    _offset: i64,
    _whence: i32,
    newoffset_out: i32,
) -> i32 {
    // Write 0 into the result slot so callers that ignore the errno see
    // a deterministic position rather than uninitialised memory.
    write_i64(newoffset_out, 0);
    EBADF
}

/// `fd_fdstat_get(fd, stat_out: i32) -> errno`
///
/// The fdstat record is 24 bytes: { fs_filetype:u8, _pad:u8,
/// fs_flags:u16, _pad:u32, fs_rights_base:u64, fs_rights_inheriting:u64 }.
/// We zero the whole struct and return ENOSYS so Mono treats the fd as
/// closed/unknown without trapping.
#[no_mangle]
pub unsafe extern "C" fn fd_fdstat_get(_fd: i32, stat_out: i32) -> i32 {
    let p = stat_out as usize as *mut u8;
    core::ptr::write_bytes(p, 0u8, 24);
    ENOSYS
}

// ---------------------------------------------------------------------------
// helpers
// ---------------------------------------------------------------------------

#[inline]
unsafe fn read_i32(addr: i32) -> i32 {
    core::ptr::read_unaligned(addr as usize as *const i32)
}

#[inline]
unsafe fn write_i32(addr: i32, value: i32) {
    core::ptr::write_unaligned(addr as usize as *mut i32, value);
}

#[inline]
unsafe fn write_i64(addr: i32, value: i64) {
    core::ptr::write_unaligned(addr as usize as *mut i64, value);
}
