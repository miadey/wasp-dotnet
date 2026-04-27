//! wasp_canister — the Rust ic-cdk shim that hosts Microsoft's pre-built
//! `dotnet.native.wasm` inside an ICP canister.
//!
//! All update endpoints are written as raw `#[no_mangle] extern "C"`
//! exports against the ic0 system API to avoid candid serde machinery.
//! Reason: candid decode pulls in trait-object dispatch (call_indirect)
//! that the wasm-table-merge pass dropped.
//!
//! `#![no_std]` + custom `#[panic_handler]` is required for the same
//! reason: std's panic machinery formats panic messages via fmt::Write
//! trait objects, which also produce indirect calls.

#![no_std]

extern crate alloc;

use alloc::collections::BTreeMap;
use alloc::vec::Vec;
use core::cell::UnsafeCell;

pub mod env_imports;
pub mod mono_embed;
pub mod vfs;
pub mod wasi_imports;
pub mod wasp_stable_abi;

#[panic_handler]
fn panic(_info: &core::panic::PanicInfo) -> ! {
    unsafe {
        let m = b"wasp_canister: panic";
        trap(m.as_ptr() as u32, m.len() as u32);
    }
}

/// Use dotnet.native.wasm's `malloc`/`free` as Rust's global allocator
/// so Rust-side `Vec` allocations and Mono's internal allocations come
/// from the same dlmalloc instance. Two independent allocators in the
/// same linear memory hand out overlapping addresses → "heap out of
/// bounds" the moment one of them touches the other's region.
struct MonoAllocator;

unsafe impl core::alloc::GlobalAlloc for MonoAllocator {
    unsafe fn alloc(&self, layout: core::alloc::Layout) -> *mut u8 {
        let p = mono_embed::malloc(layout.size());
        if p.is_null() {
            let m = b"wasp_canister: mono malloc returned NULL";
            trap(m.as_ptr() as u32, m.len() as u32);
        }
        p
    }
    unsafe fn dealloc(&self, ptr: *mut u8, _layout: core::alloc::Layout) {
        mono_embed::free(ptr)
    }
}

#[global_allocator]
static ALLOC: MonoAllocator = MonoAllocator;


// ---------------------------------------------------------------------------
// Static state
// ---------------------------------------------------------------------------

static mut UPLOADED_NAMES: Vec<Vec<u8>> = Vec::new();
static mut UPLOADED_BYTES: Vec<Vec<u8>> = Vec::new();
static mut MONO_BOOTED: bool = false;
static mut REGISTERED_COUNT: usize = 0;

static APP_BASE_KEY: &[u8] = b"APP_CONTEXT_BASE_DIRECTORY\0";
static APP_BASE_VAL: &[u8] = b"/\0";
static RID_KEY: &[u8]      = b"RUNTIME_IDENTIFIER\0";
static RID_VAL: &[u8]      = b"browser-wasm\0";
static INV_KEY: &[u8]      = b"System.Globalization.Invariant\0";
static INV_VAL: &[u8]      = b"true\0";

static TZ_INV_NAME: &[u8]  = b"DOTNET_SYSTEM_TIMEZONE_INVARIANT\0";
static TZ_INV_VAL: &[u8]   = b"true\0";

static MONO_DEBUG_KEY: &[u8] = b"MONO_DEBUG\0";
static MONO_DEBUG_VAL: &[u8] = b"\0"; // empty value avoids the parse-error exit(1) at mini-runtime.c:4279

static MONO_LOG_LEVEL_KEY: &[u8] = b"MONO_LOG_LEVEL\0";
static MONO_LOG_LEVEL_VAL: &[u8] = b"debug\0";
static MONO_LOG_MASK_KEY: &[u8] = b"MONO_LOG_MASK\0";
static MONO_LOG_MASK_VAL: &[u8] = b"all\0";

// ---------------------------------------------------------------------------
// ic0 system API
// ---------------------------------------------------------------------------

#[link(wasm_import_module = "ic0")]
extern "C" {
    fn debug_print(src: u32, size: u32);
    fn msg_arg_data_size() -> u32;
    fn msg_arg_data_copy(dst: u32, offset: u32, size: u32);
    fn msg_reply_data_append(src: u32, size: u32);
    fn msg_reply();
    fn trap(src: u32, size: u32) -> !;
}

/// Raw `ic0::debug_print` from a byte slice. No format machinery.
fn print(bytes: &[u8]) {
    unsafe { debug_print(bytes.as_ptr() as u32, bytes.len() as u32) }
}

