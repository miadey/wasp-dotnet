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

use alloc::vec::Vec;

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
        // No pre-grow this run: see if global 7 stays at the wasm-merge'd
        // initial value of 2,752,512 (= dotnet's __memory_base), or
        // tracks current memory size (= __heap_end).
        mono_embed::__wasm_call_ctors();
    }
    print(b"[wasp-dotnet] canister_init: __wasm_call_ctors done");
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
const DOTNET_MEMORY_BASE: u32 = 2_752_512;

#[inline]
fn dotnet_offset(p: *const u8) -> *const u8 {
    ((p as u32).wrapping_sub(DOTNET_MEMORY_BASE)) as *const u8
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

        let _rc = mono_embed::mono_wasm_add_assembly(name, data, data_size as i32);

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

        let mut name_z: Vec<u8> = Vec::with_capacity(name.len() + 1);
        name_z.extend_from_slice(name);
        name_z.push(0);

        let _rc = mono_embed::mono_wasm_add_assembly(
            name_z.as_ptr(),
            bytes.as_ptr(),
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
