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
static TPA_KEY: &[u8] = b"TRUSTED_PLATFORM_ASSEMBLIES\0";
static APP_PATHS_KEY: &[u8] = b"APP_PATHS\0";
// Colon separator on WASM (G_SEARCHPATH_SEPARATOR_S = ":" on non-Win).
static TPA_VAL: &[u8] = b"/managed/System.Private.CoreLib.dll\0";
static APP_PATHS_VAL: &[u8] = b"/managed\0";
static APP_BASE_VAL: &[u8] = b"/\0";
static RID_KEY: &[u8]      = b"RUNTIME_IDENTIFIER\0";
static RID_VAL: &[u8]      = b"browser-wasm\0";
static INV_KEY: &[u8]      = b"System.Globalization.Invariant\0";
static INV_VAL: &[u8]      = b"true\0";

static TZ_INV_NAME: &[u8]  = b"DOTNET_SYSTEM_TIMEZONE_INVARIANT\0";
static TZ_INV_VAL: &[u8]   = b"true\0";

static MONO_DEBUG_KEY: &[u8] = b"MONO_DEBUG\0";
static MONO_DEBUG_VAL: &[u8] = b"\0"; // empty value avoids the parse-error exit(1) at mini-runtime.c:4279
static MONO_PATH_KEY: &[u8] = b"MONO_PATH\0";
static MONO_PATH_VAL: &[u8] = b"/managed\0";

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
    /// IC instruction counter. counter_type=0 = current message budget
    /// consumed so far (not remaining). Used to chunk long operations
    /// before hitting the 50B-per-update-message cap.
    fn performance_counter(counter_type: u32) -> u64;
}

// ---------------------------------------------------------------------------
// Asyncify integration — pre-merge on dotnet alone with env.maybe_yield
// as the unwind trigger import.
//
// Pipeline (30_merge.sh, runs BEFORE wasm-merge):
//   1. inject_maybe_yield_import.py: add `(import "env" "maybe_yield" ...)`
//      to dotnet so wasm-opt --asyncify recognizes it as the trigger.
//   2. inject_yield_call.py: prepend `call $maybe_yield` to dn_simdhash
//      insert leaf body.
//   3. wasm-opt --asyncify
//        --pass-arg=asyncify-imports@env.maybe_yield
//        --pass-arg=asyncify-onlylist@<chain>
//      Asyncify treats every call to env.maybe_yield as an unwind
//      candidate and inserts saved-points + post-call state checks at
//      each call site. Asyncify creates 5 exports on dotnet:
//      asyncify_start_unwind, _stop_unwind, _start_rewind, _stop_rewind,
//      asyncify_get_state.
//   4. wasm-merge wasp(env) + asyncified-dotnet(dotnet):
//        - dotnet's env.maybe_yield → wasp's exported maybe_yield ✓
//        - wasp's `dotnet.asyncify_*` imports → dotnet's exports ✓
// ---------------------------------------------------------------------------

// Import as "asyncify.<fn>". wasm-opt --asyncify auto-recognizes the
// four control imports (start_unwind/stop_unwind/start_rewind/
// stop_rewind) from the literal module name "asyncify" and replaces
// them with internal calls to the asyncify_* runtime it generates.
// `get_state` is NOT auto-handled; we read state from a placeholder
// export patched post-asyncify by patch_fn_to_call.py.
#[link(wasm_import_module = "asyncify")]
extern "C" {
    #[link_name = "start_unwind"] fn asyncify_start_unwind(data: u32);
    #[link_name = "stop_unwind"]  fn asyncify_stop_unwind();
    #[link_name = "start_rewind"] fn asyncify_start_rewind(data: u32);
    #[link_name = "stop_rewind"]  fn asyncify_stop_rewind();
}

/// Placeholder — patched post-asyncify to call asyncify_get_state.
/// Distinct sentinel write to defeat ICF.
static mut WASP_GETSTATE_SENTINEL: u32 = 0;

#[no_mangle] #[inline(never)]
pub extern "C" fn wasp_asyncify_get_state() -> u32 {
    unsafe { core::ptr::write_volatile(&raw mut WASP_GETSTATE_SENTINEL, 0xC0DE0001); }
    // black_box prevents the optimizer from constant-folding the
    // return value; combined with #[inline(never)] this guarantees
    // the call stays so patch_fn_to_call can rewrite the body to
    // `call $asyncify_get_state` post-asyncify.
    core::hint::black_box(0u32)
}

#[inline(never)]
unsafe fn asyncify_get_state() -> u32 {
    core::hint::black_box(wasp_asyncify_get_state())
}

/// Asyncify save buffer. Layout: u32 cur, u32 end, then 256 KiB stack.
/// Asyncify runs AFTER multi-memory-lowering, so its emitted loads/
/// stores target the merged memory at raw absolute addresses — no
/// mem_base prefixing. We can use a plain wasp static.
#[repr(C, align(8))]
struct AsyncBuf { cur: u32, end: u32, stack: [u8; 256 * 1024] }
static mut ASYNC_BUF: AsyncBuf = AsyncBuf { cur: 0, end: 0, stack: [0; 256 * 1024] };
static mut ASYNC_RESUMING: bool = false;

const ASYNC_BUDGET_LIMIT: u64 = 45_000_000_000;

/// Set to true while inside an entry whose caller doesn't handle the
/// asyncify state==1 unwind protocol (e.g. boot_mono). When true,
/// maybe_yield never unwinds — it just returns. Avoids state==1
/// leaking up to a frame that can't deal with it.
static mut ASYNC_DISABLED: bool = false;

static mut MAYBE_YIELD_CALL_COUNT: u32 = 0;
static mut MAYBE_YIELD_LAST_BUDGET: u64 = 0;
static mut MAYBE_YIELD_UNWIND_COUNT: u32 = 0;

/// Satisfies dotnet's `env.maybe_yield` import (resolved at wasm-merge).
/// Asyncify treats CALLS to this import (via `call $maybe_yield` injected
/// into the dn_simdhash insert leaf) as unwind candidates and inserts
/// the saved-point + post-call state check at each call site. As the
/// trigger import, asyncify does NOT instrument this body — it never
/// participates in the rewind state machine, so calling start_unwind
/// directly here is safe.
#[no_mangle]
pub extern "C" fn maybe_yield() {
    unsafe {
        MAYBE_YIELD_CALL_COUNT = MAYBE_YIELD_CALL_COUNT.wrapping_add(1);
        let st = asyncify_get_state();
        // [my] log silenced — was flooding the log and pushing useful
        // diagnostics off the dfx canister logs tail. Counter is still
        // available via canister_query maybe_yield_count.
        if st == 2 {
            asyncify_stop_rewind();
            return;
        }
        if ASYNC_DISABLED { return; }
        let budget = performance_counter(0);
        MAYBE_YIELD_LAST_BUDGET = budget;
        if budget > ASYNC_BUDGET_LIMIT {
            MAYBE_YIELD_UNWIND_COUNT = MAYBE_YIELD_UNWIND_COUNT.wrapping_add(1);
            let buf_ptr = (&raw mut ASYNC_BUF) as u32;
            (*(&raw mut ASYNC_BUF)).cur = buf_ptr + 8;
            (*(&raw mut ASYNC_BUF)).end = buf_ptr + 8 + (256 * 1024);
            asyncify_start_unwind(buf_ptr);
        }
    }
}

/// Placeholder — patched post-asyncify by patch_fn_to_call.py to
/// `call $bundled_resources_get_assembly_resource`. Same pattern as
/// wasp_asyncify_get_state. Distinct sentinel write defeats LLVM ICF.
static mut PROBE_BUNDLED_SENTINEL: u32 = 0;

#[no_mangle] #[inline(never)]
pub extern "C" fn wasp_probe_bundled_get(name_rel: u32) -> u32 {
    unsafe { core::ptr::write_volatile(&raw mut PROBE_BUNDLED_SENTINEL, 0xB0DE0001); }
    core::hint::black_box(name_rel.wrapping_mul(0))
}

/// Probe: did our register_chunk actually insert corelib in mono's
/// bundled-resources table? Calls the patched wasp_probe_bundled_get
/// (which post-asyncify is a forwarder to bundled_resources_get_assembly_resource)
/// with "System.Private.CoreLib.dll".
#[export_name = "canister_query probe_bundled_get"]
pub extern "C" fn canister_query_probe_bundled_get() {
    unsafe {
        let name: &[u8] = b"System.Private.CoreLib.dll\0";
        let dst = mono_embed::malloc(name.len());
        if dst.is_null() { reply_blob(b"alloc failed"); return; }
        for i in 0..name.len() { *dst.add(i) = name[i]; }
        let mb = wasp_get_mem_base();
        let probe_rel = (dst as u32).wrapping_sub(mb);
        let result = wasp_probe_bundled_get(probe_rel);
        let add1_abs = ADD1_CACHED_NAME as u32;
        let add1_rel = if add1_abs != 0 { add1_abs.wrapping_sub(mb) } else { 0 };
        let mut buf = [0u8; 320];
        let mut i = 0;
        for &c in b"mb=" { buf[i]=c; i+=1; }
        i = format_decimal(&mut buf, i, mb as u64);
        for &c in b" probe_rel=" { buf[i]=c; i+=1; }
        i = format_decimal(&mut buf, i, probe_rel as u64);
        for &c in b" add1_rel=" { buf[i]=c; i+=1; }
        i = format_decimal(&mut buf, i, add1_rel as u64);
        for &c in b" result=" { buf[i]=c; i+=1; }
        i = format_decimal(&mut buf, i, result as u64);
        for (label, abs) in [
            (b" probe_str=\"" as &[u8], dst as u32),
            (b"\" add1_str=\"" as &[u8], add1_abs),
        ] {
            for &c in label { if i<buf.len() { buf[i]=c; i+=1; } }
            if abs != 0 {
                let p = abs as *const u8;
                for k in 0..40u32 {
                    let b = *p.add(k as usize);
                    if b == 0 { break; }
                    if i >= buf.len() - 4 { break; }
                    buf[i] = if (32..127).contains(&b) { b } else { b'.' };
                    i += 1;
                }
            }
        }
        for &c in b"\" add1_bytes_hdr=" { if i<buf.len() { buf[i]=c; i+=1; } }
        let bytes_abs = ADD1_CACHED_BYTES as u32;
        if bytes_abs != 0 {
            let p = bytes_abs as *const u8;
            for k in 0..8u32 {
                let b = *p.add(k as usize);
                let hi = (b >> 4) & 0xF;
                let lo = b & 0xF;
                if i + 3 > buf.len() { break; }
                buf[i] = if hi < 10 { b'0' + hi } else { b'a' + hi - 10 };
                buf[i+1] = if lo < 10 { b'0' + lo } else { b'a' + lo - 10 };
                buf[i+2] = b' ';
                i += 3;
            }
        }
        // result is dotnet-relative (returned by lowered mono code).
        // Read struct at mem_base + result.
        for &c in b"| at_mb+result=" { if i<buf.len() { buf[i]=c; i+=1; } }
        if result != 0 {
            let abs_struct = mb.wrapping_add(result);
            let p = abs_struct as *const u8;
            for k in 0..32u32 {
                let b = *p.add(k as usize);
                let hi = (b >> 4) & 0xF;
                let lo = b & 0xF;
                if i + 3 > buf.len() { break; }
                buf[i] = if hi < 10 { b'0' + hi } else { b'a' + hi - 10 };
                buf[i+1] = if lo < 10 { b'0' + lo } else { b'a' + lo - 10 };
                buf[i+2] = b' ';
                i += 3;
            }
        }
        reply_blob(&buf[..i]);
    }
}

