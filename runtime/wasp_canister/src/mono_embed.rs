// mono_embed.rs — `extern "C"` declarations for the Mono embedding API
// exported by `dotnet.native.wasm`.
//
// Linkage model
// -------------
// `wasp_canister.wasm` is built standalone (no dotnet.native.wasm in
// scope). At Rust-build time these `extern "C"` symbols are *imports*
// from the wasm `env` module — the wasm32-unknown-unknown linker emits
// `(import "env" "<name>")` for each one. After we run
// `wasm-merge wasp_canister.wasm dotnet.native.wasm`, binaryen
// rewrites those env imports against `dotnet.native.wasm`'s exports of
// the same names, leaving us with a single self-contained module.
//
// Signatures derived from `wasm-tools print` of the dotnet.native.wasm
// shipped with .NET 10 BlazorWasm. They use Mono's standard wasm
// embedding ABI: opaque pointers for domain/assembly/class/method/object
// handles, char* strings, raw byte pointers + lengths for assembly
// images.
//
// Phase A consumers: `lib.rs::canister_init` will (eventually) call
// `mono_wasm_load_runtime` then `mono_wasm_assembly_load` then
// `mono_wasm_invoke_jsexport`. Issue #11 wires that up; this file only
// declares the FFI surface.

#![allow(non_camel_case_types)]
#![allow(clippy::missing_safety_doc)]

use core::ffi::c_void;

// ---------------------------------------------------------------------------
// Opaque Mono handle types
// ---------------------------------------------------------------------------

#[repr(C)]
pub struct MonoDomain {
    _private: [u8; 0],
}

#[repr(C)]
pub struct MonoAssembly {
    _private: [u8; 0],
}

#[repr(C)]
pub struct MonoClass {
    _private: [u8; 0],
}

#[repr(C)]
pub struct MonoMethod {
    _private: [u8; 0],
}

#[repr(C)]
pub struct MonoObject {
    _private: [u8; 0],
}

// ---------------------------------------------------------------------------
// Mono embedding ABI — exported by dotnet.native.wasm, imported here
// ---------------------------------------------------------------------------
//
// In 30_merge.sh's wasm-merge invocation we name dotnet.native.wasm
// "dotnet". So our import-side declaration must use the matching module
// name "dotnet" — wasm-merge then resolves these against dotnet's
// exports of the same names.

