using System.Runtime.InteropServices;

namespace Wasp;

// P/Invoke declarations for the Rust trampolines exported by the
// canister's wasm module (see issue #18). Mono resolves DllImport with
// module name "env" against the wasm `env` import namespace, which is
// where the Rust side registers its `wasp_*` shims that forward to the
// real `ic0.*` system API.
//
// All entry points are `wasp_*` prefixed (not `ic0_*`) to make it
// obvious in stack traces / error messages that we're going through
// the trampoline layer rather than calling ic0 directly.
internal static unsafe class Ic0
{
    // Stable memory --------------------------------------------------------
    [DllImport("env", EntryPoint = "wasp_stable_size")]
    public static extern ulong wasp_stable_size();

    [DllImport("env", EntryPoint = "wasp_stable_grow")]
    public static extern ulong wasp_stable_grow(ulong new_pages);

    [DllImport("env", EntryPoint = "wasp_stable_read")]
    public static extern void wasp_stable_read(byte* dst, ulong offset, ulong size);

    [DllImport("env", EntryPoint = "wasp_stable_write")]
    public static extern void wasp_stable_write(ulong offset, byte* src, ulong size);

    // Message arg ----------------------------------------------------------
    [DllImport("env", EntryPoint = "wasp_msg_arg_size")]
    public static extern uint wasp_msg_arg_size();

    [DllImport("env", EntryPoint = "wasp_msg_arg_copy")]
    public static extern void wasp_msg_arg_copy(byte* dst, uint offset, uint size);

    // Reply / trap / debug -------------------------------------------------
    [DllImport("env", EntryPoint = "wasp_reply")]
    public static extern void wasp_reply(byte* src, uint size);

    [DllImport("env", EntryPoint = "wasp_trap")]
    public static extern void wasp_trap(byte* src, uint size);

    [DllImport("env", EntryPoint = "wasp_debug_print")]
    public static extern void wasp_debug_print(byte* src, uint size);

    // Caller / time --------------------------------------------------------
    [DllImport("env", EntryPoint = "wasp_caller_size")]
    public static extern uint wasp_caller_size();

    [DllImport("env", EntryPoint = "wasp_caller_copy")]
    public static extern void wasp_caller_copy(byte* dst, uint offset, uint size);

    [DllImport("env", EntryPoint = "wasp_time")]
    public static extern ulong wasp_time();
}
