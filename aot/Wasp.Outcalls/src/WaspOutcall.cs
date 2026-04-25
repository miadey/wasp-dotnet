using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Wasp.IcCdk;

namespace Wasp.Outcalls;

// Outbound HTTPS calls from a canister.
//
// HTTP outcalls go through the IC's management canister
// (`aaaaa-aa::http_request`). The mechanism is the inter-canister-call
// API — call_new + call_data_append + call_perform. The current message
// returns immediately; the IC later invokes our reply or reject thunk
// in a fresh callback message context, where we look up the user's
// continuation by the `env` token we passed.
//
// Phase 3 v0.1 limitations:
//   - No transform function — responses must be byte-identical across
//     replicas (works on local single-replica dfx; fragile on mainnet
//     against time-varying APIs).
//   - One outcall per inbound update message; the user's continuation
//     must call Reply.Bytes/Reply.Text (or Reply.Trap) exactly once.
//   - Cycles are attached at a fixed amount per call (configurable
//     via Cycles property below).

public static class WaspOutcall
{
    /// <summary>Cycles attached to each outcall. The IC sets a minimum based
    /// on response size and subnet replication factor; ~100 G covers small
    /// GETs comfortably. Set higher for large responses or fiduciary subnets.</summary>
    public static ulong Cycles { get; set; } = 100_000_000_000UL;

    private static int _nextToken = 1;
    private static readonly Dictionary<int, Pending> _pending = new();
    private static readonly byte[] MethodNameBytes =
        System.Text.Encoding.UTF8.GetBytes("http_request");
    // Management canister principal is the empty principal.
    private static readonly byte[] ManagementCanister = Array.Empty<byte>();

    private sealed class Pending
    {
        public Pending(Action<OutcallResponse> onReply, Action<OutcallReject>? onReject)
        {
            OnReply = onReply;
            OnReject = onReject;
        }
        public Action<OutcallResponse> OnReply { get; }
        public Action<OutcallReject>? OnReject { get; }
    }

    public static void Get(
        string url,
        Action<OutcallResponse> onReply,
        Action<OutcallReject>? onReject = null,
        ulong? maxResponseBytes = null,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null)
        => Request(OutcallMethod.Get, url, body: null, onReply, onReject, maxResponseBytes, headers);

    public static void Post(
        string url,
        byte[] body,
        Action<OutcallResponse> onReply,
        Action<OutcallReject>? onReject = null,
        ulong? maxResponseBytes = null,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null)
        => Request(OutcallMethod.Post, url, body, onReply, onReject, maxResponseBytes, headers);

    public static unsafe void Request(
        OutcallMethod method,
        string url,
        byte[]? body,
        Action<OutcallResponse> onReply,
        Action<OutcallReject>? onReject,
        ulong? maxResponseBytes,
        IReadOnlyList<KeyValuePair<string, string>>? headers)
    {
        int token = System.Threading.Interlocked.Increment(ref _nextToken);
        _pending[token] = new Pending(onReply, onReject);

        byte[] args = CandidOutcall.EncodeRequest(
            url,
            method,
            headers ?? Array.Empty<KeyValuePair<string, string>>(),
            body,
            maxResponseBytes);

        // Function-table indices for our reply/reject thunks. Cast through
        // delegate* unmanaged so NativeAOT-LLVM emits proper indirect-call
        // table entries.
        delegate* unmanaged<int, void> replyFp = &OnReplyThunk;
        delegate* unmanaged<int, void> rejectFp = &OnRejectThunk;

        fixed (byte* methodNamePtr = MethodNameBytes)
        {
            // callee_size = 0 → management canister (empty principal).
            Ic0.call_new(
                callee_src: (nint)0,
                callee_size: 0,
                name_src: (nint)methodNamePtr,
                name_size: (uint)MethodNameBytes.Length,
                reply_fun: (nint)replyFp,
                reply_env: (nint)token,
                reject_fun: (nint)rejectFp,
                reject_env: (nint)token);
        }

        fixed (byte* p = args)
        {
            Ic0.call_data_append((nint)p, (uint)args.Length);
        }

        // Attach cycles. ic0.call_cycles_add128 takes (high, low).
        Ic0.call_cycles_add128(0UL, Cycles);

        uint rc = Ic0.call_perform();
        if (rc != 0)
        {
            _pending.Remove(token);
            Reply.Trap($"Wasp.Outcalls: call_perform returned {rc}");
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "wasp_outcall_reply")]
    public static void OnReplyThunk(int env)
    {
        if (!_pending.Remove(env, out var pending))
        {
            Reply.Trap($"Wasp.Outcalls: stale reply token {env}");
            return;
        }
        try
        {
            var argBytes = MessageContext.ArgData();
            var resp = CandidOutcall.DecodeResponse(argBytes);
            pending.OnReply(resp);
        }
        catch (Exception ex)
        {
            Reply.Trap("Wasp.Outcalls reply handler: " + ex.Message);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "wasp_outcall_reject")]
    public static void OnRejectThunk(int env)
    {
        if (!_pending.Remove(env, out var pending))
        {
            Reply.Trap($"Wasp.Outcalls: stale reject token {env}");
            return;
        }
        uint code = Ic0.msg_reject_code();
        uint size = Ic0.msg_reject_msg_size();
        var msgBytes = new byte[size];
        unsafe { fixed (byte* p = msgBytes) Ic0.msg_reject_msg_copy((nint)p, 0, size); }
        var msg = System.Text.Encoding.UTF8.GetString(msgBytes);
        if (pending.OnReject is not null)
        {
            pending.OnReject(new OutcallReject(code, msg));
        }
        else
        {
            Reply.Trap($"Wasp.Outcalls rejected (code {code}): {msg}");
        }
    }
}