#[link(wasm_import_module = "dotnet")]
extern "C" {
    // ----- C/C++ static initialisation -----

    /// Run all C/C++ static initialisers. **Must be called once before
    /// any other dotnet.native.wasm export.** The Emscripten heap,
    /// `errno` slot, malloc tables, and Mono's static state are all
    /// set up here. Skipping this leads to "heap out of bounds" traps
    /// the moment Mono touches its heap.
    pub fn __wasm_call_ctors();

    // ----- runtime lifecycle -----

    /// `mono_wasm_load_runtime(debug_level, propertyCount, propertyKeys,
    ///                          propertyValues)`
    /// Verified against dotnet/runtime release/10.0
    /// `src/mono/browser/runtime/driver.c:185`. The 4 i32 args are:
    ///   debug_level    = 0 disables debugger; >0 enables w/ that log level
    ///   propertyCount  = number of monovm_initialize properties
    ///   propertyKeys   = const char **  (UTF-8 NUL-terminated)
    ///   propertyValues = const char **
    /// Microsoft's host always passes at minimum:
    ///   APP_CONTEXT_BASE_DIRECTORY = "/"
    ///   RUNTIME_IDENTIFIER = "browser-wasm"
    pub fn mono_wasm_load_runtime(
        debug_level: i32,
        property_count: i32,
        property_keys: *const *const u8,
        property_values: *const *const u8,
    );

    /// `mono_wasm_init_finalizer_thread()` — start the GC finalizer
    /// pump. On wasi this is a no-op; safe to call.
    pub fn mono_wasm_init_finalizer_thread();

    /// `mono_wasm_exit(exit_code) -> exit_code` — shut the runtime
    /// down. Returns the exit code unchanged.
    pub fn mono_wasm_exit(exit_code: i32) -> i32;

    /// `mono_wasm_setenv(name: *const u8, value: *const u8)`
    pub fn mono_wasm_setenv(name: *const u8, value: *const u8);

    /// `mono_wasm_getenv(name: *const u8) -> *const u8`
    pub fn mono_wasm_getenv(name: *const u8) -> *const u8;

    /// `mono_wasm_set_main_args(argc: i32, argv: *const *const u8)`
    pub fn mono_wasm_set_main_args(argc: i32, argv: *const *const u8);

    /// `mono_wasm_parse_runtime_options(argc: i32, argv: *const *const u8)`
    pub fn mono_wasm_parse_runtime_options(argc: i32, argv: *const *const u8);

    // ----- assembly registration -----

    /// `mono_wasm_add_assembly(name: *const u8, data: *const u8,
    ///                          size: i32) -> i32`
    /// Register an assembly image with the runtime so a subsequent
    /// `mono_wasm_assembly_load` of `name` can find it.
    pub fn mono_wasm_add_assembly(name: *const u8, data: *const u8, size: i32) -> i32;

    /// `mono_wasm_add_satellite_assembly(name: *const u8, culture: *const u8,
    ///                                    data: *const u8, size: i32)`
    pub fn mono_wasm_add_satellite_assembly(
        name: *const u8,
        culture: *const u8,
        data: *const u8,
        size: i32,
    );

    // ----- assembly / class / method lookup -----

    /// `mono_wasm_assembly_load(name: *const u8) -> *mut MonoAssembly`
    pub fn mono_wasm_assembly_load(name: *const u8) -> *mut MonoAssembly;

    /// `mono_wasm_assembly_find_class(asm: *mut MonoAssembly,
    ///                                 namespace: *const u8,
    ///                                 name: *const u8) -> *mut MonoClass`
    pub fn mono_wasm_assembly_find_class(
        asm: *mut MonoAssembly,
        namespace: *const u8,
        name: *const u8,
    ) -> *mut MonoClass;

    /// `mono_wasm_assembly_find_method(klass: *mut MonoClass,
    ///                                  name: *const u8,
    ///                                  param_count: i32) -> *mut MonoMethod`
    pub fn mono_wasm_assembly_find_method(
        klass: *mut MonoClass,
        name: *const u8,
        param_count: i32,
    ) -> *mut MonoMethod;

    // ----- managed invocation -----

    /// `mono_wasm_invoke_jsexport(method: *mut MonoMethod, args: *mut c_void)`
    /// Calls a managed method whose handle was obtained from
    /// `mono_wasm_assembly_find_method`. `args` points to the
    /// runtime's "JSExport" argument block (layout defined by Mono;
    /// `bridge.rs` will marshal Candid bytes into it in Phase B).
    pub fn mono_wasm_invoke_jsexport(method: *mut MonoMethod, args: *mut c_void);

    /// `mono_wasm_exec_regression(verbose: i32, image_name: *const u8)
    ///                            -> failure_count`
    pub fn mono_wasm_exec_regression(verbose: i32, image_name: *const u8) -> i32;

    // ----- GC root tracking -----

    /// `mono_wasm_register_root(start: *mut u8, size: i32,
    ///                           description: *const u8) -> i32`
    pub fn mono_wasm_register_root(
        start: *mut u8,
        size: i32,
        description: *const u8,
    ) -> i32;

    /// `mono_wasm_deregister_root(start: *mut u8)`
    pub fn mono_wasm_deregister_root(start: *mut u8);

    // ----- string + pointer helpers -----

    /// `mono_wasm_string_from_utf16_ref(text: *const u16, len: i32,
    ///                                   result: *mut *mut MonoObject)`
    pub fn mono_wasm_string_from_utf16_ref(
        text: *const u16,
        len: i32,
        result: *mut *mut MonoObject,
    );

    /// `mono_wasm_intern_string_ref(string_ref: *mut *mut MonoObject)`
    pub fn mono_wasm_intern_string_ref(string_ref: *mut *mut MonoObject);

    /// `mono_wasm_string_get_data_ref(string_ref: *mut *mut MonoObject,
    ///                                 outChars: *mut *mut u16,
    ///                                 outLengthBytes: *mut i32,
    ///                                 outIsInterned: *mut i32)`
    pub fn mono_wasm_string_get_data_ref(
        string_ref: *mut *mut MonoObject,
        out_chars: *mut *mut u16,
        out_length_bytes: *mut i32,
        out_is_interned: *mut i32,
    );

    /// `mono_wasm_strdup(s: *const u8) -> *const u8`
    pub fn mono_wasm_strdup(s: *const u8) -> *const u8;

    /// `mono_wasm_method_get_name(method: *mut MonoMethod) -> *const u8`
    pub fn mono_wasm_method_get_name(method: *mut MonoMethod) -> *const u8;

    /// `mono_wasm_method_get_full_name(method: *mut MonoMethod) -> *const u8`
    pub fn mono_wasm_method_get_full_name(method: *mut MonoMethod) -> *const u8;

    // ----- libc surface re-exported by dotnet.native.wasm -----

    /// Allocate from Mono's heap. Pointers passed across the FFI for
    /// strings / assembly bytes / arg blocks must live in this heap so
    /// the runtime can read them.
    pub fn malloc(size: usize) -> *mut u8;

    /// Free a pointer previously returned by `malloc`.
    pub fn free(p: *mut u8);

    /// Preserved-original dn_simdhash insert leaf (5 i32 → i32):
    /// (table_ptr, key, hash, value, mode) → status. Exported by
    /// `scripts/inject_dn_simdhash_passthrough.py`. Calling this
    /// invokes mono's REAL bucket scan / insert logic instead of
    /// our shadow-map shim. Use sparingly — the bug we're working
    /// around triggers on the 3rd distinct-pointer insert.
    pub fn wasp_dn_simdhash_insert_original(
        table: u32, key: u32, hash: u32, value: u32, mode: u32,
    ) -> u32;
}
