//! wasp_canister — the Rust ic-cdk shim that hosts Microsoft's pre-built
//! `dotnet.native.wasm` inside an ICP canister.

pub mod env_imports;
pub mod mono_embed;
pub mod vfs;
pub mod wasi_imports;
pub mod wasp_stable_abi;

use ic_cdk::{init, query, update};

const CORELIB: &[u8] = include_bytes!("../../inputs/System.Private.CoreLib.dll");
const WASPHOST: &[u8] = include_bytes!("../../inputs/WaspHost.dll");

static APP_BASE_KEY: &[u8] = b"APP_CONTEXT_BASE_DIRECTORY\0";
static APP_BASE_VAL: &[u8] = b"/\0";
static RID_KEY: &[u8]      = b"RUNTIME_IDENTIFIER\0";
static RID_VAL: &[u8]      = b"browser-wasm\0";
static INV_KEY: &[u8]      = b"System.Globalization.Invariant\0";
static INV_VAL: &[u8]      = b"true\0";

static TZ_INV_NAME: &[u8]  = b"DOTNET_SYSTEM_TIMEZONE_INVARIANT\0";
static TZ_INV_VAL: &[u8]   = b"true\0";
static MONO_LOG_LEVEL_NAME: &[u8] = b"MONO_LOG_LEVEL\0";
static MONO_LOG_LEVEL_VAL: &[u8]  = b"info\0";
static MONO_LOG_MASK_NAME: &[u8]  = b"MONO_LOG_MASK\0";
static MONO_LOG_MASK_VAL: &[u8]   = b"all\0";

static CORELIB_NAME: &[u8] = b"System.Private.CoreLib.dll\0";
static WASPHOST_NAME: &[u8] = b"WaspHost.dll\0";

#[init]
fn canister_init() {
    ic_cdk::println!("[wasp-dotnet] canister_init: pre-Mono");

    unsafe {
        // Step 0: run C/C++ static ctors so Mono's heap, errno, malloc
        // tables etc. are initialised before any other Mono call.
        mono_embed::__wasm_call_ctors();
        ic_cdk::println!("[wasp-dotnet] canister_init: __wasm_call_ctors done");

        // Step 1: env vars Mono reads during init.
        // DOTNET_SYSTEM_TIMEZONE_INVARIANT is read in driver.c:197 and
        // segfaults if NULL — set it before anything else.
        mono_embed::mono_wasm_setenv(TZ_INV_NAME.as_ptr(), TZ_INV_VAL.as_ptr());
        mono_embed::mono_wasm_setenv(MONO_LOG_LEVEL_NAME.as_ptr(), MONO_LOG_LEVEL_VAL.as_ptr());
        mono_embed::mono_wasm_setenv(MONO_LOG_MASK_NAME.as_ptr(), MONO_LOG_MASK_VAL.as_ptr());

        ic_cdk::println!("[wasp-dotnet] canister_init: registering corelib + wasphost");

        // Step 2: bundle assemblies so mono_assembly_load_corlib uses
        // the bundled-resources fast path instead of probing the FS.
        let _ = mono_embed::mono_wasm_add_assembly(
            CORELIB_NAME.as_ptr(),
            CORELIB.as_ptr(),
            CORELIB.len() as i32,
        );
        let _ = mono_embed::mono_wasm_add_assembly(
            WASPHOST_NAME.as_ptr(),
            WASPHOST.as_ptr(),
            WASPHOST.len() as i32,
        );

        // Step 3: build property arrays for monovm_initialize.
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

        ic_cdk::println!("[wasp-dotnet] canister_init: about to call mono_wasm_load_runtime");
        mono_embed::mono_wasm_load_runtime(
            0,                  // debug_level
            keys.len() as i32,  // propertyCount
            keys.as_ptr(),
            vals.as_ptr(),
        );
        ic_cdk::println!("[wasp-dotnet] canister_init: returned from mono_wasm_load_runtime");
    }
}

#[query(name = "hello")]
fn hello() -> String {
    "hello from wasp-dotnet runtime canister".to_string()
}

#[update(name = "upload_chunk")]
fn upload_chunk(_name: String, _offset: u64, _data: Vec<u8>) -> u64 {
    0
}

#[query(name = "list_assemblies")]
fn list_assemblies() -> Vec<(String, u64)> {
    vec![]
}
