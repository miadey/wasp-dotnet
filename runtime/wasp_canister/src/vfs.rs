//! vfs.rs — minimal in-memory file system Mono can read assemblies from.
//!
//! Issue #36 of github.com/miadey/wasp-dotnet. Mono's bootstrap calls
//! `__syscall_openat` + `fd_read` + `fd_seek` + `fd_close` + `fd_fdstat_get`
//! to load `System.Private.CoreLib.dll` and the user's assembly. With our
//! Phase A wasi stubs returning EBADF for everything, Mono never gets
//! past `mono_wasm_load_runtime`.
//!
//! This module:
//!   - embeds corelib + WaspHost.dll into the canister wasm via
//!     `include_bytes!` (~1.66 MB; canister grows from ~3.85 → ~5.5 MB,
//!     well under the 100 MiB ICP module limit)
//!   - exposes an in-memory file map keyed by path
//!   - manages a small fd table (max 32 open files) so `fd_read` /
//!     `fd_seek` can track position per fd
//!
//! Constraints:
//!   - No `format!` / `println!` here — those introduce indirect-call
//!     sites the wasm-table-merge pass can't lower
//!   - No heap allocation in the hot path; we use a static `[FdState; 32]`
//!     guarded by a mutex
//!   - All operations are best-effort: unknown paths → -ENOENT,
//!     bad fds → -EBADF, anything else → -ENOSYS

#![allow(dead_code)]

use core::cell::UnsafeCell;
use core::sync::atomic::{AtomicUsize, Ordering};

// ---------------------------------------------------------------------------
// Embedded files
// ---------------------------------------------------------------------------

const CORELIB: &[u8] = include_bytes!("../../inputs/System.Private.CoreLib.dll");
const WASPHOST: &[u8] = include_bytes!("../../inputs/WaspHost.dll");

/// Each entry is `(path, bytes)`. Mono looks for assemblies in MONO_PATH
/// (which we set to `/`) and in MONO_ROOT/shared/Microsoft.NETCore.App/...
/// We mount the same bytes under multiple plausible paths to maximise
/// the chance Mono finds them on its first try.
const FILES: &[(&[u8], &[u8])] = &[
    (b"/WaspHost.dll", WASPHOST),
    (b"WaspHost.dll", WASPHOST),
    (b"WaspHost", WASPHOST),
    (b"/System.Private.CoreLib.dll", CORELIB),
    (b"System.Private.CoreLib.dll", CORELIB),
    (b"/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/System.Private.CoreLib.dll", CORELIB),
    (b"./System.Private.CoreLib.dll", CORELIB),
    (b"./WaspHost.dll", WASPHOST),
];

/// Look up a NUL-terminated UTF-8 path. Returns `Some(&'static [u8])`
/// for known files, `None` otherwise.
pub fn lookup(path_ptr: *const u8) -> Option<&'static [u8]> {
    if path_ptr.is_null() {
        return None;
    }
    let path = nul_terminated(path_ptr)?;
    for (k, v) in FILES {
        if *k == path {
            return Some(v);
        }
    }
    None
}

unsafe fn nul_terminated_unchecked<'a>(p: *const u8) -> &'a [u8] {
    let mut len = 0usize;
    while *p.add(len) != 0 && len < 4096 {
        len += 1;
    }
    core::slice::from_raw_parts(p, len)
}

fn nul_terminated<'a>(p: *const u8) -> Option<&'a [u8]> {
    if p.is_null() {
        return None;
    }
    Some(unsafe { nul_terminated_unchecked(p) })
}

// ---------------------------------------------------------------------------
// fd table
// ---------------------------------------------------------------------------

#[derive(Clone, Copy, Default)]
pub struct FdState {
    /// Index into FILES, or usize::MAX for a free slot.
    pub file_idx: usize,
    /// Current read offset within the file's bytes.
    pub pos: u64,
}

const MAX_FDS: usize = 32;
const FD_BASE: i32 = 100; // wasi reserves 0/1/2 for stdio; allocate above

struct FdTable {
    inner: UnsafeCell<[FdState; MAX_FDS]>,
}
unsafe impl Sync for FdTable {}

static FD_TABLE: FdTable = FdTable {
    inner: UnsafeCell::new([FdState { file_idx: usize::MAX, pos: 0 }; MAX_FDS]),
};

static OPEN_COUNT: AtomicUsize = AtomicUsize::new(0);

