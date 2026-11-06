using System.Runtime.InteropServices;

namespace Wasp.IcCdk;

// Raw bindings for the IC system API (the `ic0` wasm import module).
// Curated from https://github.com/dfinity/cdk-rs ic0/ic0.txt. Naming
// follows the C convention used by the spec to keep cross-language
// references obvious.
//
// Marked `unsafe` and `internal` — user code calls higher-level helpers
// in MessageContext / StableMemory / Reply, not these directly.
public static unsafe partial class Ic0
{
    // ─── Caller / message arg ─────────────────────────────────────────────
    [DllImport("ic0", EntryPoint = "msg_arg_data_size")]
    [WasmImportLinkage]
    public static extern uint msg_arg_data_size();

    [DllImport("ic0", EntryPoint = "msg_arg_data_copy")]
    [WasmImportLinkage]
    public static extern void msg_arg_data_copy(nint dst, uint offset, uint size);

    [DllImport("ic0", EntryPoint = "msg_caller_size")]
    [WasmImportLinkage]
    public static extern uint msg_caller_size();

    [DllImport("ic0", EntryPoint = "msg_caller_copy")]
    [WasmImportLinkage]
    public static extern void msg_caller_copy(nint dst, uint offset, uint size);

    [DllImport("ic0", EntryPoint = "msg_method_name_size")]
    [WasmImportLinkage]
    public static extern uint msg_method_name_size();

    [DllImport("ic0", EntryPoint = "msg_method_name_copy")]
    [WasmImportLinkage]
    public static extern void msg_method_name_copy(nint dst, uint offset, uint size);

    [DllImport("ic0", EntryPoint = "accept_message")]
    [WasmImportLinkage]
    public static extern void accept_message();

    [DllImport("ic0", EntryPoint = "msg_deadline")]
    [WasmImportLinkage]
    public static extern ulong msg_deadline();

    // ─── Reply / reject ───────────────────────────────────────────────────
    [DllImport("ic0", EntryPoint = "msg_reply")]
    [WasmImportLinkage]
    public static extern void msg_reply();

    [DllImport("ic0", EntryPoint = "msg_reply_data_append")]
    [WasmImportLinkage]
    public static extern void msg_reply_data_append(nint src, nint size);

    [DllImport("ic0", EntryPoint = "msg_reject")]
    [WasmImportLinkage]
    public static extern void msg_reject(nint src, nint size);

    [DllImport("ic0", EntryPoint = "msg_reject_code")]
    [WasmImportLinkage]
    public static extern uint msg_reject_code();

    [DllImport("ic0", EntryPoint = "msg_reject_msg_size")]
    [WasmImportLinkage]
    public static extern uint msg_reject_msg_size();

    [DllImport("ic0", EntryPoint = "msg_reject_msg_copy")]
    [WasmImportLinkage]
    public static extern void msg_reject_msg_copy(nint dst, uint offset, uint size);

    [DllImport("ic0", EntryPoint = "trap")]
    [WasmImportLinkage]
    public static extern void trap(nint src, nint size);

    // ─── Canister identity ────────────────────────────────────────────────
    [DllImport("ic0", EntryPoint = "canister_self_size")]
    [WasmImportLinkage]
    public static extern uint canister_self_size();

    [DllImport("ic0", EntryPoint = "canister_self_copy")]
    [WasmImportLinkage]
    public static extern void canister_self_copy(nint dst, uint offset, uint size);

    [DllImport("ic0", EntryPoint = "canister_cycle_balance128")]
    [WasmImportLinkage]
    public static extern void canister_cycle_balance128(nint dst);

    [DllImport("ic0", EntryPoint = "canister_status")]
    [WasmImportLinkage]
    public static extern uint canister_status();

    [DllImport("ic0", EntryPoint = "canister_version")]
    [WasmImportLinkage]
    public static extern ulong canister_version();

    // ─── Inter-canister call ──────────────────────────────────────────────
    [DllImport("ic0", EntryPoint = "call_new")]
    [WasmImportLinkage]
    public static extern void call_new(
        nint callee_src, uint callee_size,
        nint name_src, uint name_size,
        nint reply_fun, nint reply_env,
        nint reject_fun, nint reject_env);

    [DllImport("ic0", EntryPoint = "call_on_cleanup")]
    [WasmImportLinkage]
    public static extern void call_on_cleanup(nint fun, nint env);

    [DllImport("ic0", EntryPoint = "call_data_append")]
    [WasmImportLinkage]
    public static extern void call_data_append(nint src, uint size);

    [DllImport("ic0", EntryPoint = "call_with_best_effort_response")]
    [WasmImportLinkage]
    public static extern void call_with_best_effort_response(uint timeout_seconds);

    [DllImport("ic0", EntryPoint = "call_cycles_add128")]
    [WasmImportLinkage]
    public static extern void call_cycles_add128(ulong amount_high, ulong amount_low);

    [DllImport("ic0", EntryPoint = "call_perform")]
    [WasmImportLinkage]
    public static extern uint call_perform();

    // ─── Stable memory (64-bit, the only flavour we use) ──────────────────
    [DllImport("ic0", EntryPoint = "stable64_size")]
    [WasmImportLinkage]
    public static extern ulong stable64_size();

    [DllImport("ic0", EntryPoint = "stable64_grow")]
    [WasmImportLinkage]
    public static extern ulong stable64_grow(ulong new_pages);

    [DllImport("ic0", EntryPoint = "stable64_write")]
    [WasmImportLinkage]
    public static extern void stable64_write(ulong offset, ulong src, ulong size);

    [DllImport("ic0", EntryPoint = "stable64_read")]
    [WasmImportLinkage]
    public static extern void stable64_read(ulong dst, ulong offset, ulong size);

    // ─── Time, randomness, certified data ─────────────────────────────────
    [DllImport("ic0", EntryPoint = "time")]
    [WasmImportLinkage]
    public static extern ulong time();

    [DllImport("ic0", EntryPoint = "global_timer_set")]
    [WasmImportLinkage]
    public static extern ulong global_timer_set(ulong timestamp);

    [DllImport("ic0", EntryPoint = "performance_counter")]
    [WasmImportLinkage]
    public static extern ulong performance_counter(uint counter_type);

    [DllImport("ic0", EntryPoint = "is_controller")]
    [WasmImportLinkage]
    public static extern uint is_controller(nint src, uint size);

    [DllImport("ic0", EntryPoint = "in_replicated_execution")]
    [WasmImportLinkage]
    public static extern uint in_replicated_execution();

    [DllImport("ic0", EntryPoint = "certified_data_set")]
    [WasmImportLinkage]
    public static extern void certified_data_set(nint src, uint size);

    [DllImport("ic0", EntryPoint = "data_certificate_present")]
    [WasmImportLinkage]
    public static extern uint data_certificate_present();

    [DllImport("ic0", EntryPoint = "data_certificate_size")]
    [WasmImportLinkage]
    public static extern uint data_certificate_size();

    [DllImport("ic0", EntryPoint = "data_certificate_copy")]
    [WasmImportLinkage]
    public static extern void data_certificate_copy(nint dst, uint offset, uint size);

    // ─── Logging ──────────────────────────────────────────────────────────
    [DllImport("ic0", EntryPoint = "debug_print")]
    [WasmImportLinkage]
    public static extern void debug_print(nint src, nint size);
}