/// Format `v` as decimal at *p[i..], returning new offset.
unsafe fn format_decimal_at(p: *mut u8, mut i: usize, mut v: u64) -> usize {
    if v == 0 { *p.add(i) = b'0'; return i + 1; }
    let mut tmp = [0u8; 20];
    let mut tlen = 0;
    while v > 0 {
        tmp[tlen] = b'0' + (v % 10) as u8;
        v /= 10;
        tlen += 1;
    }
    while tlen > 0 {
        tlen -= 1;
        *p.add(i) = tmp[tlen]; i += 1;
    }
    i
}

/// Format `v` as decimal ASCII into `buf[i..]`, returning new offset.
fn format_decimal(buf: &mut [u8], mut i: usize, mut v: u64) -> usize {
    if v == 0 {
        if i < buf.len() { buf[i] = b'0'; i += 1; }
        return i;
    }
    let mut tmp = [0u8; 20];
    let mut tlen = 0;
    while v > 0 {
        tmp[tlen] = b'0' + (v % 10) as u8;
        v /= 10;
        tlen += 1;
    }
    while tlen > 0 {
        tlen -= 1;
        if i < buf.len() { buf[i] = tmp[tlen]; i += 1; }
    }
    i
}

/// Parse a candid-encoded single `(blob)` arg, returning a slice of
/// the contained bytes inside `buf`. Layout:
///   "DIDL" 0x01 0x6d 0x7b 0x01 0x00 <LEB128 len> <bytes...>
/// Returns the (offset, length) of the payload inside `buf`.
unsafe fn parse_candid_blob_arg(buf: &[u8]) -> Option<(usize, usize)> {
    if buf.len() < 9 {
        return None;
    }
    if &buf[0..4] != b"DIDL" {
        return None;
    }
    if buf[4] != 0x01 || buf[5] != 0x6d || buf[6] != 0x7b
        || buf[7] != 0x01 || buf[8] != 0x00
    {
        return None;
    }
    // LEB128 length
    let mut i = 9;
    let mut len: usize = 0;
    let mut shift: u32 = 0;
    while i < buf.len() {
        let b = buf[i];
        i += 1;
        len |= ((b & 0x7f) as usize) << shift;
        if b & 0x80 == 0 {
            break;
        }
        shift += 7;
        if shift > 35 {
            return None;
        }
    }
    if i + len > buf.len() {
        return None;
    }
    Some((i, len))
}

/// Write a candid-encoded `(blob)` response of `payload` and call
/// `msg_reply`. Header: "DIDL" 01 6d 7b 01 00, then LEB128(len), then
/// the bytes themselves.
unsafe fn reply_blob(payload: &[u8]) {
    // Fixed header for return type "(vec nat8)".
    let header: [u8; 6] = [b'D', b'I', b'D', b'L', 0x01, 0x6d];
    msg_reply_data_append(header.as_ptr() as u32, header.len() as u32);
    let typ: [u8; 3] = [0x7b, 0x01, 0x00];
    msg_reply_data_append(typ.as_ptr() as u32, typ.len() as u32);
    // LEB128(len)
    let mut leb = [0u8; 10];
    let mut n = payload.len() as u64;
    let mut llen = 0;
    loop {
        let byte = (n & 0x7f) as u8;
        n >>= 7;
        if n == 0 {
            leb[llen] = byte;
            llen += 1;
            break;
        } else {
            leb[llen] = byte | 0x80;
            llen += 1;
        }
    }
    msg_reply_data_append(leb.as_ptr() as u32, llen as u32);
    if !payload.is_empty() {
        msg_reply_data_append(payload.as_ptr() as u32, payload.len() as u32);
    }
    msg_reply();
}

// ---------------------------------------------------------------------------
// canister_init — run wasm static initialisers
// ---------------------------------------------------------------------------

#[no_mangle]
pub extern "C" fn canister_init() {
    print(b"[wasp-dotnet] canister_init: pre-ctors");
    unsafe {
        mono_embed::__wasm_call_ctors();
        // Pre-fill Mono's dlmalloc pool with one large allocation. Forces
        // ONE memory.grow → shift cycle here at init (before any Rust
        // pointer values are stored long-term). Subsequent allocations
        // reuse the freed pool without growing → no further shifts → all
        // long-lived Rust pointers stay valid.
        let p = mono_embed::malloc(20 * 1024 * 1024);
        if !p.is_null() {
            mono_embed::free(p);
        }
    }
    print(b"[wasp-dotnet] canister_init: __wasm_call_ctors done");
}