/// Walk the registered corelib PE → CLI → metadata → tables and report
/// the TypeDef row count plus the first few type names. If the bytes
/// mono parses are valid, this should show a row count > 0 and the
/// first row should be `<Module>` (offset 1 = `System.Object`). If the
/// row count is 0 or the strings are garbage, mono is parsing the
/// wrong bytes (addressing convention issue).
#[export_name = "canister_query dump_corlib_meta"]
pub extern "C" fn canister_query_dump_corlib_meta() {
    unsafe {
        let mut buf = [0u8; 1024];
        let mut i = 0usize;
        let bytes_abs = ADD1_CACHED_BYTES as u32;
        if bytes_abs == 0 {
            reply_blob(b"corelib not registered");
            return;
        }
        let base = bytes_abs as *const u8;

        macro_rules! emit { ($s:expr) => {
            for &c in $s.as_bytes() { if i < buf.len() { buf[i] = c; i += 1; } }
        }; }
        macro_rules! emit_b { ($s:expr) => {
            for &c in $s { if i < buf.len() { buf[i] = c; i += 1; } }
        }; }
        macro_rules! emit_dec { ($v:expr) => { i = format_decimal(&mut buf, i, $v as u64); }; }
        macro_rules! emit_hex { ($v:expr) => {
            let v: u32 = $v;
            for s in (0..32u32).step_by(4).rev() {
                let n = ((v >> s) & 0xF) as u8;
                if i < buf.len() {
                    buf[i] = if n < 10 { b'0' + n } else { b'a' + n - 10 };
                    i += 1;
                }
            }
        }; }

        // Helpers to read u16 / u32 little-endian.
        let rd_u32 = |off: u32| -> u32 {
            let p = base.add(off as usize) as *const u32;
            core::ptr::read_unaligned(p)
        };
        let rd_u16 = |off: u32| -> u16 {
            let p = base.add(off as usize) as *const u16;
            core::ptr::read_unaligned(p)
        };

        // 1. DOS header — verify "MZ", read e_lfanew at offset 0x3C.
        let mz = rd_u16(0);
        emit!("mz=0x"); emit_hex!(mz as u32);
        let pe_off = rd_u32(0x3C);
        emit!(" pe_off=0x"); emit_hex!(pe_off);

        // 2. PE signature at pe_off — should be "PE\0\0" (0x00004550).
        let pe_sig = rd_u32(pe_off);
        emit!(" pe_sig=0x"); emit_hex!(pe_sig);
        if pe_sig != 0x4550 {
            emit!(" BAD_PE_SIG"); reply_blob(&buf[..i]); return;
        }

        // 3. COFF header (20 bytes) at pe_off+4. Optional header follows.
        let coff_off = pe_off + 4;
        let opt_hdr_size = rd_u16(coff_off + 16);
        let opt_off = coff_off + 20;
        let magic = rd_u16(opt_off);
        emit!(" opt_magic=0x"); emit_hex!(magic as u32);
        emit!(" opt_size="); emit_dec!(opt_hdr_size);

        // 4. Data directories: PE32 (magic 0x10B) — directories start at
        //    opt_off + 96. PE32+ (magic 0x20B) — opt_off + 112. CLR
        //    runtime header is directory index 14 (offset = base + 14*8).
        let dirs_off = if magic == 0x10B { opt_off + 96 } else { opt_off + 112 };
        let cli_dir_off = dirs_off + 14 * 8;
        let cli_rva = rd_u32(cli_dir_off);
        let cli_size = rd_u32(cli_dir_off + 4);
        emit!(" cli_rva=0x"); emit_hex!(cli_rva);
        emit!(" cli_size="); emit_dec!(cli_size);
        if cli_rva == 0 {
            emit!(" NO_CLI_DIR"); reply_blob(&buf[..i]); return;
        }

        // 5. Resolve CLI RVA to file offset by walking section headers.
        // Number of sections is at coff_off+2.
        let n_sec = rd_u16(coff_off + 2) as u32;
        let sec_table = opt_off + opt_hdr_size as u32;
        let rva_to_off = |rva: u32| -> u32 {
            for s in 0..n_sec {
                let s_off = sec_table + s * 40;
                let s_va = rd_u32(s_off + 12);
                let s_size = rd_u32(s_off + 8);
                let s_rawoff = rd_u32(s_off + 20);
                if rva >= s_va && rva < s_va + s_size {
                    return rva - s_va + s_rawoff;
                }
            }
            0
        };
        let cli_off = rva_to_off(cli_rva);
        emit!(" cli_off=0x"); emit_hex!(cli_off);
        if cli_off == 0 {
            emit!(" CLI_RVA_UNRESOLVED"); reply_blob(&buf[..i]); return;
        }

        // 6. CLI header — Metadata directory at offset 8 in CLI header
        //    (RVA, size).
        let md_rva = rd_u32(cli_off + 8);
        let md_size = rd_u32(cli_off + 12);
        let md_off = rva_to_off(md_rva);
        emit!(" md_off=0x"); emit_hex!(md_off);
        emit!(" md_size="); emit_dec!(md_size);
        if md_off == 0 {
            emit!(" MD_UNRESOLVED"); reply_blob(&buf[..i]); return;
        }

        // 7. Metadata root: signature 0x424A5342 ("BSJB").
        let md_sig = rd_u32(md_off);
        emit!(" md_sig=0x"); emit_hex!(md_sig);
        if md_sig != 0x424A5342 {
            emit!(" BAD_MD_SIG"); reply_blob(&buf[..i]); return;
        }

        // Skip version-string (length at md_off+12, padded to 4).
        let vlen = rd_u32(md_off + 12);
        let pad_vlen = (vlen + 3) & !3u32;
        let post_vstr = md_off + 16 + pad_vlen;
        // Flags(2) + StreamCount(2) at post_vstr.
        let n_streams = rd_u16(post_vstr + 2) as u32;
        emit!(" streams="); emit_dec!(n_streams);

        // 8. Iterate stream headers, find #~ (tables) and #Strings.
        let mut sh_off = post_vstr + 4;
        let mut tables_off: u32 = 0;
        let mut strings_off: u32 = 0;
        let mut strings_size: u32 = 0;
        for _ in 0..n_streams {
            let s_off = rd_u32(sh_off);
            let s_size = rd_u32(sh_off + 4);
            // Read NUL-terminated name (up to 32 bytes, padded to 4).
            let name_start = sh_off + 8;
            let mut nm = [0u8; 16];
            let mut nl = 0;
            for k in 0..16 {
                let b = *base.add((name_start + k as u32) as usize);
                if b == 0 { break; }
                nm[nl] = b; nl += 1;
            }
            let pad_nl = ((nl as u32 + 1 + 3) & !3u32) as usize;
            sh_off = name_start + pad_nl as u32;
            if &nm[..nl] == b"#~" || &nm[..nl] == b"#-" {
                tables_off = md_off + s_off;
            } else if &nm[..nl] == b"#Strings" {
                strings_off = md_off + s_off;
                strings_size = s_size;
            }
        }
        emit!(" tables_off=0x"); emit_hex!(tables_off);
        emit!(" strings_off=0x"); emit_hex!(strings_off);
        emit!(" strings_size="); emit_dec!(strings_size);

        if tables_off == 0 || strings_off == 0 {
            emit!(" MISSING_STREAM"); reply_blob(&buf[..i]); return;
        }

        // 9. Tables stream header: u32 reserved, u8 major, u8 minor,
        //    u8 heap_sizes, u8 reserved, u64 valid_mask, u64 sorted_mask,
        //    then u32 row counts for each set bit in valid_mask.
        let valid_lo = rd_u32(tables_off + 8);
        let valid_hi = rd_u32(tables_off + 12);
        emit!(" valid_lo=0x"); emit_hex!(valid_lo);
        emit!(" valid_hi=0x"); emit_hex!(valid_hi);

        // Count rows for table 0x02 = TypeDef. Iterate set bits 0..table_idx
        // to find offset.
        let mut row_off = tables_off + 24;
        let mut typedef_rows: u32 = 0;
        for tbl in 0..64u32 {
            let mask = if tbl < 32 { (valid_lo >> tbl) & 1 } else { (valid_hi >> (tbl - 32)) & 1 };
            if mask == 0 { continue; }
            let rows = rd_u32(row_off);
            if tbl == 0x02 {
                typedef_rows = rows;
                break;
            }
            row_off += 4;
        }
        emit!(" typedef_rows="); emit_dec!(typedef_rows);

        // 10. Read first 64 bytes of strings heap (skip the leading NUL).
        emit!(" strings_head=\"");
        for k in 1..64u32 {
            if k >= strings_size { break; }
            let b = *base.add((strings_off + k) as usize);
            if b == 0 {
                if i < buf.len() { buf[i] = b'|'; i += 1; }
            } else if (32..127).contains(&b) {
                if i < buf.len() { buf[i] = b; i += 1; }
            } else {
                if i < buf.len() { buf[i] = b'.'; i += 1; }
            }
        }
        emit!("\"");

        reply_blob(&buf[..i]);
    }
}

unsafe fn log_tagged(tag: &[u8], p: u32) {
    let mb = wasp_get_mem_base();
    let mut buf = [0u8; 600];
    let mut i = 0;
    for &c in b"[" { buf[i] = c; i += 1; }
    for &c in tag { buf[i] = c; i += 1; }
    for &c in b"] p=" { buf[i] = c; i += 1; }
    i = format_decimal(&mut buf, i, p as u64);
    for &c in b" raw=\"" { buf[i] = c; i += 1; }
    if p != 0 {
        let abs_raw = p as *const u8;
        for k in 0..64u32 {
            let b = *abs_raw.add(k as usize);
            if b == 0 { break; }
            if i >= buf.len() - 16 { break; }
            buf[i] = if (32..127).contains(&b) { b } else { b'.' };
            i += 1;
        }
    }
    for &c in b"\" mb+p=\"" { buf[i] = c; i += 1; }
    if p != 0 {
        let abs_mb = mb.wrapping_add(p) as *const u8;
        for k in 0..64u32 {
            let b = *abs_mb.add(k as usize);
            if b == 0 { break; }
            if i >= buf.len() - 4 { break; }
            buf[i] = if (32..127).contains(&b) { b } else { b'.' };
            i += 1;
        }
    }
    for &c in b"\"" { if i < buf.len() { buf[i] = c; i += 1; } }
    debug_print(buf.as_ptr() as u32, i as u32);
}

#[no_mangle] pub extern "C" fn wasp_log_request_open(p: u32) { unsafe { log_tagged(b"REQ_OPEN", p); } }
#[no_mangle] pub extern "C" fn wasp_log_bundled_get(p: u32) { unsafe { log_tagged(b"BUND_GET", p); } }
#[no_mangle] pub extern "C" fn wasp_log_name_new(p: u32)    { unsafe { log_tagged(b"NAME_NEW", p); } }

