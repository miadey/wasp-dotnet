//! wasp_canister — the Rust ic-cdk shim that hosts Microsoft's pre-built
//! `dotnet.native.wasm` inside an ICP canister.
//!
//! Phase A skeleton. The modules in this crate together provide the
//! ~85 host imports that `dotnet.native.wasm` needs:
//!
//!   * [`env_imports`] — the 75 `(import "env" ...)` entries (Emscripten
//!                       POSIX shims, mono interp/JIT trampolines, JS
//!                       interop hooks) — all stubbed for v0.1.
//!   * [`wasi_imports`] — the 10 `(import "wasi_snapshot_preview1" ...)`
//!                        entries; `fd_write` to stdout/stderr is wired
//!                        to `ic0::debug_print`, the rest no-op.
//!   * [`mono_embed`] — `extern "C"` declarations for the Mono embedding
//!                      API exported by `dotnet.native.wasm` (post-merge).
//!
//! After `cargo build --release --target wasm32-unknown-unknown`, the
//! resulting `wasp_canister.wasm` is fed into `wasm-merge` together
//! with `dotnet.native.wasm`; binaryen resolves both directions of
//! cross-module imports/exports and emits a single canister wasm.

pub mod env_imports;
pub mod mono_embed;
pub mod wasi_imports;

use ic_cdk::{init, query, update};

// ---------------------------------------------------------------------------
// canister entry points
// ---------------------------------------------------------------------------

/// Phase A v0.1: just log. Issue #11 wires this to
/// `mono_embed::mono_wasm_load_runtime` and assembly registration.
#[init]
fn canister_init() {
    ic_cdk::println!(
        "[wasp-dotnet] canister_init (Phase A v0.1, managed runtime not yet booted)"
    );
}

/// Phase A v0.1 stub query. Returns a fixed string so we can prove the
/// wasm-merge + ic-cdk dispatcher pipeline lands a working canister on
/// dfx before tackling Mono boot. Issue #11 will replace the body with
/// a `mono_wasm_invoke_jsexport` call into `WaspHost.Bridge.Hello()`.
#[query(name = "hello")]
fn hello() -> String {
    "hello from wasp-dotnet runtime canister (managed code not yet wired)".to_string()
}

// ---------------------------------------------------------------------------
// stable-memory assembly upload skeleton
// ---------------------------------------------------------------------------
//
// The deploy flow (`runtime/scripts/40_deploy.sh`) chunks the corelib +
// user assemblies into ~1 MiB pieces and calls `upload_chunk`
// repeatedly. Phase B fills in the stable-memory backing; for now we
// accept the bytes and discard them so the candid surface is stable.

/// Append `data` to the named assembly's stable-memory buffer at
/// `offset`. Returns the new total length of that assembly.
#[update(name = "upload_chunk")]
fn upload_chunk(_name: String, _offset: u64, _data: Vec<u8>) -> u64 {
    // TODO(phase-B): write data into stable memory under `name`,
    // tracked by an index in a `StableBTreeMap<String, AssemblyMeta>`.
    0
}

/// List `(assembly_name, byte_length)` for everything currently
/// uploaded. Empty until upload_chunk is implemented.
#[query(name = "list_assemblies")]
fn list_assemblies() -> Vec<(String, u64)> {
    vec![]
}