fn table_mut() -> &'static mut [FdState; MAX_FDS] {
    // Single-threaded canister model: no real concurrency, so the cell
    // is safe to dereference. A future multithreading patch would need
    // a mutex.
    unsafe { &mut *FD_TABLE.inner.get() }
}

/// Open a file by path; returns the new wasi fd or -1 on failure.
pub fn open_path(path_ptr: *const u8) -> i32 {
    let path = match nul_terminated(path_ptr) {
        Some(p) => p,
        None => return -1,
    };
    // Find the file index.
    let mut found: Option<usize> = None;
    for (i, (k, _)) in FILES.iter().enumerate() {
        if *k == path {
            found = Some(i);
            break;
        }
    }
    let file_idx = match found {
        Some(i) => i,
        None => return -1,
    };
    // Allocate a free fd slot.
    let table = table_mut();
    for (slot_idx, slot) in table.iter_mut().enumerate() {
        if slot.file_idx == usize::MAX {
            slot.file_idx = file_idx;
            slot.pos = 0;
            OPEN_COUNT.fetch_add(1, Ordering::Relaxed);
            return FD_BASE + slot_idx as i32;
        }
    }
    -1 // table full
}

pub fn close(fd: i32) -> i32 {
    let slot = fd_to_slot(fd);
    if slot.is_none() {
        return -1;
    }
    let i = slot.unwrap();
    let table = table_mut();
    if table[i].file_idx == usize::MAX {
        return -1;
    }
    table[i].file_idx = usize::MAX;
    table[i].pos = 0;
    OPEN_COUNT.fetch_sub(1, Ordering::Relaxed);
    0
}

pub fn fd_to_slot(fd: i32) -> Option<usize> {
    if fd < FD_BASE {
        return None;
    }
    let idx = (fd - FD_BASE) as usize;
    if idx >= MAX_FDS {
        return None;
    }
    Some(idx)
}

pub fn read(fd: i32, dst: *mut u8, len: usize) -> i32 {
    let slot = match fd_to_slot(fd) {
        Some(i) => i,
        None => return -1,
    };
    let table = table_mut();
    let st = &mut table[slot];
    if st.file_idx == usize::MAX {
        return -1;
    }
    let bytes = FILES[st.file_idx].1;
    let pos = st.pos as usize;
    if pos >= bytes.len() {
        return 0;
    }
    let to_copy = core::cmp::min(len, bytes.len() - pos);
    unsafe { core::ptr::copy_nonoverlapping(bytes.as_ptr().add(pos), dst, to_copy) }
    st.pos += to_copy as u64;
    to_copy as i32
}

pub fn pread(fd: i32, dst: *mut u8, len: usize, offset: u64) -> i32 {
    let slot = match fd_to_slot(fd) {
        Some(i) => i,
        None => return -1,
    };
    let st = table_mut()[slot];
    if st.file_idx == usize::MAX {
        return -1;
    }
    let bytes = FILES[st.file_idx].1;
    let pos = offset as usize;
    if pos >= bytes.len() {
        return 0;
    }
    let to_copy = core::cmp::min(len, bytes.len() - pos);
    unsafe { core::ptr::copy_nonoverlapping(bytes.as_ptr().add(pos), dst, to_copy) }
    to_copy as i32
}

/// Returns the new offset after seeking, or -1 on failure.
/// `whence`: 0=SET, 1=CUR, 2=END (wasi convention).
pub fn seek(fd: i32, offset: i64, whence: i32) -> i64 {
    let slot = match fd_to_slot(fd) {
        Some(i) => i,
        None => return -1,
    };
    let table = table_mut();
    let st = &mut table[slot];
    if st.file_idx == usize::MAX {
        return -1;
    }
    let bytes = FILES[st.file_idx].1;
    let new_pos = match whence {
        0 => offset, // SET
        1 => st.pos as i64 + offset,
        2 => bytes.len() as i64 + offset,
        _ => return -1,
    };
    if new_pos < 0 {
        return -1;
    }
    st.pos = new_pos as u64;
    new_pos
}

pub fn file_size(fd: i32) -> i64 {
    let slot = match fd_to_slot(fd) {
        Some(i) => i,
        None => return -1,
    };
    let st = table_mut()[slot];
    if st.file_idx == usize::MAX {
        return -1;
    }
    FILES[st.file_idx].1.len() as i64
}

/// Look up the size of a path (for fstatat/stat). Returns -1 if unknown.
pub fn path_size(path_ptr: *const u8) -> i64 {
    match lookup(path_ptr) {
        Some(b) => b.len() as i64,
        None => -1,
    }
}