/// 3-arg entry trace for `mono_class_load_from_name(image, name_space,
/// name)`. Image is mb-relative (mono pointer convention). Dumps both
/// the args AND key MonoImage fields (raw_data, name, TypeDef row
/// count, heap_strings pointer) so we can compare mono's view of the
/// image with our wasp-side PE walker.
///
/// MonoImage layout on wasm32 (release/10.0, see metadata-internals.h):
///   8:   char *raw_data
///   12:  u32 raw_data_len
///   20:  char *name
///   56:  char *raw_metadata
///   60:  u32 raw_metadata_len
///   64:  MonoStreamHeader heap_strings { data: ptr@0, size: u32@4 }
///   128: MonoTableInfo tables[]; one entry = 24 bytes;
///        tables[TYPEDEF=2].rows = low 24 bits of word at +180
#[no_mangle]
#[inline(never)]
/// Replacement for `mono_class_from_name_checked(image, ns, name, error)`.
/// Mono's name-cache (dn_simdhash) silently drops inserts in our
/// build, so by-name class lookup always misses. Instead, do direct
/// TypeDef iteration to find the matching (ns, name) pair, then call
/// `mono_class_get_checked` with the resulting metadata token to let
/// mono build the MonoClass struct itself.
///
/// Token format: TypeDef N (1-based row) → 0x02000000 | N.
#[no_mangle]
#[inline(never)]
pub extern "C" fn wasp_class_from_name(image: u32, ns_rel: u32, name_rel: u32, error: u32) -> u32 {
    unsafe {
        let mb = wasp_get_mem_base();
        if image == 0 || ns_rel == 0 || name_rel == 0 { return 0; }
        // Read input ns + name (NUL-terminated, mb-relative).
        let ns_p = mb.wrapping_add(ns_rel) as *const u8;
        let name_p = mb.wrapping_add(name_rel) as *const u8;
        let mut ns_len = 0usize;
        while *ns_p.add(ns_len) != 0 { ns_len += 1; if ns_len > 256 { return 0; } }
        let mut name_len = 0usize;
        while *name_p.add(name_len) != 0 { name_len += 1; if name_len > 256 { return 0; } }
        // Read MonoImage struct fields needed: heap_strings@64
        // (data ptr, mb-relative), tables[TYPEDEF=2] starting at 132+48=180.
        let img = mb.wrapping_add(image) as *const u32;
        let strings_data_rel: u32 = core::ptr::read_unaligned(img.add(16));   // +64
        let td_base_rel: u32 = core::ptr::read_unaligned(
            (mb.wrapping_add(image).wrapping_add(180)) as *const u32);
        let td_rows_word: u32 = core::ptr::read_unaligned(
            (mb.wrapping_add(image).wrapping_add(184)) as *const u32);
        let td_rows = td_rows_word & 0x00FF_FFFF;
        let td_row_size = (td_rows_word >> 24) & 0xFF;
        if td_base_rel == 0 || td_rows == 0 || strings_data_rel == 0 {
            return 0;
        }
        let strings_data_abs = mb.wrapping_add(strings_data_rel) as *const u8;
        // Iterate TypeDef rows, skip row 0 (<Module>). Layout (row_size=18,
        // u32 string indexes): flags@0..4, name@4..8, ns@8..12, ...
        let mut r: u32 = 1;
        let mut found_row: u32 = u32::MAX;
        while r < td_rows {
            let row_p = mb.wrapping_add(td_base_rel)
                .wrapping_add(td_row_size.wrapping_mul(r)) as *const u8;
            let nidx: u32 = core::ptr::read_unaligned(row_p.add(4) as *const u32);
            let nsidx: u32 = core::ptr::read_unaligned(row_p.add(8) as *const u32);
            // Compare name + ns to input.
            let name_s = strings_data_abs.add(nidx as usize);
            let mut ok = true;
            for k in 0..name_len {
                if *name_s.add(k) != *name_p.add(k) { ok = false; break; }
            }
            if ok && *name_s.add(name_len) == 0 {
                let ns_s = strings_data_abs.add(nsidx as usize);
                let mut ok2 = true;
                for k in 0..ns_len {
                    if *ns_s.add(k) != *ns_p.add(k) { ok2 = false; break; }
                }
                if ok2 && *ns_s.add(ns_len) == 0 {
                    found_row = r;
                    break;
                }
            }
            r += 1;
        }
        // Log the lookup.
        let mut buf = [0u8; 200];
        let mut i = 0;
        for &c in b"[wasp_cls] " { if i < buf.len() { buf[i] = c; i += 1; } }
        for k in 0..ns_len { if i < buf.len() { buf[i] = *ns_p.add(k); i += 1; } }
        if i < buf.len() { buf[i] = b'.'; i += 1; }
        for k in 0..name_len { if i < buf.len() { buf[i] = *name_p.add(k); i += 1; } }
        for &c in b" -> " { if i < buf.len() { buf[i] = c; i += 1; } }
        if found_row == u32::MAX {
            for &c in b"NOT_FOUND" { if i < buf.len() { buf[i] = c; i += 1; } }
            debug_print(buf.as_ptr() as u32, i as u32);
            return 0;
        }
        let token: u32 = 0x02000000 | (found_row + 1);
        for &c in b"row=" { if i < buf.len() { buf[i] = c; i += 1; } }
        i = format_decimal(&mut buf, i, found_row as u64);
        for &c in b" tok=0x" { if i < buf.len() { buf[i] = c; i += 1; } }
        for s in (0..32u32).step_by(4).rev() {
            let n = ((token >> s) & 0xF) as u8;
            if i < buf.len() {
                buf[i] = if n < 10 { b'0' + n } else { b'a' + n - 10 };
                i += 1;
            }
        }
        debug_print(buf.as_ptr() as u32, i as u32);
        // Hand off to mono to actually construct the MonoClass.
        mono_embed::mono_class_get_checked(image, token, error)
    }
}

pub extern "C" fn wasp_log_class_load(image: u32, ns_rel: u32, name_rel: u32) {
    unsafe {
        let mb = wasp_get_mem_base();
        let mut buf = [0u8; 600];
        let mut i = 0;
        for &c in b"[CLASS_LOAD] image=" { buf[i] = c; i += 1; }
        i = format_decimal(&mut buf, i, image as u64);
        for &c in b" ns=\"" { buf[i] = c; i += 1; }
        if ns_rel != 0 {
            let p = mb.wrapping_add(ns_rel) as *const u8;
            let mut k = 0;
            while k < 128 {
                let b = *p.add(k); if b == 0 { break; }
                if i >= buf.len() - 4 { break; }
                buf[i] = if (32..127).contains(&b) { b } else { b'.' };
                i += 1; k += 1;
            }
        }
        for &c in b"\" name=\"" { buf[i] = c; i += 1; }
        if name_rel != 0 {
            let p = mb.wrapping_add(name_rel) as *const u8;
            let mut k = 0;
            while k < 128 {
                let b = *p.add(k); if b == 0 { break; }
                if i >= buf.len() - 4 { break; }
                buf[i] = if (32..127).contains(&b) { b } else { b'.' };
                i += 1; k += 1;
            }
        }
        for &c in b"\"" { buf[i] = c; i += 1; }

        // Dump the MonoImage struct fields. `image` is mb-relative.
        if image != 0 {
            let img = mb.wrapping_add(image) as *const u32;
            let raw_data_rel = core::ptr::read_unaligned(img.add(2));        // +8
            let raw_data_len = core::ptr::read_unaligned(img.add(3));        // +12
            let name_rel_img = core::ptr::read_unaligned(img.add(5));        // +20
            let raw_meta_rel = core::ptr::read_unaligned(img.add(14));       // +56
            let strings_data_rel = core::ptr::read_unaligned(img.add(16));   // +64
            let strings_size = core::ptr::read_unaligned(img.add(17));       // +68
            let typedef_word: u32 = core::ptr::read_unaligned(
                (mb.wrapping_add(image).wrapping_add(180)) as *const u32);
            let typedef_rows = typedef_word & 0x00FF_FFFF;

            for &c in b" | img.raw_data_rel=" { if i < buf.len() { buf[i] = c; i += 1; } }
            i = format_decimal(&mut buf, i, raw_data_rel as u64);
            for &c in b" len=" { if i < buf.len() { buf[i] = c; i += 1; } }
            i = format_decimal(&mut buf, i, raw_data_len as u64);
            for &c in b" name_rel=" { if i < buf.len() { buf[i] = c; i += 1; } }
            i = format_decimal(&mut buf, i, name_rel_img as u64);
            // Read first 32 bytes of the name string at mb+name_rel_img
            for &c in b" name=\"" { if i < buf.len() { buf[i] = c; i += 1; } }
            if name_rel_img != 0 {
                let np = mb.wrapping_add(name_rel_img) as *const u8;
                for k in 0..40usize {
                    let b = *np.add(k); if b == 0 { break; }
                    if i >= buf.len() - 4 { break; }
                    buf[i] = if (32..127).contains(&b) { b } else { b'.' };
                    i += 1;
                }
            }
            for &c in b"\" raw_meta_rel=" { if i < buf.len() { buf[i] = c; i += 1; } }
            i = format_decimal(&mut buf, i, raw_meta_rel as u64);
            for &c in b" strings_rel=" { if i < buf.len() { buf[i] = c; i += 1; } }
            i = format_decimal(&mut buf, i, strings_data_rel as u64);
            for &c in b" strings_size=" { if i < buf.len() { buf[i] = c; i += 1; } }
            i = format_decimal(&mut buf, i, strings_size as u64);
            for &c in b" typedef_rows=" { if i < buf.len() { buf[i] = c; i += 1; } }
            i = format_decimal(&mut buf, i, typedef_rows as u64);
            // tables[] is at offset 132 (was 128 before u64 alignment
            // padding shift). tables[TYPEDEF=2] starts at 132+2*24=180.
            // Within MonoTableInfo: base@0 (ptr), rows_word@4 where low
            // 24 bits = rows, upper 8 = row_size.
            let img_base = mb.wrapping_add(image) as *const u8;
            let td_base_rel: u32 = core::ptr::read_unaligned(img_base.add(180) as *const u32);
            let td_rows_word: u32 = core::ptr::read_unaligned(img_base.add(184) as *const u32);
            let td_rows = td_rows_word & 0x00FF_FFFF;
            let td_row_size = (td_rows_word >> 24) & 0xFF;
            for &c in b" td.base_rel=" { if i < buf.len() { buf[i] = c; i += 1; } }
            i = format_decimal(&mut buf, i, td_base_rel as u64);
            for &c in b" td.rows=" { if i < buf.len() { buf[i] = c; i += 1; } }
            i = format_decimal(&mut buf, i, td_rows as u64);
            for &c in b" td.row_size=" { if i < buf.len() { buf[i] = c; i += 1; } }
            i = format_decimal(&mut buf, i, td_row_size as u64);

            // Read TypeDef row 1 (TypeDef[0] = <Module>, [1] = first
            // real type, expected System.Object). Layout when no large
            // heaps: u32 flags, u16 name_idx, u16 ns_idx, u16 extends,
            // u16 fields, u16 methods. With heap_sizes hint bits set,
            // indexes can be u32.
            if td_base_rel != 0 && td_rows >= 2 {
                // row_size 18 => u32 strings heap indexes:
                //   flags @ 0..4, name @ 4..8, ns @ 8..12,
                //   extends @ 12..14, fields @ 14..16, methods @ 16..18.
                let row_abs = mb.wrapping_add(td_base_rel).wrapping_add(td_row_size) as *const u8;
                let flags: u32 = core::ptr::read_unaligned(row_abs as *const u32);
                let name_idx: u32 = core::ptr::read_unaligned(row_abs.add(4) as *const u32);
                let ns_idx: u32 = core::ptr::read_unaligned(row_abs.add(8) as *const u32);
                for &c in b" td[1].flags=" { if i < buf.len() { buf[i] = c; i += 1; } }
                i = format_decimal(&mut buf, i, flags as u64);
                for &c in b" name_idx=" { if i < buf.len() { buf[i] = c; i += 1; } }
                i = format_decimal(&mut buf, i, name_idx as u64);
                for &c in b" ns_idx=" { if i < buf.len() { buf[i] = c; i += 1; } }
                i = format_decimal(&mut buf, i, ns_idx as u64);

                let strings_data_abs = mb.wrapping_add(strings_data_rel) as *const u8;
                for &c in b" name=\"" { if i < buf.len() { buf[i] = c; i += 1; } }
                let np = strings_data_abs.add(name_idx as usize);
                for k in 0..40 {
                    let b = *np.add(k); if b == 0 { break; }
                    if i >= buf.len() - 4 { break; }
                    buf[i] = if (32..127).contains(&b) { b } else { b'.' };
                    i += 1;
                }
                for &c in b"\" ns=\"" { if i < buf.len() { buf[i] = c; i += 1; } }
                let np2 = strings_data_abs.add(ns_idx as usize);
                for k in 0..40 {
                    let b = *np2.add(k); if b == 0 { break; }
                    if i >= buf.len() - 4 { break; }
                    buf[i] = if (32..127).contains(&b) { b } else { b'.' };
                    i += 1;
                }
                for &c in b"\"" { if i < buf.len() { buf[i] = c; i += 1; } }
            }

            // Scan strings heap for ALL "Object\0" occurrences (CIL
            // strings heap supports suffix-sharing, so a type name's
            // index may point INSIDE a longer string).
            for &c in b" Object_offsets=[" { if i < buf.len() { buf[i] = c; i += 1; } }
            let strings_data_abs = mb.wrapping_add(strings_data_rel) as *const u8;
            let needle = b"Object";
            let mut object_offsets = [0u32; 16];
            let mut n_offsets = 0usize;
            let mut off = 0usize;
            let max = strings_size as usize;
            while off + needle.len() < max && n_offsets < 16 {
                let mut matched = true;
                for k in 0..needle.len() {
                    if *strings_data_abs.add(off + k) != needle[k] { matched = false; break; }
                }
                if matched && *strings_data_abs.add(off + needle.len()) == 0 {
                    object_offsets[n_offsets] = off as u32;
                    if n_offsets > 0 && i < buf.len() { buf[i] = b','; i += 1; }
                    i = format_decimal(&mut buf, i, off as u64);
                    n_offsets += 1;
                }
                off += 1;
            }
            for &c in b"]" { if i < buf.len() { buf[i] = c; i += 1; } }

            // Now find any TypeDef row whose name_idx matches ANY of
            // those Object offsets.
            for &c in b" obj_match=" { if i < buf.len() { buf[i] = c; i += 1; } }
            let mut found_row = u32::MAX;
            let mut found_idx = u32::MAX;
            if td_base_rel != 0 && n_offsets > 0 {
                let mut r = 0u32;
                'outer: while r < td_rows {
                    let row_p = mb.wrapping_add(td_base_rel)
                        .wrapping_add(td_row_size.wrapping_mul(r)) as *const u8;
                    let nidx: u32 = core::ptr::read_unaligned(row_p.add(4) as *const u32);
                    let mut k = 0;
                    while k < n_offsets {
                        if nidx == object_offsets[k] {
                            found_row = r;
                            found_idx = nidx;
                            break 'outer;
                        }
                        k += 1;
                    }
                    r += 1;
                }
            }
            if found_row == u32::MAX {
                for &c in b"NONE" { if i < buf.len() { buf[i] = c; i += 1; } }
            } else {
                i = format_decimal(&mut buf, i, found_row as u64);
                for &c in b"@idx=" { if i < buf.len() { buf[i] = c; i += 1; } }
                i = format_decimal(&mut buf, i, found_idx as u64);
            }

            // (Object_rows[] block removed — superseded by obj_match
            // logic above which scans against ALL Object offsets.)

            // Also: scan strings heap for "System\0" — the namespace.
            for &c in b" System_at=" { if i < buf.len() { buf[i] = c; i += 1; } }
            let sys_needle = b"System";
            let mut sys_found = u32::MAX;
            let mut soff = 0usize;
            while soff + sys_needle.len() < strings_size as usize {
                let mut m = true;
                for k in 0..sys_needle.len() {
                    if *strings_data_abs.add(soff + k) != sys_needle[k] { m = false; break; }
                }
                if m && *strings_data_abs.add(soff + sys_needle.len()) == 0 {
                    sys_found = soff as u32;
                    break;
                }
                soff += 1;
            }
            if sys_found == u32::MAX {
                for &c in b"NONE" { if i < buf.len() { buf[i] = c; i += 1; } }
            } else {
                i = format_decimal(&mut buf, i, sys_found as u64);
            }
            // First few bytes of strings heap to verify it looks real
            for &c in b" strings_head=\"" { if i < buf.len() { buf[i] = c; i += 1; } }
            if strings_data_rel != 0 {
                let sp = mb.wrapping_add(strings_data_rel) as *const u8;
                for k in 1..48usize {
                    let b = *sp.add(k);
                    if i >= buf.len() - 4 { break; }
                    if b == 0 {
                        buf[i] = b'|';
                    } else if (32..127).contains(&b) {
                        buf[i] = b;
                    } else {
                        buf[i] = b'.';
                    }
                    i += 1;
                }
            }
            for &c in b"\"" { if i < buf.len() { buf[i] = c; i += 1; } }
        }

        debug_print(buf.as_ptr() as u32, i as u32);
    }
}