// ---------------------------------------------------------------------------
// Bundled resources bypass — replaces dn_simdhash entirely.
//
// Mono's `dn_simdhash` (the SIMD-accelerated hashtable used by
// `mono_bundled_resources_*`) deterministically corrupts on the 3rd
// distinct-pointer insert, regardless of content / allocator / name
// location. Tested across .NET 9.0.15, 10.0.6, 10.0.7, and 11.0
// preview — bug present in every version with dn_simdhash.
//
// Strategy: keep our own table here in Rust, then patch
// `mono_wasm_add_assembly` (fn 1274) and
// `mono_bundled_resources_get_assembly_resource` (fn 5662) in the
// merged canister wasm to call into these Rust functions instead of
// going through Mono's bundled-resources path.
//
// Resource struct layout (matches what fn 1274 builds via g_new0):
//   offset  0: type   = MONO_BUNDLED_ASSEMBLY = 1
//   offset  4: id     = name pointer (relative)
//   offset  8: hash   = constant 458 in mono's build
//   offset 12: free_data
//   offset 16: name   = name pointer (relative)
//   offset 20: data   = bytes pointer (relative)
//   offset 24: size   = i32
//   offset 28: pdb1   = 0 (no PDB)
//   offset 32: pdb2   = 0 (no PDB)
// ---------------------------------------------------------------------------

struct AsmMap(UnsafeCell<Option<BTreeMap<Vec<u8>, u32>>>);
unsafe impl Sync for AsmMap {}
static ASM_MAP: AsmMap = AsmMap(UnsafeCell::new(None));

unsafe fn asm_map_mut() -> &'static mut BTreeMap<Vec<u8>, u32> {
    let slot = &mut *ASM_MAP.0.get();
    if slot.is_none() {
        *slot = Some(BTreeMap::new());
    }
    slot.as_mut().unwrap_unchecked()
}

/// Read a NUL-terminated string starting at dotnet-relative `rel_ptr`.
unsafe fn read_cstr_rel(rel_ptr: u32) -> Vec<u8> {
    let abs = rel_ptr.wrapping_add(DOTNET_MEMORY_BASE) as *const u8;
    let mut v = Vec::new();
    let mut i = 0;
    loop {
        let b = *abs.add(i);
        if b == 0 {
            break;
        }
        v.push(b);
        i += 1;
    }
    v
}

/// Replacement for `mono_wasm_add_assembly` (fn 1274 in merged wasm).
/// Builds a MonoBundledResource struct in Mono's malloc heap and
/// stores it in our own map under the name AND a few common variants
/// (with and without ".dll" suffix) so lookups under any form succeed.
#[no_mangle]
pub unsafe extern "C" fn wasp_add_assembly(name_rel: u32, data_rel: u32, size: u32) -> u32 {
    let name = read_cstr_rel(name_rel);
    let res = mono_embed::malloc(36);
    if res.is_null() {
        let m = b"wasp_add_assembly: malloc NULL";
        trap(m.as_ptr() as u32, m.len() as u32);
    }
    let p = res as *mut u32;
    *p.add(0) = 1;
    *p.add(1) = name_rel;
    *p.add(2) = 458;
    *p.add(3) = 0;
    *p.add(4) = name_rel;
    *p.add(5) = data_rel;
    *p.add(6) = size;
    *p.add(7) = 0;
    *p.add(8) = 0;

    let res_rel = (res as u32).wrapping_sub(DOTNET_MEMORY_BASE);
    asm_map_mut().insert(name, res_rel);
    REGISTERED_COUNT += 1;
    1
}

/// Replacement for `mono_bundled_resources_get_assembly_resource`
/// AND `bundled_resources_get` (the lower-level lookup). Returns the
/// resource struct ptr (relative) or 0 if not found.
#[no_mangle]
pub unsafe extern "C" fn wasp_get_assembly(name_rel: u32) -> u32 {
    let name = read_cstr_rel(name_rel);
    let slot = &*ASM_MAP.0.get();
    let map = slot.as_ref();
    // Try the exact name first, then try with ".dll" suffix added
    // (caller might pass "System.Private.CoreLib" expecting bundled
    // resources for "System.Private.CoreLib.dll").
    let mut result = map.and_then(|m| m.get(&name)).copied().unwrap_or(0);
    if result == 0 && !name.ends_with(b".dll") {
        let mut with_dll = name.clone();
        with_dll.extend_from_slice(b".dll");
        result = map.and_then(|m| m.get(&with_dll)).copied().unwrap_or(0);
    }
    let mut buf = [0u8; 96];
    let mut i = 0;
    for &b in b"[wasp-get] " { buf[i] = b; i += 1; }
    let name_max = if name.len() < 60 { name.len() } else { 60 };
    let mut j = 0;
    while j < name_max { buf[i] = name[j]; i += 1; j += 1; }
    for &b in b" -> " { buf[i] = b; i += 1; }
    if result == 0 {
        for &b in b"NULL" { buf[i] = b; i += 1; }
    } else {
        for &b in b"OK" { buf[i] = b; i += 1; }
    }
    debug_print(buf.as_ptr() as u32, i as u32);
    result
}

// ---------------------------------------------------------------------------
// init-time test harness: register ALL 35 BCL dlls + boot Mono
// ---------------------------------------------------------------------------

