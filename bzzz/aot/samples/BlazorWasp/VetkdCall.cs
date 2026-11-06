using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Wasp.IcCdk;

namespace WaspSample.BlazorWasp;

// Generalized async call to the IC management canister (aaaaa-aa), modeled on
// Wasp.Outcalls/WaspOutcall but self-contained in the app assembly (its own
// _pending table + its own uniquely-named reply/reject thunks) so it composes
// cleanly with the HTTP-outcall path and needs no new ProjectReference.
//
// Used for vetKeys (vetKD): vetkd_public_key / vetkd_derive_key. As with any
// inter-canister call, the inbound update message returns immediately and the
// IC invokes our reply/reject thunk in a FRESH callback message, where the
// continuation (looked up by the int env token) MUST emit the canister reply
// exactly once (Reply.Bytes / Reply.Trap).
internal static class VetkdCall
{
    private static int _nextToken = 1;
    private static readonly Dictionary<int, Pending> _pending = new();

    private sealed class Pending
    {
        public Pending(Action<byte[]> onReply, Action<uint, string>? onReject)
        { OnReply = onReply; OnReject = onReject; }
        public Action<byte[]> OnReply { get; }
        public Action<uint, string>? OnReject { get; }
    }

    /// <summary>Call a management-canister method.</summary>
    /// <param name="method">e.g. "vetkd_derive_key" / "vetkd_public_key".</param>
    /// <param name="candidArgs">already-encoded Candid argument blob.</param>
    /// <param name="cycles">cycles to attach (0 for free methods).</param>
    /// <param name="onReply">receives the raw Candid reply arg bytes; must reply.</param>
    public static unsafe void Call(string method, byte[] candidArgs, ulong cycles,
        Action<byte[]> onReply, Action<uint, string>? onReject = null)
    {
        int token = System.Threading.Interlocked.Increment(ref _nextToken);
        _pending[token] = new Pending(onReply, onReject);

        var methodBytes = System.Text.Encoding.UTF8.GetBytes(method);

        // delegate* unmanaged so NativeAOT-LLVM emits real indirect-call table
        // entries for the IC to invoke as the reply/reject callbacks.
        delegate* unmanaged<int, void> replyFp = &OnReplyThunk;
        delegate* unmanaged<int, void> rejectFp = &OnRejectThunk;

        fixed (byte* m = methodBytes)
        {
            // callee_size = 0 → management canister (empty principal).
            Ic0.call_new(
                callee_src: (nint)0, callee_size: 0,
                name_src: (nint)m, name_size: (uint)methodBytes.Length,
                reply_fun: (nint)replyFp, reply_env: (nint)token,
                reject_fun: (nint)rejectFp, reject_env: (nint)token);
        }

        fixed (byte* p = candidArgs)
        {
            Ic0.call_data_append((nint)p, (uint)candidArgs.Length);
        }

        if (cycles != 0) Ic0.call_cycles_add128(0UL, cycles);

        uint rc = Ic0.call_perform();
        if (rc != 0)
        {
            _pending.Remove(token);
            Reply.Trap($"VetkdCall: call_perform({method}) returned {rc}");
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "wasp_mgmt_reply")]
    public static void OnReplyThunk(int env)
    {
        if (!_pending.Remove(env, out var pending))
        { Reply.Trap($"VetkdCall: stale reply token {env}"); return; }
        try { pending.OnReply(MessageContext.ArgData()); }
        catch (Exception ex) { Reply.Trap("VetkdCall reply handler: " + ex.Message); }
    }

    [UnmanagedCallersOnly(EntryPoint = "wasp_mgmt_reject")]
    public static void OnRejectThunk(int env)
    {
        if (!_pending.Remove(env, out var pending))
        { Reply.Trap($"VetkdCall: stale reject token {env}"); return; }
        uint code = Ic0.msg_reject_code();
        uint size = Ic0.msg_reject_msg_size();
        var msgBytes = new byte[size];
        unsafe { fixed (byte* p = msgBytes) Ic0.msg_reject_msg_copy((nint)p, 0, size); }
        var msg = System.Text.Encoding.UTF8.GetString(msgBytes);
        if (pending.OnReject is not null) pending.OnReject(code, msg);
        else Reply.Trap($"VetkdCall rejected (code {code}): {msg}");
    }
}