/// Replacement for monoeg_g_strdup_printf. Mono's load_corlib path
/// uses this with a single `%s` (e.g. "%s.dll") to build the corlib
/// filename. Our environment's printf returns zero-filled buffers
/// (root cause unclear — likely wasi-stub'd write/syscall path),
/// breaking the filename and causing mono_assembly_request_open to
/// receive an empty string. This replacement handles the common
/// "<prefix>%s<suffix>" format directly.
///
/// Args (mono ABI):
///   fmt_rel  — dotnet-relative (mem_base + fmt_rel = absolute) C string
///   va_rel   — dotnet-relative pointer to the va_args buffer; the
///              first 4 bytes hold the FIRST var-arg (a string ptr,
///              also dotnet-relative).
/// Returns the dotnet-relative offset of a malloc'd, NUL-terminated
/// formatted string.
#[no_mangle]
pub extern "C" fn wasp_g_strdup_printf(fmt_rel: u32, va_rel: u32) -> u32 {
    unsafe {
        let mb = wasp_get_mem_base();
        let fmt = mb.wrapping_add(fmt_rel) as *const u8;
        let mut flen = 0usize;
        while *fmt.add(flen) != 0 { flen += 1; if flen > 4096 { break; } }
        let va_base = mb.wrapping_add(va_rel) as *const u32;
        // Pass 1: compute output length.
        let mut out_len = 0usize;
        let mut va_idx = 0usize;
        let mut i = 0usize;
        while i < flen {
            if *fmt.add(i) != b'%' || i + 1 >= flen {
                out_len += 1; i += 1; continue;
            }
            // Skip flags / width / precision / length (very loose).
            let mut j = i + 1;
            while j < flen {
                let c = *fmt.add(j);
                if c == b'%' || c == b's' || c == b'd' || c == b'u' || c == b'x'
                   || c == b'X' || c == b'p' || c == b'c' || c == b'i' { break; }
                j += 1;
            }
            if j >= flen { out_len += j - i; i = j; continue; }
            let conv = *fmt.add(j);
            match conv {
                b'%' => { out_len += 1; }
                b's' => {
                    let arg_rel = *va_base.add(va_idx); va_idx += 1;
                    if arg_rel != 0 {
                        let p = mb.wrapping_add(arg_rel) as *const u8;
                        let mut k = 0; while *p.add(k) != 0 { k += 1; if k > 4096 { break; } }
                        out_len += k;
                    } else { out_len += 6; }
                }
                b'd' | b'i' | b'u' | b'x' | b'X' | b'p' => { out_len += 11; va_idx += 1; }
                b'c' => { out_len += 1; va_idx += 1; }
                _ => { out_len += j - i + 1; }
            }
            i = j + 1;
        }
        let buf = mono_embed::malloc(out_len + 1) as *mut u8;
        if buf.is_null() { return 0; }
        // Pass 2: write.
        va_idx = 0; i = 0; let mut o = 0usize;
        while i < flen {
            if *fmt.add(i) != b'%' || i + 1 >= flen {
                *buf.add(o) = *fmt.add(i); o += 1; i += 1; continue;
            }
            let mut j = i + 1;
            while j < flen {
                let c = *fmt.add(j);
                if c == b'%' || c == b's' || c == b'd' || c == b'u' || c == b'x'
                   || c == b'X' || c == b'p' || c == b'c' || c == b'i' { break; }
                j += 1;
            }
            if j >= flen {
                let mut k = i;
                while k < j { *buf.add(o) = *fmt.add(k); o += 1; k += 1; }
                i = j; continue;
            }
            let conv = *fmt.add(j);
            match conv {
                b'%' => { *buf.add(o) = b'%'; o += 1; }
                b's' => {
                    let arg_rel = *va_base.add(va_idx); va_idx += 1;
                    if arg_rel != 0 {
                        let p = mb.wrapping_add(arg_rel) as *const u8;
                        let mut k = 0;
                        while *p.add(k) != 0 {
                            *buf.add(o) = *p.add(k); o += 1; k += 1;
                            if k > 4096 { break; }
                        }
                    } else { for &c in b"(null)" { *buf.add(o) = c; o += 1; } }
                }
                b'd' | b'i' => {
                    let v = *va_base.add(va_idx) as i32; va_idx += 1;
                    let mut tmp = [0u8; 16]; let mut ti = 0;
                    let neg = v < 0;
                    let mut n = if neg { (-(v as i64)) as u64 } else { v as u64 };
                    if n == 0 { tmp[0] = b'0'; ti = 1; }
                    while n > 0 { tmp[ti] = b'0' + (n % 10) as u8; n /= 10; ti += 1; }
                    if neg { *buf.add(o) = b'-'; o += 1; }
                    while ti > 0 { ti -= 1; *buf.add(o) = tmp[ti]; o += 1; }
                }
                b'u' => {
                    let mut n = *va_base.add(va_idx) as u64; va_idx += 1;
                    let mut tmp = [0u8; 16]; let mut ti = 0;
                    if n == 0 { tmp[0] = b'0'; ti = 1; }
                    while n > 0 { tmp[ti] = b'0' + (n % 10) as u8; n /= 10; ti += 1; }
                    while ti > 0 { ti -= 1; *buf.add(o) = tmp[ti]; o += 1; }
                }
                b'x' | b'X' | b'p' => {
                    let mut n = *va_base.add(va_idx) as u64; va_idx += 1;
                    let mut tmp = [0u8; 16]; let mut ti = 0;
                    if n == 0 { tmp[0] = b'0'; ti = 1; }
                    while n > 0 {
                        let nib = (n & 0xF) as u8;
                        tmp[ti] = if nib < 10 { b'0' + nib }
                                   else if conv == b'X' { b'A' + nib - 10 }
                                   else { b'a' + nib - 10 };
                        n >>= 4; ti += 1;
                    }
                    while ti > 0 { ti -= 1; *buf.add(o) = tmp[ti]; o += 1; }
                }
                b'c' => { *buf.add(o) = (*va_base.add(va_idx) & 0xFF) as u8; va_idx += 1; o += 1; }
                _ => {
                    let mut k = i;
                    while k <= j { *buf.add(o) = *fmt.add(k); o += 1; k += 1; }
                }
            }
            i = j + 1;
        }
        *buf.add(o) = 0;
        // One-line log.
        let mut log = [0u8; 320]; let mut li = 0;
        for &c in b"[printf] fmt=\"" { log[li] = c; li += 1; }
        let mut k2 = 0;
        while k2 < flen && li < 100 {
            let b = *fmt.add(k2);
            log[li] = if (32..127).contains(&b) { b } else { b'.' };
            li += 1; k2 += 1;
        }
        for &c in b"\" out=\"" { log[li] = c; li += 1; }
        let mut k3 = 0;
        while k3 < o && li < 280 {
            let b = *buf.add(k3);
            log[li] = if (32..127).contains(&b) { b } else { b'.' };
            li += 1; k3 += 1;
        }
        for &c in b"\"" { log[li] = c; li += 1; }
        debug_print(log.as_ptr() as u32, li as u32);
        (buf as u32).wrapping_sub(mb)
    }
}

/// Generic string-pointer logger. Logs the first 32 bytes at both
/// addressing conventions: `p` raw (absolute) and `mb+p` (mem_base
/// relative). One of the two must hold the actual C string mono
/// passed; comparing both pins down which convention this fn uses.
#[no_mangle]
pub extern "C" fn wasp_log_str_ptr(p: u32) {
    unsafe {
        let mb = wasp_get_mem_base();
        let mut buf = [0u8; 600];
        let mut i = 0;
        for &c in b"[trace] p=" { buf[i] = c; i += 1; }
        i = format_decimal(&mut buf, i, p as u64);
        // raw[p] view
        for &c in b" raw_ascii=\"" { buf[i] = c; i += 1; }
        if p != 0 {
            let abs_raw = p as *const u8;
            for k in 0..64u32 {
                let b = *abs_raw.add(k as usize);
                if b == 0 { break; }
                if i >= buf.len() - 16 { break; }
                buf[i] = if (32..127).contains(&b) { b } else { b'.' };
                i += 1;
            }
        }
        for &c in b"\" mb+p_ascii=\"" { buf[i] = c; i += 1; }
        if p != 0 {
            let abs_mb = mb.wrapping_add(p) as *const u8;
            for k in 0..64u32 {
                let b = *abs_mb.add(k as usize);
                if b == 0 { break; }
                if i >= buf.len() - 4 { break; }
                buf[i] = if (32..127).contains(&b) { b } else { b'.' };
                i += 1;
            }
        }
        for &c in b"\"" { if i < buf.len() { buf[i] = c; i += 1; } }
        debug_print(buf.as_ptr() as u32, i as u32);
    }
}