static BUILTIN_BCL: [(&[u8], &[u8]); 34] = [
    (b"System.Private.CoreLib.dll\0", include_bytes!("../../inputs/System.Private.CoreLib.dll")),
    (b"Microsoft.AspNetCore.Components.Web.dll\0", include_bytes!("../../inputs/bcl_extracted/Microsoft.AspNetCore.Components.Web.dll")),
    (b"Microsoft.AspNetCore.Components.WebAssembly.dll\0", include_bytes!("../../inputs/bcl_extracted/Microsoft.AspNetCore.Components.WebAssembly.dll")),
    (b"Microsoft.AspNetCore.Components.dll\0", include_bytes!("../../inputs/bcl_extracted/Microsoft.AspNetCore.Components.dll")),
    (b"Microsoft.Extensions.Configuration.Abstractions.dll\0", include_bytes!("../../inputs/bcl_extracted/Microsoft.Extensions.Configuration.Abstractions.dll")),
    (b"Microsoft.Extensions.Configuration.Json.dll\0", include_bytes!("../../inputs/bcl_extracted/Microsoft.Extensions.Configuration.Json.dll")),
    (b"Microsoft.Extensions.Configuration.dll\0", include_bytes!("../../inputs/bcl_extracted/Microsoft.Extensions.Configuration.dll")),
    (b"Microsoft.Extensions.DependencyInjection.Abstractions.dll\0", include_bytes!("../../inputs/bcl_extracted/Microsoft.Extensions.DependencyInjection.Abstractions.dll")),
    (b"Microsoft.Extensions.DependencyInjection.dll\0", include_bytes!("../../inputs/bcl_extracted/Microsoft.Extensions.DependencyInjection.dll")),
    (b"Microsoft.Extensions.Logging.Abstractions.dll\0", include_bytes!("../../inputs/bcl_extracted/Microsoft.Extensions.Logging.Abstractions.dll")),
    (b"Microsoft.Extensions.Logging.dll\0", include_bytes!("../../inputs/bcl_extracted/Microsoft.Extensions.Logging.dll")),
    (b"Microsoft.Extensions.Options.dll\0", include_bytes!("../../inputs/bcl_extracted/Microsoft.Extensions.Options.dll")),
    (b"Microsoft.Extensions.Primitives.dll\0", include_bytes!("../../inputs/bcl_extracted/Microsoft.Extensions.Primitives.dll")),
    (b"Microsoft.JSInterop.WebAssembly.dll\0", include_bytes!("../../inputs/bcl_extracted/Microsoft.JSInterop.WebAssembly.dll")),
    (b"Microsoft.JSInterop.dll\0", include_bytes!("../../inputs/bcl_extracted/Microsoft.JSInterop.dll")),
    (b"System.Collections.Concurrent.dll\0", include_bytes!("../../inputs/bcl_extracted/System.Collections.Concurrent.dll")),
    (b"System.Collections.Immutable.dll\0", include_bytes!("../../inputs/bcl_extracted/System.Collections.Immutable.dll")),
    (b"System.Collections.dll\0", include_bytes!("../../inputs/bcl_extracted/System.Collections.dll")),
    (b"System.ComponentModel.dll\0", include_bytes!("../../inputs/bcl_extracted/System.ComponentModel.dll")),
    (b"System.Console.dll\0", include_bytes!("../../inputs/bcl_extracted/System.Console.dll")),
    (b"System.Diagnostics.DiagnosticSource.dll\0", include_bytes!("../../inputs/bcl_extracted/System.Diagnostics.DiagnosticSource.dll")),
    (b"System.IO.Pipelines.dll\0", include_bytes!("../../inputs/bcl_extracted/System.IO.Pipelines.dll")),
    (b"System.Linq.dll\0", include_bytes!("../../inputs/bcl_extracted/System.Linq.dll")),
    (b"System.Memory.dll\0", include_bytes!("../../inputs/bcl_extracted/System.Memory.dll")),
    (b"System.Net.Http.Json.dll\0", include_bytes!("../../inputs/bcl_extracted/System.Net.Http.Json.dll")),
    (b"System.Net.Http.dll\0", include_bytes!("../../inputs/bcl_extracted/System.Net.Http.dll")),
    (b"System.Net.Primitives.dll\0", include_bytes!("../../inputs/bcl_extracted/System.Net.Primitives.dll")),
    (b"System.Private.Uri.dll\0", include_bytes!("../../inputs/bcl_extracted/System.Private.Uri.dll")),
    (b"System.Runtime.InteropServices.JavaScript.dll\0", include_bytes!("../../inputs/bcl_extracted/System.Runtime.InteropServices.JavaScript.dll")),
    (b"System.Runtime.dll\0", include_bytes!("../../inputs/bcl_extracted/System.Runtime.dll")),
    (b"System.Security.Cryptography.dll\0", include_bytes!("../../inputs/bcl_extracted/System.Security.Cryptography.dll")),
    (b"System.Text.Encodings.Web.dll\0", include_bytes!("../../inputs/bcl_extracted/System.Text.Encodings.Web.dll")),
    (b"System.Text.Json.dll\0", include_bytes!("../../inputs/bcl_extracted/System.Text.Json.dll")),
    (b"System.Text.RegularExpressions.dll\0", include_bytes!("../../inputs/bcl_extracted/System.Text.RegularExpressions.dll")),
];

