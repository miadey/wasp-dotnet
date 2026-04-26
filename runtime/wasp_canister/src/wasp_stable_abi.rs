//! `wasp_stable_abi` — Rust trampolines that managed C# code (running
//! inside Mono in the canister) calls via P/Invoke to reach the IC
//! system API.
//!
//! All wrappers go directly to the `ic0` system API. We deliberately
//! avoid `ic_cdk` and `candid` here so no trait-object dispatch
//! (call_indirect) leaks into the wasm — the wasm-table-merge pass
//! after `wasm-merge` can only handle a single function table.

use alloc::vec::Vec;
use core::cell::UnsafeCell;

#[link(wasm_import_module = "ic0")]
extern "C" {
    fn debug_print(src: u32, size: u32);
    fn msg_arg_data_size() -> u32;
    fn msg_arg_data_copy(dst: u32, offset: u32, size: u32);
    fn msg_caller_size() -> u32;
    fn msg_caller_copy(dst: u32, offset: u32, size: u32);
    fn msg_reply_data_append(src: u32, size: u32);
    fn msg_reply();
    fn trap(src: u32, size: u32) -> !;
    fn time() -> u64;
    fn stable64_size() -> u64;
    fn stable64_grow(new_pages: u64) -> u64;
    fn stable64_read(dst: u64, offset: u64, size: u64);
    fn stable64_write(offset: u64, src: u64, size: u64);
}

// Canister-instance-scoped caches for msg arg + caller. Single-threaded
// execution model means no synchronisation is needed; UnsafeCell rather
// than Cell so we can hand out borrowed slices.
struct OnceBytes(UnsafeCell<Option<Vec<u8>>>);
unsafe impl Sync for OnceBytes {}

static MSG_ARG_CACHE: OnceBytes = OnceBytes(UnsafeCell::new(None));
static CALLER_CACHE:  OnceBytes = OnceBytes(UnsafeCell::new(None));

unsafe fn msg_arg_bytes() -> &'static [u8] {
    let slot = &mut *MSG_ARG_CACHE.0.get();
    if slot.is_none() {
        let n = msg_arg_data_size() as usize;
        let mut buf: Vec<u8> = Vec::with_capacity(n);
        buf.set_len(n);
        if n > 0 {
            msg_arg_data_copy(buf.as_mut_ptr() as u32, 0, n as u32);
        }
        *slot = Some(buf);
    }
    slot.as_ref().unwrap_unchecked().as_slice()
}

unsafe fn caller_bytes() -> &'static [u8] {
    let slot = &mut *CALLER_CACHE.0.get();
    if slot.is_none() {
        let n = msg_caller_size() as usize;
        let mut buf: Vec<u8> = Vec::with_capacity(n);
        buf.set_len(n);
        if n > 0 {
            msg_caller_copy(buf.as_mut_ptr() as u32, 0, n as u32);
        }
        *slot = Some(buf);
    }
    slot.as_ref().unwrap_unchecked().as_slice()
}

// ---------------------------------------------------------------------------
// stable memory (64-bit; matches ic0.stable64_*)
// ---------------------------------------------------------------------------

#[no_mangle]
pub extern "C" fn wasp_stable_size() -> u64 {
    unsafe { stable64_size() }
}

#[no_mangle]
pub extern "C" fn wasp_stable_grow(new_pages: u64) -> u64 {
    unsafe { stable64_grow(new_pages) }
}

/// # Safety: dst must point to len writable bytes.
#[no_mangle]
pub unsafe extern "C" fn wasp_stable_read(offset: u64, dst: *mut u8, len: u64) {
    stable64_read(dst as u64, offset, len);
}

/// # Safety: src must point to len readable bytes.
#[no_mangle]
pub unsafe extern "C" fn wasp_stable_write(offset: u64, src: *const u8, len: u64) {
    stable64_write(offset, src as u64, len);
}

// ---------------------------------------------------------------------------
// message argument
// ---------------------------------------------------------------------------

#[no_mangle]
pub extern "C" fn wasp_msg_arg_size() -> u32 {
    unsafe { msg_arg_bytes().len() as u32 }
}

/// # Safety: dst must point to size writable bytes.
#[no_mangle]
pub unsafe extern "C" fn wasp_msg_arg_copy(dst: *mut u8, offset: u32, size: u32) {
    let src = msg_arg_bytes();
    let off = offset as usize;
    let sz = size as usize;
    core::ptr::copy_nonoverlapping(src.as_ptr().add(off), dst, sz);
}

// ---------------------------------------------------------------------------
// reply / trap
// ---------------------------------------------------------------------------

/// # Safety: src must point to len readable bytes.
#[no_mangle]
pub unsafe extern "C" fn wasp_reply(src: *const u8, len: u32) {
    msg_reply_data_append(src as u32, len);
    msg_reply();
}

/// # Safety: src must point to len readable bytes.
#[no_mangle]
pub unsafe extern "C" fn wasp_trap(src: *const u8, len: u32) {
    trap(src as u32, len)
}

// ---------------------------------------------------------------------------
// time, caller, debug_print
// ---------------------------------------------------------------------------

#[no_mangle]
pub extern "C" fn wasp_time() -> u64 {
    unsafe { time() }
}

#[no_mangle]
pub extern "C" fn wasp_caller_size() -> u32 {
    unsafe { caller_bytes().len() as u32 }
}

/// # Safety: dst must point to size writable bytes.
#[no_mangle]
pub unsafe extern "C" fn wasp_caller_copy(dst: *mut u8, offset: u32, size: u32) {
    let src = caller_bytes();
    let off = offset as usize;
    let sz = size as usize;
    core::ptr::copy_nonoverlapping(src.as_ptr().add(off), dst, sz);
}

/// # Safety: src must point to len readable bytes.
#[no_mangle]
pub unsafe extern "C" fn wasp_debug_print(src: *const u8, len: u32) {
    debug_print(src as u32, len);
}