/// Hooked replacement for `monoeg_g_print(fmt, args)`. We don't bother
/// implementing real printf — just capture the format string + log it
/// to ic0.debug_print so we can see what mono is trying to print
/// (typically the "couldn't load corlib" message right before exit).
#[no_mangle]
pub extern "C" fn wasp_log_g_print(fmt: u32, args: u32) {
    unsafe {
        let mb = wasp_get_mem_base();
        let mut buf = [0u8; 600];
        let mut i = 0;
        for &c in b"[g_print] mb=" { buf[i] = c; i += 1; }
        i = format_decimal(&mut buf, i, mb as u64);
        for &c in b" fmt=" { buf[i] = c; i += 1; }
        i = format_decimal(&mut buf, i, fmt as u64);
        for &c in b" args=" { buf[i] = c; i += 1; }
        i = format_decimal(&mut buf, i, args as u64);
        for &c in b" | mb+fmt: \"" { buf[i] = c; i += 1; }
        if fmt != 0 {
            let p = mb.wrapping_add(fmt) as *const u8;
            for k in 0..256u32 {
                let b = *p.add(k as usize);
                if b == 0 { break; }
                if i >= buf.len() - 4 { break; }
                buf[i] = if (32..127).contains(&b) { b } else { b'.' };
                i += 1;
            }
        }
        for &c in b"\" raw_fmt: \"" { if i < buf.len() { buf[i] = c; i += 1; } }
        if fmt != 0 {
            let p = fmt as *const u8;
            for k in 0..96u32 {
                let b = *p.add(k as usize);
                if b == 0 { break; }
                if i >= buf.len() - 4 { break; }
                buf[i] = if (32..127).contains(&b) { b } else { b'.' };
                i += 1;
            }
        }
        for &c in b"\"" { if i < buf.len() { buf[i] = c; i += 1; } }
        debug_print(buf.as_ptr() as u32, i as u32);
    }
}

/// Read up to 256 bytes of dotnet static memory at offsets 127870 and
/// 128954 — the two candidate format-string addresses found statically
/// in mono_assembly_load_corlib's monoeg_g_print call before exit(1).
/// Also includes ABS-form variants (in case mono passes the literal
/// memory address rather than a memory-base-relative offset).
#[export_name = "canister_query peek_corlib_msg"]
pub extern "C" fn canister_query_peek_corlib_msg() {
    unsafe {
        let mb = wasp_get_mem_base();
        let mut buf = [0u8; 1200];
        let mut i = 0;
        for &c in b"mb=" { buf[i] = c; i += 1; }
        i = format_decimal(&mut buf, i, mb as u64);
        for &c in b" | " { buf[i] = c; i += 1; }
        for (label, addr) in [
            (b"mb+127870" as &[u8], mb.wrapping_add(127870)),
            (b"127870",     127870u32),
            (b"mb+128954",  mb.wrapping_add(128954)),
            (b"128954",     128954u32),
        ] {
            for &c in label { if i<buf.len() { buf[i]=c; i+=1; } }
            for &c in b"=\"" { if i<buf.len() { buf[i]=c; i+=1; } }
            let p = addr as *const u8;
            for k in 0..96u32 {
                let b = *p.add(k as usize);
                if b == 0 { break; }
                if i >= buf.len() - 4 { break; }
                buf[i] = if (32..127).contains(&b) { b } else { b'.' };
                i += 1;
            }
            for &c in b"\" " { if i<buf.len() { buf[i]=c; i+=1; } }
        }
        reply_blob(&buf[..i]);
    }
}

/// Diagnostic — read MAYBE_YIELD_CALL_COUNT to confirm the wat-injected
/// `call $maybe_yield` in the dn_simdhash leaf is actually firing.
#[export_name = "canister_query maybe_yield_count"]
pub extern "C" fn canister_query_maybe_yield_count() {
    unsafe {
        let mut buf = [0u8; 128];
        let mut i = 0;
        for &c in b"calls=" { buf[i] = c; i += 1; }
        i = format_decimal(&mut buf, i, MAYBE_YIELD_CALL_COUNT as u64);
        for &c in b" unwinds=" { buf[i] = c; i += 1; }
        i = format_decimal(&mut buf, i, MAYBE_YIELD_UNWIND_COUNT as u64);
        for &c in b" last_budget=" { buf[i] = c; i += 1; }
        i = format_decimal(&mut buf, i, MAYBE_YIELD_LAST_BUDGET);
        for &c in b" reg_idx=" { buf[i] = c; i += 1; }
        i = format_decimal(&mut buf, i, BUILTIN_REG_IDX as u64);
        reply_blob(&buf[..i]);
    }
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
pub(crate) fn format_decimal(buf: &mut [u8], mut i: usize, mut v: u64) -> usize {
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
    print(b"[wasp-dotnet] canister_init: pre-grow heap by 256MiB");
    // Pre-grow BEFORE ctors so multi-memory-lowering's mem_base
    // stabilizes early. Mono later wouldn't need to grow during
    // class-load (the dn_simdhash bucket pointers stored before a
    // grow would otherwise be stale relative to the post-grow
    // mem_base, causing heap-out-of-bounds reads). 4096 pages = 256 MiB.
    let _ = core::arch::wasm32::memory_grow(0, 4096);
    print(b"[wasp-dotnet] canister_init: pre-ctors");
    unsafe {
        mono_embed::__wasm_call_ctors();
    }
    print(b"[wasp-dotnet] canister_init: __wasm_call_ctors done");
    // Pre-register corelib (BUILTIN_BCL[0]) here. The 1T canister_init
    // budget covers it comfortably; per-update 50B does not. Advance
    // BUILTIN_REG_IDX so register_chunk picks up at index 1 for the
    // remaining 33 BCLs (each small enough to fit one-per-message).
    unsafe {
        let (n, b) = BUILTIN_BCL[0];
        print(b"[wasp-dotnet] canister_init: pre-register corelib");
        add1(n, b);
        BUILTIN_REG_IDX = 1;
        print(b"[wasp-dotnet] canister_init: corelib registered");
    }
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

// Shadow map for dn_simdhash. Keyed on (simdhash_struct_ptr, key_ptr)
// so multiple distinct simdhash tables (bundled_resources, env vars,
// internal Mono caches, ...) can coexist transparently.
//
// We use the raw `key_ptr` value rather than the dereferenced string
// content because:
//   1. Some dn_simdhash tables are gpointer-keyed (key is not a string).
//   2. Even for str_ptr tables, mono usually passes the SAME pointer
//      back on get() that it passed on insert() (e.g. via mono_image_strdup
//      which keeps a stable copy in mono's heap).
//   3. Keying on dereferenced bytes was causing false matches when two
//      distinct gpointer keys both happened to deref to empty/zero memory.
struct SimdMap(UnsafeCell<Option<BTreeMap<(u32, u32), u32>>>);
unsafe impl Sync for SimdMap {}
static SIMD_MAP: SimdMap = SimdMap(UnsafeCell::new(None));

unsafe fn simd_map_mut() -> &'static mut BTreeMap<(u32, u32), u32> {
    let slot = &mut *SIMD_MAP.0.get();
    if slot.is_none() {
        *slot = Some(BTreeMap::new());
    }
    slot.as_mut().unwrap_unchecked()
}

// String-content fallback. For str_ptr tables, mono may pass a NEW
// pointer on get() that has the same string content as the one used
// on insert(). Storing (table_ptr, string_bytes) → value lets us still
// hit on content even when the pointer differs.
struct SimdMapByStr(UnsafeCell<Option<BTreeMap<(u32, Vec<u8>), u32>>>);
unsafe impl Sync for SimdMapByStr {}
static SIMD_MAP_BY_STR: SimdMapByStr = SimdMapByStr(UnsafeCell::new(None));

unsafe fn simd_map_by_str_mut() -> &'static mut BTreeMap<(u32, Vec<u8>), u32> {
    let slot = &mut *SIMD_MAP_BY_STR.0.get();
    if slot.is_none() {
        *slot = Some(BTreeMap::new());
    }
    slot.as_mut().unwrap_unchecked()
}

// Legacy assembly map kept for back-compat with previous patches; the
// universal simdhash bypass below makes wasp_add_assembly / wasp_get_assembly
// no longer strictly necessary.
struct AsmMap(UnsafeCell<Option<BTreeMap<Vec<u8>, u32>>>);
unsafe impl Sync for AsmMap {}
static ASM_MAP: AsmMap = AsmMap(UnsafeCell::new(None));

/// Shadow registry for the bundled-resources lookup. Keyed by the
/// bare assembly name (no NUL); value is the mb-relative pointer to a
/// `MonoBundledAssemblyResource` struct allocated in mono's heap.
/// Populated from `add1`; consulted by `wasp_bundled_resource_get`
/// which replaces `bundled_resources_get_assembly_resource` post-merge
/// (mono's dn_simdhash bundled-resources table is unreliable in our
/// build, so we maintain a parallel store).
struct WaspResources(UnsafeCell<Option<BTreeMap<Vec<u8>, u32>>>);
unsafe impl Sync for WaspResources {}
static WASP_RESOURCES: WaspResources = WaspResources(UnsafeCell::new(None));

unsafe fn wasp_resources_mut() -> &'static mut BTreeMap<Vec<u8>, u32> {
    let slot = &mut *WASP_RESOURCES.0.get();
    if slot.is_none() { *slot = Some(BTreeMap::new()); }
    slot.as_mut().unwrap_unchecked()
}

/// Replacement body for mono's `bundled_resources_get_assembly_resource`.
/// `id_rel` is mb-relative (mono's standard pointer convention after
/// multi-memory-lowering). Reads the C string at `mb + id_rel`,
/// normalises (strip `.dll`/`.webcil`/`.wasm` then re-add `.dll`),
/// looks up in `WASP_RESOURCES`, and returns the resource struct's
/// mb-relative pointer (or 0 on miss).
#[no_mangle]
#[inline(never)]
pub extern "C" fn wasp_bundled_resource_get(id_rel: u32) -> u32 {
    unsafe {
        let mb = wasp_get_mem_base();
        let id_abs = mb.wrapping_add(id_rel) as *const u8;
        let mut name = Vec::new();
        let mut i = 0usize;
        loop {
            let b = *id_abs.add(i);
            if b == 0 { break; }
            name.push(b);
            i += 1;
            if i > 256 { break; }
        }
        // key_from_id semantics: strip known extensions, re-add .dll
        for ext in &[b".dll" as &[u8], b".webcil", b".wasm"] {
            if name.ends_with(ext) {
                name.truncate(name.len() - ext.len());
                break;
            }
        }
        name.extend_from_slice(b".dll");
        let result = wasp_resources_mut().get(&name).copied().unwrap_or(0);
        let mut buf = [0u8; 320];
        let mut k = 0;
        for &c in b"[wasp_get] " { buf[k] = c; k += 1; }
        let nm = if name.len() < 60 { name.len() } else { 60 };
        let mut j = 0;
        while j < nm { buf[k] = name[j]; k += 1; j += 1; }
        for &c in b" -> " { buf[k] = c; k += 1; }
        if result == 0 {
            for &c in b"NULL" { buf[k] = c; k += 1; }
        } else {
            for &c in b"OK rel=" { buf[k] = c; k += 1; }
            k = format_decimal(&mut buf, k, result as u64);
            // Dump struct fields and first 8 bytes at data ptr to
            // verify mono will see valid PE bytes.
            let s = mb.wrapping_add(result) as *const u32;
            let type_v = *s.add(0);
            let name_v = *s.add(4);   // assembly.name @ offset 16
            let data_v = *s.add(5);   // assembly.data @ offset 20
            let size_v = *s.add(6);   // assembly.size @ offset 24
            for &c in b" type=" { buf[k] = c; k += 1; }
            k = format_decimal(&mut buf, k, type_v as u64);
            for &c in b" size=" { buf[k] = c; k += 1; }
            k = format_decimal(&mut buf, k, size_v as u64);
            for &c in b" data_rel=" { buf[k] = c; k += 1; }
            k = format_decimal(&mut buf, k, data_v as u64);
            for &c in b" hdr=" { buf[k] = c; k += 1; }
            let dp = mb.wrapping_add(data_v) as *const u8;
            for q in 0..8usize {
                let b = *dp.add(q);
                let hi = (b >> 4) & 0xF;
                let lo = b & 0xF;
                buf[k] = if hi < 10 { b'0' + hi } else { b'a' + hi - 10 }; k += 1;
                buf[k] = if lo < 10 { b'0' + lo } else { b'a' + lo - 10 }; k += 1;
                buf[k] = b' '; k += 1;
            }
            // Use name_v in a side log too (if non-zero).
            for &c in b"name@" { buf[k] = c; k += 1; }
            k = format_decimal(&mut buf, k, name_v as u64);
        }
        debug_print(buf.as_ptr() as u32, k as u32);
        result
    }
}