unsafe fn cmh(src: &[u8], pad: usize) -> *mut u8 {
    let dst = mono_embed::malloc(src.len() + pad);
    if dst.is_null() { let m = b"NULL"; trap(m.as_ptr() as u32, m.len() as u32); }
    let mut i = 0;
    while i < src.len() { *dst.add(i) = src[i]; i += 1; }
    let mut j = 0;
    while j < pad { *dst.add(src.len() + j) = 0; j += 1; }
    dst
}

unsafe fn add1(name_src: &[u8], bytes_src: &[u8]) {
    let name = cmh(name_src, 0);
    let bytes = cmh(bytes_src, 4096);
    mono_embed::mono_wasm_add_assembly(
        dotnet_offset(name), dotnet_offset(bytes), bytes_src.len() as i32);
}

#[export_name = "canister_update register_all"]
pub extern "C" fn canister_update_register_all() {
    unsafe {
        let mut i = 0;
        while i < BUILTIN_BCL.len() {
            let (n, b) = BUILTIN_BCL[i];
            add1(n, b);
            i += 1;
        }
        let mut buf = [0u8; 64];
        let mut bi = 0;
        for &c in b"all registered: " { buf[bi] = c; bi += 1; }
        bi = format_decimal(&mut buf, bi, BUILTIN_BCL.len() as u64);
        reply_blob(&buf[..bi]);
    }
}

#[export_name = "canister_update boot_mono"]
pub extern "C" fn canister_update_boot_mono() {
    unsafe {
        if MONO_BOOTED { reply_blob(b"already booted"); return; }
        print(b"[wasp-boot] setenv");
        // Mono code does `global.get 7 + arg` to dereference; pointers
        // must be dotnet-relative (caller subtracts DOTNET_MEMORY_BASE).
        mono_embed::mono_wasm_setenv(
            dotnet_offset(TZ_INV_NAME.as_ptr()),
            dotnet_offset(TZ_INV_VAL.as_ptr()));
        mono_embed::mono_wasm_setenv(
            dotnet_offset(MONO_DEBUG_KEY.as_ptr()),
            dotnet_offset(MONO_DEBUG_VAL.as_ptr()));
        // NOTE: a 3rd distinct setenv triggers the dn_simdhash bug
        // (setenv uses simdhash internally). Skipping MONO_LOG_LEVEL
        // / MONO_LOG_MASK until we patch the env hashtable too.

        print(b"[wasp-boot] build keys/vals in dotnet heap");
        // Build keys/vals arrays in mono malloc heap so g7 + array_ptr
        // reads them. Each entry is a dotnet-relative pointer to the
        // corresponding NUL-terminated string in our static data.
        let keys_arr = mono_embed::malloc(12) as *mut u32;
        *keys_arr.add(0) = dotnet_offset(APP_BASE_KEY.as_ptr()) as u32;
        *keys_arr.add(1) = dotnet_offset(RID_KEY.as_ptr()) as u32;
        *keys_arr.add(2) = dotnet_offset(INV_KEY.as_ptr()) as u32;

        let vals_arr = mono_embed::malloc(12) as *mut u32;
        *vals_arr.add(0) = dotnet_offset(APP_BASE_VAL.as_ptr()) as u32;
        *vals_arr.add(1) = dotnet_offset(RID_VAL.as_ptr()) as u32;
        *vals_arr.add(2) = dotnet_offset(INV_VAL.as_ptr()) as u32;

        print(b"[wasp-boot] load_runtime");
        mono_embed::mono_wasm_load_runtime(
            0,
            3,
            dotnet_offset(keys_arr as *const u8) as *const *const u8,
            dotnet_offset(vals_arr as *const u8) as *const *const u8,
        );
        MONO_BOOTED = true;
        reply_blob(b"booted!");
    }
}

// ---------------------------------------------------------------------------
// upload_chunk — raw binary protocol
//
//   payload format (all little-endian):
//     [u32 name_len]
//     [name_len bytes  name]
//     [u32 total_size]   total bytes for this assembly (set on first chunk)
//     [u8  final_flag]   0 or 1
//     [...remaining bytes are chunk data]
//
//   reply: [u64 total_bytes_for_this_assembly so far]
//
// We require the caller to send `total_size` so we can allocate the
// destination buffer once via mono malloc and write chunks into it
// without Vec-grow doubling, which fragments the heap.
// ---------------------------------------------------------------------------

