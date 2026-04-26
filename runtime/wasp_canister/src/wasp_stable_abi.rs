//! `wasp_stable_abi` — Rust trampolines that managed C# code (running
//! inside Mono in the canister) calls via P/Invoke to reach the IC
//! system API.
//!
//! Why this exists: Mono on wasm cannot directly emit `ic0::*` imports
//! the way the AOT path does (see `aot/Wasp.IcCdk/src/Ic0.cs`'s
//! `[WasmImportLinkage]` `[DllImport("ic0", ...)]` pattern). Instead,
//! managed code in the runtime canister does `[DllImport("wasp", ...)]`
//! and Mono's P/Invoke resolves those names against the surrounding
//! wasm module's exports. The functions in this file are precisely
//! those exports — each is `#[no_mangle] pub extern "C"` so the symbol
//! survives wasm-merge with the literal `wasp_*` name.
//!
//! All functions are thin wrappers around `ic_cdk::api::*`. The
//! convenience helpers (`wasp_msg_arg_*`, `wasp_caller_*`) cache the
//! requested data in a thread-local so the size+copy pattern that
//! `ic0` itself uses can be applied symmetrically by managed code.

use std::cell::RefCell;

use candid::Principal;

thread_local! {
    /// Lazily-populated cache of the inbound message argument bytes.
    /// `None` until first `wasp_msg_arg_size` call in this request.
    static MSG_ARG_CACHE: RefCell<Option<Vec<u8>>> = const { RefCell::new(None) };

    /// Lazily-populated cache of the caller principal as raw bytes.
    static CALLER_CACHE: RefCell<Option<Vec<u8>>> = const { RefCell::new(None) };
}

fn with_msg_arg<R>(f: impl FnOnce(&[u8]) -> R) -> R {
    MSG_ARG_CACHE.with(|c| {
        let mut slot = c.borrow_mut();
        if slot.is_none() {
            *slot = Some(ic_cdk::api::msg_arg_data());
        }
        f(slot.as_ref().unwrap().as_slice())
    })
}

fn with_caller<R>(f: impl FnOnce(&[u8]) -> R) -> R {
    CALLER_CACHE.with(|c| {
        let mut slot = c.borrow_mut();
        if slot.is_none() {
            let p: Principal = ic_cdk::api::msg_caller();
            *slot = Some(p.as_slice().to_vec());
        }
        f(slot.as_ref().unwrap().as_slice())
    })
}

// ---------------------------------------------------------------------------
// stable memory (64-bit; ic-cdk 0.19 dropped the `64` suffix from the names
// because the 32-bit syscalls were retired by the protocol)
// ---------------------------------------------------------------------------

/// Pages currently allocated in stable memory (1 page = 64 KiB).
#[no_mangle]
pub extern "C" fn wasp_stable_size() -> u64 {
    ic_cdk::api::stable_size()
}

/// Grow stable memory by `new_pages`. Returns the previous page count,
/// or `u64::MAX` on failure (matches the raw `ic0.stable64_grow` ABI).
#[no_mangle]
pub extern "C" fn wasp_stable_grow(new_pages: u64) -> u64 {
    ic_cdk::api::stable_grow(new_pages)
}

/// Read `len` bytes from stable memory starting at `offset` into `dst`.
///
/// # Safety
/// `dst` must be a valid writable pointer to `len` bytes of memory in the
/// caller's address space (here, the merged wasm module's linear memory).
#[no_mangle]
pub unsafe extern "C" fn wasp_stable_read(offset: u64, dst: *mut u8, len: u64) {
    let buf = std::slice::from_raw_parts_mut(dst, len as usize);
    ic_cdk::api::stable_read(offset, buf);
}

/// Write `len` bytes from `src` into stable memory starting at `offset`.
///
/// # Safety
/// `src` must be a valid readable pointer to `len` bytes of memory.
#[no_mangle]
pub unsafe extern "C" fn wasp_stable_write(offset: u64, src: *const u8, len: u64) {
    let buf = std::slice::from_raw_parts(src, len as usize);
    ic_cdk::api::stable_write(offset, buf);
}

// ---------------------------------------------------------------------------
// message argument
// ---------------------------------------------------------------------------

/// Length in bytes of the inbound message argument. Caches the bytes
/// on first call so subsequent `wasp_msg_arg_copy` is cheap.
#[no_mangle]
pub extern "C" fn wasp_msg_arg_size() -> u32 {
    with_msg_arg(|b| b.len() as u32)
}

/// Copy `size` bytes of the inbound message argument starting at
/// `offset` into `dst`.
///
/// # Safety
/// `dst` must be a valid writable pointer to `size` bytes; `offset+size`
/// must be in range of the cached arg buffer.
#[no_mangle]
pub unsafe extern "C" fn wasp_msg_arg_copy(dst: *mut u8, offset: u32, size: u32) {
    with_msg_arg(|src| {
        let off = offset as usize;
        let sz = size as usize;
        let slice = &src[off..off + sz];
        std::ptr::copy_nonoverlapping(slice.as_ptr(), dst, sz);
    });
}

// ---------------------------------------------------------------------------
// reply / trap
// ---------------------------------------------------------------------------

/// Reply to the current message with `len` bytes from `src`.
///
/// # Safety
/// `src` must be a valid readable pointer to `len` bytes.
#[no_mangle]
pub unsafe extern "C" fn wasp_reply(src: *const u8, len: u32) {
    let slice = std::slice::from_raw_parts(src, len as usize);
    // ic_cdk::api::msg_reply takes anything AsRef<[u8]>; pass by ref to
    // avoid the extra allocation a Vec round-trip would force.
    ic_cdk::api::msg_reply(slice);
}

/// Trap with the UTF-8 message at `src..src+len`. Does not return.
///
/// # Safety
/// `src` must be a valid readable pointer to `len` bytes of UTF-8.
#[no_mangle]
pub unsafe extern "C" fn wasp_trap(src: *const u8, len: u32) {
    let slice = std::slice::from_raw_parts(src, len as usize);
    let msg = std::str::from_utf8_unchecked(slice);
    ic_cdk::api::trap(msg)
}

// ---------------------------------------------------------------------------
// time, caller, debug_print
// ---------------------------------------------------------------------------

/// IC time in nanoseconds since the Unix epoch.
#[no_mangle]
pub extern "C" fn wasp_time() -> u64 {
    ic_cdk::api::time()
}

/// Length in bytes of the caller principal.
#[no_mangle]
pub extern "C" fn wasp_caller_size() -> u32 {
    with_caller(|b| b.len() as u32)
}

/// Copy `size` bytes of the caller principal starting at `offset` into `dst`.
///
/// # Safety
/// `dst` must be a valid writable pointer to `size` bytes; `offset+size`
/// must be in range of the cached principal byte buffer.
#[no_mangle]
pub unsafe extern "C" fn wasp_caller_copy(dst: *mut u8, offset: u32, size: u32) {
    with_caller(|src| {
        let off = offset as usize;
        let sz = size as usize;
        let slice = &src[off..off + sz];
        std::ptr::copy_nonoverlapping(slice.as_ptr(), dst, sz);
    });
}

/// Emit the UTF-8 message at `src..src+len` to the canister log.
///
/// # Safety
/// `src` must be a valid readable pointer to `len` bytes of UTF-8.
#[no_mangle]
pub unsafe extern "C" fn wasp_debug_print(src: *const u8, len: u32) {
    let slice = std::slice::from_raw_parts(src, len as usize);
    ic_cdk::api::debug_print(std::str::from_utf8_unchecked(slice));
}