unsafe fn asm_map_mut() -> &'static mut BTreeMap<Vec<u8>, u32> {
    let slot = &mut *ASM_MAP.0.get();
    if slot.is_none() {
        *slot = Some(BTreeMap::new());
    }
    slot.as_mut().unwrap_unchecked()
}

/// Read a NUL-terminated string starting at dotnet-relative `rel_ptr`.
unsafe fn read_cstr_rel(rel_ptr: u32) -> Vec<u8> {
    let abs = rel_ptr.wrapping_add(wasp_get_g7()) as *const u8;
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

    let res_rel = (res as u32).wrapping_sub(wasp_get_g7());
    asm_map_mut().insert(name, res_rel);
    REGISTERED_COUNT += 1;
    1
}

/// Replacement for `dn_simdhash_ght_get_value_or_default` (fn 1020 in
/// merged wasm). Looks up the value in SIMD_MAP keyed on
/// (simdhash struct ptr, key string content). Falls back to ASM_MAP
/// (which only knows the bundled-resources assembly entries) so the
/// `mono_bundled_resources_get_assembly_resource_values` path — which
/// goes through fn 1024 directly without our higher-level fn 5671
/// patch — still finds the entries inserted via `wasp_add_assembly`.
static mut SIMDHASH_GET_COUNT: u32 = 0;

#[no_mangle]
pub unsafe extern "C" fn wasp_simdhash_get(table_ptr: u32, key_ptr: u32) -> u32 {
    SIMDHASH_GET_COUNT += 1;
    let simd_slot = &*SIMD_MAP.0.get();
    if let Some(&v) = simd_slot.as_ref().and_then(|m| m.get(&(table_ptr, key_ptr))) {
        if SIMDHASH_GET_COUNT <= 50 {
            let mut buf = [0u8; 96];
            let mut i = 0;
            for &b in b"[wasp-sh-get] tbl=0x" { buf[i] = b; i += 1; }
            for s in (0..32).step_by(4).rev() {
                let n = (table_ptr >> s) & 0xF;
                buf[i] = if n < 10 { b'0' + n as u8 } else { b'a' + (n - 10) as u8 };
                i += 1;
            }
            for &b in b" key=0x" { buf[i] = b; i += 1; }
            for s in (0..32).step_by(4).rev() {
                let n = (key_ptr >> s) & 0xF;
                buf[i] = if n < 10 { b'0' + n as u8 } else { b'a' + (n - 10) as u8 };
                i += 1;
            }
            for &b in b" SIMD-HIT" { buf[i] = b; i += 1; }
            debug_print(buf.as_ptr() as u32, i as u32);
        }
        return v;
    }
    // Fall back to string-content lookup in SIMD_MAP_BY_STR (handles
    // the common pattern where mono inserts under one ptr and looks up
    // under a different ptr to the same string content).
    let key = read_cstr_rel(key_ptr);
    let by_str_slot = &*SIMD_MAP_BY_STR.0.get();
    if !key.is_empty() {
        if let Some(&v) = by_str_slot.as_ref().and_then(|m| m.get(&(table_ptr, key.clone()))) {
            if SIMDHASH_GET_COUNT <= 50 {
                let mut buf = [0u8; 96];
                let mut i = 0;
                for &b in b"[wasp-sh-get] str-hit " { buf[i] = b; i += 1; }
                let nm = if key.len() < 50 { key.len() } else { 50 };
                let mut j = 0;
                while j < nm { buf[i] = key[j]; i += 1; j += 1; }
                debug_print(buf.as_ptr() as u32, i as u32);
            }
            return v;
        }
        // Try with .dll suffix appended (mono looks up "Foo" but we
        // stored "Foo.dll").
        if !key.ends_with(b".dll") {
            let mut k2 = key.clone();
            k2.extend_from_slice(b".dll");
            if let Some(&v) = by_str_slot.as_ref().and_then(|m| m.get(&(table_ptr, k2))) {
                if SIMDHASH_GET_COUNT <= 50 {
                    let mut buf = [0u8; 96];
                    let mut i = 0;
                    for &b in b"[wasp-sh-get] str-hit-dll " { buf[i] = b; i += 1; }
                    let nm = if key.len() < 50 { key.len() } else { 50 };
                    let mut j = 0;
                    while j < nm { buf[i] = key[j]; i += 1; j += 1; }
                    debug_print(buf.as_ptr() as u32, i as u32);
                }
                return v;
            }
        }
    }
    // Final fallback: ASM_MAP from wasp_add_assembly path (only used
    // if the high-level mono_wasm_add_assembly patch is in effect).
    let asm_slot = &*ASM_MAP.0.get();
    let asm = asm_slot.as_ref();
    let mut result = asm.and_then(|m| m.get(&key)).copied().unwrap_or(0);
    if result == 0 && !key.ends_with(b".dll") {
        let mut k2 = key.clone();
        k2.extend_from_slice(b".dll");
        result = asm.and_then(|m| m.get(&k2)).copied().unwrap_or(0);
    }
    if SIMDHASH_GET_COUNT <= 50 {
        let mut buf = [0u8; 96];
        let mut i = 0;
        for &b in b"[wasp-sh-get] " { buf[i] = b; i += 1; }
        let nm = if key.len() < 50 { key.len() } else { 50 };
        let mut j = 0;
        while j < nm { buf[i] = key[j]; i += 1; j += 1; }
        if result != 0 {
            for &b in b" ASM-HIT" { buf[i] = b; i += 1; }
        } else {
            for &b in b" MISS" { buf[i] = b; i += 1; }
        }
        debug_print(buf.as_ptr() as u32, i as u32);
    }
    result
}

static mut SIMDHASH_INS_COUNT: u32 = 0;