#[export_name = "canister_update upload_chunk"]
pub extern "C" fn canister_update_upload_chunk() {
    unsafe {
        let size = msg_arg_data_size() as usize;
        let mut buf: Vec<u8> = Vec::with_capacity(size);
        buf.set_len(size);
        msg_arg_data_copy(buf.as_mut_ptr() as u32, 0, size as u32);

        let (poff, plen) = match parse_candid_blob_arg(&buf) {
            Some(p) => p,
            None => {
                let m = b"upload_chunk: bad candid arg";
                trap(m.as_ptr() as u32, m.len() as u32);
            }
        };
        let payload = &buf[poff..poff + plen];

        if payload.len() < 5 {
            let m = b"upload_chunk: payload too small";
            trap(m.as_ptr() as u32, m.len() as u32);
        }
        let name_len = u32::from_le_bytes([payload[0], payload[1], payload[2], payload[3]]) as usize;
        let header_end = 4 + name_len + 4 + 1;
        if payload.len() < header_end {
            let m = b"upload_chunk: header exceeds payload";
            trap(m.as_ptr() as u32, m.len() as u32);
        }
        let name: Vec<u8> = payload[4..4 + name_len].to_vec();
        let total_size = u32::from_le_bytes([
            payload[4 + name_len],
            payload[5 + name_len],
            payload[6 + name_len],
            payload[7 + name_len],
        ]) as usize;
        let _final = payload[8 + name_len] != 0;
        let chunk_start = header_end;

        // Find existing slot by name.
        let mut idx: usize = usize::MAX;
        let n = UPLOADED_NAMES.len();
        let mut i = 0;
        while i < n {
            if UPLOADED_NAMES[i].as_slice() == name.as_slice() {
                idx = i;
                break;
            }
            i += 1;
        }
        if idx == usize::MAX {
            // First chunk for this assembly: pre-allocate the full
            // destination buffer + 4 KiB safety pad so anything that
            // scans past the assembly bytes (e.g. mono_has_pdb_checksum)
            // reads zero-initialised tail rather than hitting the next
            // mono malloc allocation's slab boundary.
            let mut dst: Vec<u8> = Vec::with_capacity(total_size + 4096);
            dst.extend_from_slice(&payload[chunk_start..]);
            UPLOADED_NAMES.push(name);
            UPLOADED_BYTES.push(dst);
            idx = UPLOADED_NAMES.len() - 1;
        } else {
            UPLOADED_BYTES[idx].extend_from_slice(&payload[chunk_start..]);
        }

        let total = UPLOADED_BYTES[idx].len() as u64;
        let total_bytes = total.to_le_bytes();
        reply_blob(&total_bytes);
    }
}

// ---------------------------------------------------------------------------
// boot — register uploaded assemblies + load Mono
//
//   payload: empty
//   reply:   ascii bytes ("booted" or "already-booted")
// ---------------------------------------------------------------------------

/// Static-data add_assembly: registers a .dll that lives in
/// wasp_canister's data section (NOT in mono malloc heap). Tests
/// whether Mono's bundled-resources internals require pointer
/// provenance from mono malloc.
static STATIC_DLL: &[u8] = include_bytes!("../../inputs/bcl_extracted/System.Runtime.dll");
static STATIC_NAMES: [&[u8]; 4] = [
    b"StaticA.dll\0",
    b"StaticB.dll\0",
    b"StaticC.dll\0",
    b"StaticD.dll\0",
];

#[export_name = "canister_update static_add"]
pub extern "C" fn canister_update_static_add() {
    unsafe {
        let count = REGISTERED_COUNT;
        if count >= STATIC_NAMES.len() {
            reply_blob(b"static-all-done");
            return;
        }
        REGISTERED_COUNT += 1;

        let name = STATIC_NAMES[count];
        let data = STATIC_DLL;

        let mut buf = [0u8; 128];
        let mut i = 0;
        for &b in b"[wasp-dotnet] static_add " { buf[i] = b; i += 1; }
        i = format_decimal(&mut buf, i, count as u64 + 1);
        for &b in b" name_ptr=" { buf[i] = b; i += 1; }
        i = format_decimal(&mut buf, i, name.as_ptr() as u64);
        for &b in b" data_ptr=" { buf[i] = b; i += 1; }
        i = format_decimal(&mut buf, i, data.as_ptr() as u64);
        print(&buf[..i]);

        let _rc = mono_embed::mono_wasm_add_assembly(
            dotnet_offset(name.as_ptr()),
            dotnet_offset(data.as_ptr()),
            data.len() as i32,
        );

        reply_blob(b"static_add returned");
    }
}

