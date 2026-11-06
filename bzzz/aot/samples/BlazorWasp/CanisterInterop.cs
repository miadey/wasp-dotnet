using System.Runtime.InteropServices;
using Wasp.IcCdk;

namespace WaspSample.BlazorWasp;

public static class CanisterInterop
{
    // The IC invokes canister_inspect_message for EVERY update ingress message
    // before it is executed. Queries are NOT inspected, so all read paths
    // (http_request query, etc.) are completely unaffected by this gate.
    //
    // Because we export this entrypoint, the IC requires us to explicitly call
    // accept_message() for any update ingress that should run — so we MUST accept
    // every legitimate message. The only update method on this canister is
    // http_request_update (see blazorwasp.did), so the size check below cannot
    // reject anything real.
    //
    // This gate does ONE thing: reject absurdly oversized argument blobs — a free,
    // pre-execution DoS vector — before we pay to decode them. 3 MiB comfortably
    // covers the largest legitimate write (a max-size image post).
    //
    // It FAILS OPEN on any error: if inspection ever throws, we still try to
    // accept the message, so an inspect bug can never brick all writes.
    //
    // Per-principal rate limiting is enforced in the request handlers
    // (RateService), NOT here — inspect runs without replicated state guarantees
    // and is the wrong place for stateful policy.
    [UnmanagedCallersOnly(EntryPoint = "canister_inspect_message")]
    public static void InspectMessage()
    {
        try
        {
            const uint MaxIngressArg = 3u * 1024u * 1024u; // 3 MiB
            if (Ic0.msg_arg_data_size() > MaxIngressArg)
                return; // oversized — silently drop (do not accept)

            Ic0.accept_message();
        }
        catch
        {
            // Fail open: never let an inspect bug brick legitimate writes.
            try { Ic0.accept_message(); } catch { }
        }
    }
}