/// Replacement for `dn_simdhash_ght_insert_replace` (fn 555/559 in merged
/// wasm). Stores (table_ptr, key_string) → value in our shadow map.
/// Returns 0 = DN_SIMDHASH_INSERT_OK_ADDED_NEW.
///
/// Mono's signature: (struct, key, hash, value, mode) → status
#[no_mangle]
pub unsafe extern "C" fn wasp_simdhash_insert(
    table_ptr: u32,
    key_ptr: u32,
    _hash: u32,
    value_ptr: u32,
    _mode: u32,
) -> u32 {
    SIMDHASH_INS_COUNT += 1;
    // PASSTHROUGH MODE: for the first PASSTHROUGH_LIMIT distinct
    // inserts, call mono's REAL dn_simdhash insert leaf so the
    // entries land in the actual hash table. The dn_simdhash bug
    // triggers on the 3rd distinct-pointer insert (per the original
    // bypass investigation), so we let the first 2 succeed via the
    // real path (one of which should be corelib). After that we
    // fall back to the shadow-map bypass.
    const PASSTHROUGH_LIMIT: u32 = 2;
    if SIMDHASH_INS_COUNT <= PASSTHROUGH_LIMIT {
        let r = mono_embed::wasp_dn_simdhash_insert_original(
            table_ptr, key_ptr, _hash, value_ptr, _mode,
        );
        // Still maintain the shadow map so wasp_simdhash_get can
        // serve later lookups when the real hash misses.
        simd_map_mut().insert((table_ptr, key_ptr), value_ptr);
        let key_str = read_cstr_rel(key_ptr);
        if !key_str.is_empty() {
            simd_map_by_str_mut().insert((table_ptr, key_str), value_ptr);
        }
        // Also auto-populate ASM_MAP from the resource struct.
        let abs_value = dotnet_to_abs(value_ptr) as *const u32;
        if !abs_value.is_null() {
            let res_type = *abs_value.add(0);
            if res_type == 1 {
                let name_rel = *abs_value.add(1);
                if name_rel != 0 {
                    let name = read_cstr_rel(name_rel);
                    if !name.is_empty() {
                        asm_map_mut().insert(name.clone(), value_ptr);
                        if let Some(base) = name.strip_suffix(b".dll") {
                            asm_map_mut().insert(base.to_vec(), value_ptr);
                        }
                    }
                }
            }
        }
        let mut buf = [0u8; 64];
        let mut i = 0;
        for &b in b"[ins-passthrough#" { buf[i] = b; i += 1; }
        i = format_decimal(&mut buf, i, SIMDHASH_INS_COUNT as u64);
        for &b in b"] result=" { buf[i] = b; i += 1; }
        i = format_decimal(&mut buf, i, r as u64);
        debug_print(buf.as_ptr() as u32, i as u32);
        return r;
    }
    if SIMDHASH_INS_COUNT <= 3 {
        let mut buf = [0u8; 256];
        let mut i = 0;
        for &b in b"[ins] tbl=0x" { buf[i] = b; i += 1; }
        for s in (0..32).step_by(4).rev() {
            let n = (table_ptr >> s) & 0xF;
            buf[i] = if n < 10 { b'0' + n as u8 } else { b'a' + (n - 10) as u8 };
            i += 1;
        }
        for &b in b" key=0x" { buf[i] = b; i += 1; }
        for s in (0..32).step_by(4).rev() {
            let n = (key_ptr >> s) & 0xF;
            buf[i] = if n < 10 { b'0' + n as u8 } else { b'a' + (n - 10) as u8 };
            i += 1;
        }
        for &b in b" hash=0x" { buf[i] = b; i += 1; }
        for s in (0..32).step_by(4).rev() {
            let n = (_hash >> s) & 0xF;
            buf[i] = if n < 10 { b'0' + n as u8 } else { b'a' + (n - 10) as u8 };
            i += 1;
        }
        for &b in b" val=0x" { buf[i] = b; i += 1; }
        for s in (0..32).step_by(4).rev() {
            let n = (value_ptr >> s) & 0xF;
            buf[i] = if n < 10 { b'0' + n as u8 } else { b'a' + (n - 10) as u8 };
            i += 1;
        }
        debug_print(buf.as_ptr() as u32, i as u32);
    }
    simd_map_mut().insert((table_ptr, key_ptr), value_ptr);
    // Also store keyed by string content (if the key dereferences to a
    // non-empty NUL-terminated string at g7+key_ptr) so later get()
    // calls with a DIFFERENT pointer to the same content still hit.
    let key_str = read_cstr_rel(key_ptr);
    if !key_str.is_empty() {
        simd_map_by_str_mut().insert((table_ptr, key_str), value_ptr);
    }
    // If the value is a MonoBundledResource of type=ASSEMBLY (1), pull
    // the name string from the resource struct itself and also index
    // the entry in ASM_MAP under that name. This handles the common
    // case where mono passes a different key pointer on get() than on
    // insert() (e.g. via key_from_id() which returns a malloc'd
    // normalized copy) — the value's embedded name is a stable
    // identifier we can look up by.
    let abs_value = dotnet_to_abs(value_ptr) as *const u32;
    let res_type = *abs_value.add(0);
    if res_type == 1 {
        // MonoBundledAssemblyResource layout: type@0, id@4, hash@8,
        // free@12, name@16, data@20, size@24, ... — both id and name
        // are dotnet-relative pointers to the assembly name string.
        let name_rel = *abs_value.add(1);
        if name_rel != 0 {
            let name = read_cstr_rel(name_rel);
            if !name.is_empty() {
                asm_map_mut().insert(name.clone(), value_ptr);
                // Also register the no-suffix form (mono's
                // key_from_id strips .dll/.pdb).
                if let Some(base) = name.strip_suffix(b".dll") {
                    asm_map_mut().insert(base.to_vec(), value_ptr);
                }
            }
        }
    }
    0  // OK_ADDED_NEW
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

/// Cached cmh allocations for the current add1 call. Cleared after
/// each successful add1 completes. Reused on asyncify rewind so we
/// don't leak ~1.7 MB per yield (corelib bytes-copy is the dominant
/// allocation per BCL).
static mut ADD1_CACHED_NAME: *mut u8 = core::ptr::null_mut();
static mut ADD1_CACHED_BYTES: *mut u8 = core::ptr::null_mut();
static mut ADD1_CACHED_IDX: u32 = u32::MAX;

unsafe fn add1(name_src: &[u8], bytes_src: &[u8]) {
    let idx = BUILTIN_REG_IDX as u32;
    let (name, bytes) = if ADD1_CACHED_IDX == idx
        && !ADD1_CACHED_NAME.is_null()
        && !ADD1_CACHED_BYTES.is_null() {
        (ADD1_CACHED_NAME, ADD1_CACHED_BYTES)
    } else {
        // free previous cache entry if it was for a different idx
        if !ADD1_CACHED_NAME.is_null() { mono_embed::free(ADD1_CACHED_NAME); }
        if !ADD1_CACHED_BYTES.is_null() { mono_embed::free(ADD1_CACHED_BYTES); }
        let n = cmh(name_src, 0);
        let b = cmh(bytes_src, 4096);
        ADD1_CACHED_NAME = n;
        ADD1_CACHED_BYTES = b;
        ADD1_CACHED_IDX = idx;
        (n, b)
    };
    // Use mem_base offset for both name (string mono dereferences) and
    // bytes (PE buffer mono later reads metadata from). Previously used
    // g7 which is a different base — the mismatch caused mono to read
    // metadata at the WRONG address (off by mem_base - g7), failing
    // metadata-decode assertions during class load.
    let mb_before = wasp_get_mem_base();
    let name_off = dotnet_mem_offset(name);
    let bytes_off = dotnet_mem_offset(bytes);
    let rc = mono_embed::mono_wasm_add_assembly(
        name_off, bytes_off, bytes_src.len() as i32);
    // ALSO populate WASP_RESOURCES so wasp_bundled_resource_get can
    // bypass mono's broken dn_simdhash bundled-resources lookup. Build
    // a MonoBundledAssemblyResource (40 bytes) in mono heap and store
    // its mb-relative offset keyed by the bare name (without trailing
    // NUL). Mono's bundled_resources_get_assembly_resource (patched) will
    // be replaced to call wasp_bundled_resource_get which reads from
    // this map.
    {
        let res = mono_embed::malloc(40) as *mut u32;
        let name_off_u32 = name_off as u32;
        let data_off_u32 = bytes_off as u32;
        *res.add(0) = 1;                          // type = MONO_BUNDLED_ASSEMBLY
        *res.add(1) = name_off_u32;               // id (mb-relative)
        *res.add(2) = 458;                         // hash (mono's constant)
        *res.add(3) = 0;                           // free_data
        *res.add(4) = name_off_u32;               // name (mb-relative)
        *res.add(5) = data_off_u32;               // data (mb-relative)
        *res.add(6) = bytes_src.len() as u32;     // size
        *res.add(7) = 0;                           // pdb1
        *res.add(8) = 0;                           // pdb2
        *res.add(9) = 0;                           // padding
        let res_rel = (res as u32).wrapping_sub(wasp_get_mem_base());
        // Strip trailing NUL from key for consistent lookup
        let key: Vec<u8> = name_src.iter().take_while(|&&b| b != 0).copied().collect();
        wasp_resources_mut().insert(key, res_rel);
    }
    let mb_after = wasp_get_mem_base();
    let mut buf = [0u8; 192];
    let mut bi = 0;
    for &c in b"[add1] idx=" { buf[bi] = c; bi += 1; }
    bi = format_decimal(&mut buf, bi, idx as u64);
    for &c in b" mb_before=" { buf[bi] = c; bi += 1; }
    bi = format_decimal(&mut buf, bi, mb_before as u64);
    for &c in b" mb_after=" { buf[bi] = c; bi += 1; }
    bi = format_decimal(&mut buf, bi, mb_after as u64);
    for &c in b" name_off=" { buf[bi] = c; bi += 1; }
    bi = format_decimal(&mut buf, bi, name_off as u32 as u64);
    for &c in b" rc=" { buf[bi] = c; bi += 1; }
    bi = format_decimal(&mut buf, bi, rc as u32 as u64);
    print(&buf[..bi]);
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

// Stateful counter for register_next — each call registers ONE BCL
// from BUILTIN_BCL, advancing the counter. Lets client split the
// work across IC messages (scalar dn_simdhash is slow — 34-in-one
// blows past the 50B insn cap).
static mut BUILTIN_REG_IDX: usize = 0;

/// Register the NEXT BUILTIN_BCL entry via mono. Reply: "<idx>/<total>"
/// or "all-registered" when done. Call repeatedly until the latter.
#[export_name = "canister_update register_next"]
pub extern "C" fn canister_update_register_next() {
    unsafe {
        let total = BUILTIN_BCL.len();
        if BUILTIN_REG_IDX >= total {
            reply_blob(b"all-registered");
            return;
        }
        let (n, b) = BUILTIN_BCL[BUILTIN_REG_IDX];
        add1(n, b);
        BUILTIN_REG_IDX += 1;
        let mut buf = [0u8; 64];
        let mut bi = 0;
        bi = format_decimal(&mut buf, bi, BUILTIN_REG_IDX as u64);
        for &c in b"/" { buf[bi] = c; bi += 1; }
        bi = format_decimal(&mut buf, bi, total as u64);
        reply_blob(&buf[..bi]);
    }
}

/// Chunked register with asyncify yield support. Each call may either
/// complete an add_assembly normally OR yield mid-way; the caller
/// invokes repeatedly until the reply is "all-registered".
///
/// Protocol:
///   call register_chunk → "in_progress N/M" (call again) | "all-registered"
#[export_name = "canister_update register_chunk"]
pub extern "C" fn canister_update_register_chunk() {
    print(b"[register_chunk] entry");
    unsafe {
        let total = BUILTIN_BCL.len();

        if ASYNC_RESUMING {
            ASYNC_RESUMING = false;
            let buf_ptr = (&raw mut ASYNC_BUF) as u32;
            asyncify_start_rewind(buf_ptr);
        }

        if BUILTIN_REG_IDX >= total {
            reply_blob(b"all-registered");
            return;
        }
        let (n, b) = BUILTIN_BCL[BUILTIN_REG_IDX];
        print(b"[register_chunk] before add1");
        add1(n, b);
        print(b"[register_chunk] after add1");
        let st = asyncify_get_state();
        if st == 1 {
            asyncify_stop_unwind();
            ASYNC_RESUMING = true;
            let mut buf = [0u8; 96];
            let mut bi = 0;
            for &c in b"in_progress " { buf[bi] = c; bi += 1; }
            bi = format_decimal(&mut buf, bi, BUILTIN_REG_IDX as u64);
            for &c in b"/" { buf[bi] = c; bi += 1; }
            bi = format_decimal(&mut buf, bi, total as u64);
            reply_blob(&buf[..bi]);
            return;
        }
        // add1 completed normally (no unwind). Advance to the next BCL
        // and reply — caller invokes register_chunk again for the next
        // BCL. One BCL per message keeps inter-BCL mono work bounded.
        BUILTIN_REG_IDX += 1;
        let mut buf = [0u8; 96];
        let mut bi = 0;
        for &c in b"completed " { buf[bi] = c; bi += 1; }
        bi = format_decimal(&mut buf, bi, BUILTIN_REG_IDX as u64);
        for &c in b"/" { buf[bi] = c; bi += 1; }
        bi = format_decimal(&mut buf, bi, total as u64);
        reply_blob(&buf[..bi]);
    }
}

#[export_name = "canister_update boot_mono"]
pub extern "C" fn canister_update_boot_mono() {
    unsafe {
        if MONO_BOOTED { reply_blob(b"already booted"); return; }
        // Disable maybe_yield unwinds for the whole boot_mono call.
        // boot_mono doesn't have asyncify-aware caller logic; if a
        // yield fired, state==1 would leak past mono_wasm_load_runtime
        // and trap somewhere downstream. boot_mono runs in canister_init-
        // like context (full instruction budget), so chunking isn't
        // needed here anyway.
        ASYNC_DISABLED = true;
        // Pre-grow linear memory by 32 MiB (512 wasm pages) BEFORE
        // touching mono. The agent's diagnosis of the dn_simdhash bug
        // was: a stale base pointer after the table grows past its
        // initial bucket count on the 3rd insert (a memory.grow
        // triggers but cached HEAPU8 views are not updated). By
        // pre-growing, we hope to keep the dn_simdhash rehash from
        // triggering memory.grow during table init.
        // pre-grow disabled — it shifts mb mid-flight; canister_init's
        // 256MiB pre-grow should already provide enough headroom.
        print(b"[wasp-boot] pre-grow disabled");
        print(b"[wasp-boot] setenv");
        // Mono code does `global.get 7 + arg` to dereference; pointers
        // must be dotnet-relative (caller subtracts DOTNET_MEMORY_BASE).
        mono_embed::mono_wasm_setenv(
            dotnet_mem_offset(TZ_INV_NAME.as_ptr()),
            dotnet_mem_offset(TZ_INV_VAL.as_ptr()));
        mono_embed::mono_wasm_setenv(
            dotnet_mem_offset(MONO_DEBUG_KEY.as_ptr()),
            dotnet_mem_offset(MONO_DEBUG_VAL.as_ptr()));
        // 3rd setenv — was the dn_simdhash trap point but we now have
        // the passthrough+shadow-map bypass at fn 559, so this should
        // succeed.
        mono_embed::mono_wasm_setenv(
            dotnet_mem_offset(MONO_PATH_KEY.as_ptr()),
            dotnet_mem_offset(MONO_PATH_VAL.as_ptr()));

        print(b"[wasp-boot] build keys/vals in dotnet heap");
        // 4 properties: APP_BASE, RID, INV, TPA. TPA causes mono's
        // mono_core_preload_hook to load corelib via the standard
        // g_file_test + open + read path (backed by our vfs).
        // Allocate in mono-malloc heap so g7 + array_ptr resolves
        // correctly inside mono code.
        let keys_arr = mono_embed::malloc(20) as *mut u32;
        let vals_arr = mono_embed::malloc(20) as *mut u32;
        // CRITICAL: each property STRING also needs to be in mono-
        // malloc heap (or otherwise reachable via g7+ptr by mono
        // code). Copy each Rust static string into mono-malloc and
        // store the dotnet-relative pointer to that copy.
        unsafe fn cpy_static(src: &[u8]) -> u32 {
            let dst = mono_embed::malloc(src.len()) as *mut u8;
            let mut i = 0;
            while i < src.len() {
                *dst.add(i) = src[i];
                i += 1;
            }
            // Use mem_base (NOT g7) — mono dereferences via
            // mem_base + ptr after multi-memory-lowering.
            (dst as u32).wrapping_sub(wasp_get_mem_base())
        }
        *keys_arr.add(0) = cpy_static(APP_BASE_KEY);
        *keys_arr.add(1) = cpy_static(RID_KEY);
        *keys_arr.add(2) = cpy_static(INV_KEY);
        *keys_arr.add(3) = cpy_static(TPA_KEY);
        *keys_arr.add(4) = cpy_static(APP_PATHS_KEY);
        *vals_arr.add(0) = cpy_static(APP_BASE_VAL);
        *vals_arr.add(1) = cpy_static(RID_VAL);
        *vals_arr.add(2) = cpy_static(INV_VAL);
        *vals_arr.add(3) = cpy_static(TPA_VAL);
        *vals_arr.add(4) = cpy_static(APP_PATHS_VAL);

        // Print the TPA value via direct read at the dotnet-relative
        // pointer to verify our layout — mono should see this same
        // string when it parses TRUSTED_PLATFORM_ASSEMBLIES.
        let tpa_val_rel = *vals_arr.add(3);
        let tpa_abs = wasp_get_mem_base().wrapping_add(tpa_val_rel) as *const u8;
        let mut buf = [0u8; 256];
        let prefix = b"[wasp-boot] tpa_val=";
        let mut bi = 0;
        for &b in prefix { buf[bi] = b; bi += 1; }
        let mut k = 0;
        while k < 200 {
            let bb = *tpa_abs.add(k);
            if bb == 0 { break; }
            buf[bi] = bb;
            bi += 1;
            k += 1;
        }
        debug_print(buf.as_ptr() as u32, bi as u32);

        print(b"[wasp-boot] load_runtime");
        mono_embed::mono_wasm_load_runtime(
            0,
            5,
            dotnet_mem_offset(keys_arr as *const u8) as *const *const u8,
            dotnet_mem_offset(vals_arr as *const u8) as *const *const u8,
        );
        ASYNC_DISABLED = false;
        MONO_BOOTED = true;
        reply_blob(b"booted!");
    }
}

/// Probe the ACTUAL bundled_resources globals at 0x885528 and
/// 0x885532 (discovered by inspecting pristine mono_wasm_add_assembly
/// → fn 1129 → which reads/stores tables there). If non-zero, our
/// passthrough successfully populated the real bundled_resources
/// tables.
#[export_name = "canister_update probe_bundled"]
pub extern "C" fn canister_update_probe_bundled() {
    unsafe {
        if !MONO_BOOTED { reply_blob(b"not booted yet"); return; }
        let g7 = wasp_get_g7();
        let mut buf = [0u8; 200];
        let mut bi = 0;
        for &c in b"bundled@0x885528=0x" { buf[bi] = c; bi += 1; }
        let v = *((g7.wrapping_add(0x885528)) as *const u32);
        for s in (0..32).step_by(4).rev() {
            let n = (v >> s) & 0xF;
            buf[bi] = if n < 10 { b'0' + n as u8 } else { b'a' + (n - 10) as u8 };
            bi += 1;
        }
        for &c in b" @0x885532=0x" { buf[bi] = c; bi += 1; }
        let v = *((g7.wrapping_add(0x885532)) as *const u32);
        for s in (0..32).step_by(4).rev() {
            let n = (v >> s) & 0xF;
            buf[bi] = if n < 10 { b'0' + n as u8 } else { b'a' + (n - 10) as u8 };
            bi += 1;
        }
        for &c in b" cache@0x885508=0x" { buf[bi] = c; bi += 1; }
        let v = *((g7.wrapping_add(0x885508)) as *const u32);
        for s in (0..32).step_by(4).rev() {
            let n = (v >> s) & 0xF;
            buf[bi] = if n < 10 { b'0' + n as u8 } else { b'a' + (n - 10) as u8 };
            bi += 1;
        }
        reply_blob(&buf[..bi]);
    }
}

/// Force-write a value into the corelib cache slot. Demo/diagnostic:
/// passes a malloc'd zeroed buffer ptr to see what mono does when it
/// thinks corelib is loaded but the struct is empty. Exits with
/// whatever new failure mode this exposes — useful to map the
/// downstream MonoAssembly fields mono accesses on the corlib pointer.
#[export_name = "canister_update force_corlib"]
pub extern "C" fn canister_update_force_corlib() {
    unsafe {
        if !MONO_BOOTED { reply_blob(b"not booted yet"); return; }
        let g7 = wasp_get_g7();
        // Allocate a zeroed 4KB buffer in mono's heap. Use that as
        // a fake MonoAssembly pointer.
        let fake = mono_embed::malloc(4096) as *mut u8;
        if fake.is_null() { reply_blob(b"malloc null"); return; }
        let mut i = 0;
        while i < 4096 { *fake.add(i) = 0; i += 1; }
        // Convert abs ptr to dotnet-relative for mono's convention.
        let fake_rel = (fake as u32).wrapping_sub(g7);
        // Write dotnet-relative ptr to the cache slot.
        let slot = (g7.wrapping_add(0x885508)) as *mut u32;
        *slot = fake_rel;
        let mut buf = [0u8; 64];
        let mut bi = 0;
        for &c in b"force_corlib slot=0x" { buf[bi] = c; bi += 1; }
        for s in (0..32).step_by(4).rev() {
            let n = (fake_rel >> s) & 0xF;
            buf[bi] = if n < 10 { b'0' + n as u8 } else { b'a' + (n - 10) as u8 };
            bi += 1;
        }
        reply_blob(&buf[..bi]);
    }
}

/// After force_corlib, ask mono to load a tiny assembly to see what
/// fields of the (fake) corlib pointer mono actually dereferences.
/// Will trap somewhere; the trap location maps the next field we'd
/// need to populate in the fake struct.
#[export_name = "canister_update probe_load"]
pub extern "C" fn canister_update_probe_load() {
    unsafe {
        if !MONO_BOOTED { reply_blob(b"not booted yet"); return; }
        // Try loading an arbitrary assembly. The internal class
        // resolution will deref the corlib pointer.
        let name = b"System.Runtime.dll\0";
        let dst = mono_embed::malloc(name.len()) as *mut u8;
        let mut i = 0;
        while i < name.len() { *dst.add(i) = name[i]; i += 1; }
        let asm = mono_embed::mono_wasm_assembly_load(dotnet_offset(dst));
        let mut buf = [0u8; 64];
        let mut bi = 0;
        for &c in b"asm=0x" { buf[bi] = c; bi += 1; }
        let v = asm as u32;
        for s in (0..32).step_by(4).rev() {
            let n = (v >> s) & 0xF;
            buf[bi] = if n < 10 { b'0' + n as u8 } else { b'a' + (n - 10) as u8 };
            bi += 1;
        }
        reply_blob(&buf[..bi]);
    }
}

/// Sweep a window of dotnet's static data section looking for any
/// non-zero global. Helps locate `bundled_assembly_count`,
/// `bundled_resources` table head, and other mono globals whose
/// addresses we don't know upfront. Reports first 32 non-zero u32s
/// in the window.
#[export_name = "canister_update probe_globals"]
pub extern "C" fn canister_update_probe_globals() {
    unsafe {
        if !MONO_BOOTED { reply_blob(b"not booted yet"); return; }
        let g7 = wasp_get_g7();
        let mut buf = [0u8; 1024];
        let mut bi = 0;
        for &c in b"non-zero in 0x880000..0x8a0000 (4-byte step):" { buf[bi] = c; bi += 1; }
        let mut found = 0;
        // Scan finely around the corelib loader's code-referenced
        // addresses (0x885508, 0x885496, 0x885484) to find what's
        // actually populated.
        let mut off = 0x880000u32;
        while off < 0x8a0000 && found < 30 {
            let v = *((g7.wrapping_add(off)) as *const u32);
            if v != 0 {
                buf[bi] = b' '; bi += 1;
                buf[bi] = b'0'; bi += 1; buf[bi] = b'x'; bi += 1;
                for s in (0..32).step_by(4).rev() {
                    let n = (off >> s) & 0xF;
                    buf[bi] = if n < 10 { b'0' + n as u8 } else { b'a' + (n - 10) as u8 };
                    bi += 1;
                }
                buf[bi] = b'='; bi += 1;
                for s in (0..32).step_by(4).rev() {
                    let n = (v >> s) & 0xF;
                    buf[bi] = if n < 10 { b'0' + n as u8 } else { b'a' + (n - 10) as u8 };
                    bi += 1;
                }
                found += 1;
            }
            off = off.wrapping_add(4);
        }
        if found == 0 {
            for &c in b" (all zero)" { buf[bi] = c; bi += 1; }
        }
        reply_blob(&buf[..bi]);
    }
}

/// Dump the corelib cache slot AND the preload-hook list head AND
/// the bundled_assemblies count flag region — all the pieces of
/// state mono's `mono_assembly_load_corlib` consults.
#[export_name = "canister_update probe_corlib"]
pub extern "C" fn canister_update_probe_corlib() {
    unsafe {
        if !MONO_BOOTED { reply_blob(b"not booted yet"); return; }
        let g7 = wasp_get_g7();
        let corlib_slot = *((g7.wrapping_add(0x885508)) as *const u32);
        let hook_head = *((g7.wrapping_add(0x885496)) as *const u32);
        let g_885484 = *((g7.wrapping_add(0x885484)) as *const u32);
        let mut buf = [0u8; 200];
        let mut bi = 0;
        for &c in b"corelib=0x" { buf[bi] = c; bi += 1; }
        for s in (0..32).step_by(4).rev() {
            let n = (corlib_slot >> s) & 0xF;
            buf[bi] = if n < 10 { b'0' + n as u8 } else { b'a' + (n - 10) as u8 };
            bi += 1;
        }
        for &c in b" hook_head[0x885496]=0x" { buf[bi] = c; bi += 1; }
        for s in (0..32).step_by(4).rev() {
            let n = (hook_head >> s) & 0xF;
            buf[bi] = if n < 10 { b'0' + n as u8 } else { b'a' + (n - 10) as u8 };
            bi += 1;
        }
        for &c in b" [0x885484]=0x" { buf[bi] = c; bi += 1; }
        for s in (0..32).step_by(4).rev() {
            let n = (g_885484 >> s) & 0xF;
            buf[bi] = if n < 10 { b'0' + n as u8 } else { b'a' + (n - 10) as u8 };
            bi += 1;
        }
        reply_blob(&buf[..bi]);
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
/// Initial value of dotnet's data base (`global 7`) before any
/// memory.grow shift. Used by static-data convention BEFORE Mono
/// triggers its first grow. After grow, callers must use
/// `wasp_get_g7()` for the live value.
pub(crate) const DOTNET_MEMORY_BASE: u32 = 2_752_512;

/// Returns the LIVE value of dotnet's `global 7` (the data section
/// base, which shifts every time fn 5236's grow wrapper runs). Body
/// is replaced post-merge by `scripts/patch_fn_to_global_get.py` with
/// a single `global.get 7` instruction.
///
/// `#[inline(never)]` + `black_box` are CRITICAL: without them LLVM
/// inlines the constant return at every call site and the post-merge
/// patch becomes dead code (no callers ever execute the patched body).
#[no_mangle]
#[inline(never)]
pub extern "C" fn wasp_get_g7() -> u32 {
    static mut G7_SENTINEL: u32 = 0;
    unsafe { core::ptr::write_volatile(&raw mut G7_SENTINEL, 0xC7DE0007); }
    core::hint::black_box(DOTNET_MEMORY_BASE)
}

/// Distinct from g7: this returns the multi-memory-lowering mem_base
/// (global N where dotnet's memory was placed in the merged module).
/// Patched post-merge to `global.get <N>` by find-and-patch in
/// 30_merge.sh. Needed for asyncify's buffer pointer math: asyncify's
/// lowered code reads buffer fields via `mem_base + ptr`, so the ptr
/// we pass MUST be `abs - mem_base` (not `abs - g7`).
///
/// Same inlining concern as `wasp_get_g7` — `#[inline(never)]` +
/// `black_box` are required so call sites actually issue a `call`
/// to this function (whose body the post-merge patch replaces).
#[no_mangle]
#[inline(never)]
pub extern "C" fn wasp_get_mem_base() -> u32 {
    static mut MEM_BASE_SENTINEL: u32 = 0;
    unsafe { core::ptr::write_volatile(&raw mut MEM_BASE_SENTINEL, 0xDEADBEEF); }
    core::hint::black_box(DOTNET_MEMORY_BASE)
}

#[inline]
fn dotnet_offset(p: *const u8) -> *const u8 {
    ((p as u32).wrapping_sub(wasp_get_g7())) as *const u8
}

/// MEMORY-BASED dotnet_offset: subtracts the multi-memory-lowering
/// mem_base instead of the g7 ALC base. Use this for byte buffers
/// (assembly bytes, strings) that mono will dereference via
/// `mem_base + ptr` after our offset is treated as a memory address.
#[inline]
fn dotnet_mem_offset(p: *const u8) -> *const u8 {
    ((p as u32).wrapping_sub(wasp_get_mem_base())) as *const u8
}

/// Inverse of dotnet_offset: given a dotnet-relative ptr received from
/// Mono code (e.g. as a callback arg), return the absolute address in
/// our linear memory.
#[inline]
pub(crate) fn dotnet_to_abs(rel: u32) -> *const u8 {
    rel.wrapping_add(wasp_get_g7()) as *const u8
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