/// `__memory_base` of the merged dotnet.native.wasm module — the
/// wasm-merge relocated dotnet's data to start at this absolute byte
/// offset. Any pointer passed THROUGH dotnet's exported ABI (such as
/// `mono_wasm_add_assembly`) is expected to be RELATIVE to this base
/// (dotnet's code does `global.get 7 + arg_ptr` to compute the
/// effective address). Our shim's absolute addresses must be translated
/// by subtracting this constant before crossing the dotnet boundary.
pub(crate) const DOTNET_MEMORY_BASE: u32 = 2_752_512;

#[inline]
fn dotnet_offset(p: *const u8) -> *const u8 {
    ((p as u32).wrapping_sub(DOTNET_MEMORY_BASE)) as *const u8
}

/// Inverse of dotnet_offset: given a dotnet-relative ptr received from
/// Mono code (e.g. as a callback arg), return the absolute address in
/// our linear memory.
#[inline]
pub(crate) fn dotnet_to_abs(rel: u32) -> *const u8 {
    rel.wrapping_add(DOTNET_MEMORY_BASE) as *const u8
}

/// Pure synthetic add_assembly (no upload required). Lets us reproduce
/// the third-add_assembly trap independent of upload state.
#[export_name = "canister_update synth_add"]
pub extern "C" fn canister_update_synth_add() {
    unsafe {
        // Allocate a fresh name + 1KB zero buffer per call, register.
        let count = REGISTERED_COUNT;
        REGISTERED_COUNT += 1;

        // Name: "Asm<N>.dll" via mono_embed::malloc
        let name = mono_embed::malloc(16);
        let mut nlen = 0;
        for &b in b"Asm" { *name.add(nlen) = b; nlen += 1; }
        nlen = format_decimal_at(name, nlen, count as u64);
        for &b in b".dll" { *name.add(nlen) = b; nlen += 1; }
        *name.add(nlen) = 0;

        // Use a 64KB buffer with zero PE structure but plenty of pad
        // so mono_has_pdb_checksum's metadata scan can't read OOB.
        let data_size = 65536;
        let data = mono_embed::malloc(data_size);
        core::ptr::write_bytes(data, 0u8, data_size);

        let pages = core::arch::wasm32::memory_size(0) as u32;
        let mut buf = [0u8; 128];
        let mut i = 0;
        for &b in b"[wasp-dotnet] synth_add " { buf[i] = b; i += 1; }
        i = format_decimal(&mut buf, i, count as u64 + 1);
        for &b in b" pages=" { buf[i] = b; i += 1; }
        i = format_decimal(&mut buf, i, pages as u64);
        for &b in b" name_ptr=" { buf[i] = b; i += 1; }
        i = format_decimal(&mut buf, i, name as u64);
        for &b in b" data_ptr=" { buf[i] = b; i += 1; }
        i = format_decimal(&mut buf, i, data as u64);
        print(&buf[..i]);

        let _rc = mono_embed::mono_wasm_add_assembly(
            dotnet_offset(name),
            dotnet_offset(data),
            data_size as i32,
        );

        let mut buf2 = [0u8; 64];
        let pre = b"synth_add returned ";
        let mut bi = 0;
        for &b in pre { buf2[bi] = b; bi += 1; }
        bi = format_decimal(&mut buf2, bi, count as u64 + 1);
        reply_blob(&buf2[..bi]);
    }
}

/// Register one assembly per call. The IC delivers each update as a
/// fresh canister entry which resets the wasm stack pointer — so this
/// tests whether the multi-call-trap is stack-pointer related.
///
/// Returns the running count of registered assemblies.
#[export_name = "canister_update register_one"]
pub extern "C" fn canister_update_register_one() {
    unsafe {
        let n = UPLOADED_NAMES.len();
        if REGISTERED_COUNT >= n {
            reply_blob(b"all-registered");
            return;
        }
        let name = &UPLOADED_NAMES[REGISTERED_COUNT];
        let bytes = &UPLOADED_BYTES[REGISTERED_COUNT];

        // Diagnostic: dump current memory size + bytes ptr value before
        // calling add_assembly so we can see whether a trap correlates
        // with a memory boundary.
        let pages = core::arch::wasm32::memory_size(0) as u32;
        let bp = bytes.as_ptr() as u32;
        let mut buf = [0u8; 128];
        let pre = b"[wasp-dotnet] register_one ";
        let mut i = 0;
        for &b in pre { buf[i] = b; i += 1; }
        // Format: "register_one <count> pages=<N> bytes_ptr=<hex> len=<N>"
        let count = REGISTERED_COUNT as u64 + 1;
        i = format_decimal(&mut buf, i, count);
        for &b in b" pages=" { buf[i] = b; i += 1; }
        i = format_decimal(&mut buf, i, pages as u64);
        for &b in b" bytes_ptr=" { buf[i] = b; i += 1; }
        i = format_decimal(&mut buf, i, bp as u64);
        for &b in b" len=" { buf[i] = b; i += 1; }
        i = format_decimal(&mut buf, i, bytes.len() as u64);
        print(&buf[..i]);

        // Mono stores the name POINTER (not a copy) in its
        // bundled-resources simdhash. The buffer must outlive every
        // future lookup, so allocate via mono_embed::malloc directly
        // and intentionally leak it for the canister's lifetime.
        let name_z = mono_embed::malloc(name.len() + 1);
        let mut k = 0;
        while k < name.len() {
            *name_z.add(k) = name[k];
            k += 1;
        }
        *name_z.add(name.len()) = 0;

        let _rc = mono_embed::mono_wasm_add_assembly(
            dotnet_offset(name_z),
            dotnet_offset(bytes.as_ptr()),
            bytes.len() as i32,
        );

        REGISTERED_COUNT += 1;

        let mut buf = [0u8; 64];
        let pre = b"registered ";
        let mut i = 0;
        for &b in pre { buf[i] = b; i += 1; }
        let mut v = REGISTERED_COUNT as u64;
        let mut tmp = [0u8; 12];
        let mut tlen = 0;
        if v == 0 { tmp[tlen] = b'0'; tlen += 1; }
        else { while v > 0 { tmp[tlen] = b'0' + (v % 10) as u8; v /= 10; tlen += 1; } }
        while tlen > 0 { tlen -= 1; buf[i] = tmp[tlen]; i += 1; }
        reply_blob(&buf[..i]);
    }
}

#[export_name = "canister_update boot"]
pub extern "C" fn canister_update_boot() {
    unsafe {
        if MONO_BOOTED {
            reply_blob(b"already-booted");
            return;
        }

        let n = UPLOADED_NAMES.len();
        if REGISTERED_COUNT < n {
            // Boot must NOT batch-register — that triggers the
            // multi-add_assembly stack-pointer trap. The client must
            // call register_one N times (one per IC update message)
            // to register all uploaded assemblies first.
            reply_blob(b"call register_one until all-registered, then boot");
            return;
        }

        print(b"[wasp-dotnet] boot: setenv");
        mono_embed::mono_wasm_setenv(TZ_INV_NAME.as_ptr(), TZ_INV_VAL.as_ptr());
        mono_embed::mono_wasm_setenv(MONO_DEBUG_KEY.as_ptr(), MONO_DEBUG_VAL.as_ptr());

        print(b"[wasp-dotnet] boot: about to call mono_wasm_load_runtime");
        let keys: [*const u8; 3] = [
            APP_BASE_KEY.as_ptr(),
            RID_KEY.as_ptr(),
            INV_KEY.as_ptr(),
        ];
        let vals: [*const u8; 3] = [
            APP_BASE_VAL.as_ptr(),
            RID_VAL.as_ptr(),
            INV_VAL.as_ptr(),
        ];
        mono_embed::mono_wasm_load_runtime(
            0,
            3,
            keys.as_ptr(),
            vals.as_ptr(),
        );
        print(b"[wasp-dotnet] boot: mono_wasm_load_runtime returned");

        MONO_BOOTED = true;

        reply_blob(b"booted");
    }
}

// ---------------------------------------------------------------------------
// hello — returns a one-shot ascii payload (raw, no candid)
// ---------------------------------------------------------------------------

#[export_name = "canister_query hello"]
pub extern "C" fn canister_query_hello() {
    unsafe {
        let booted = MONO_BOOTED;
        let count = UPLOADED_NAMES.len();

        let mut out: Vec<u8> = Vec::with_capacity(64);
        out.extend_from_slice(b"booted=");
        out.extend_from_slice(if booted { b"true" } else { b"false" });
        out.extend_from_slice(b" assemblies=");
        // u64 -> ascii without format!
        let mut buf = [0u8; 20];
        let mut blen = 0;
        let mut v = count as u64;
        if v == 0 {
            buf[blen] = b'0';
            blen += 1;
        } else {
            let mut tmp = [0u8; 20];
            let mut tlen = 0;
            while v > 0 {
                tmp[tlen] = b'0' + (v % 10) as u8;
                v /= 10;
                tlen += 1;
            }
            while tlen > 0 {
                tlen -= 1;
                buf[blen] = tmp[tlen];
                blen += 1;
            }
        }
        out.extend_from_slice(&buf[..blen]);

        reply_blob(&out);
    }
}

// ---------------------------------------------------------------------------
// ping — raw "pong" reply for liveness checks
// ---------------------------------------------------------------------------

#[export_name = "canister_query ping"]
pub extern "C" fn canister_query_ping() {
    unsafe { reply_blob(b"pong"); }
}
