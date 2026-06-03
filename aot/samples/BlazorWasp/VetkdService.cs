using System;
using System.Runtime.InteropServices;
using Wasp.IcCdk;

namespace WaspSample.BlazorWasp;

// vetKeys (vetKD) canister methods for end-to-end-encrypted 1:1 DMs.
//
// These are hand-written canister UPDATE entrypoints (no [CanisterUpdate]
// source generator needed — the BlazorWasp assembly is already in
// UnmanagedEntryPointsAssembly, and icp-publish renames "canister_update__NAME"
// to the IC export "canister_update NAME", exactly as the existing
// http_request_update / canister_inspect_message entrypoints are handled).
// They issue an async call to the IC management canister (aaaaa-aa) and reply
// from its callback — the request message returns immediately. The synchronous
// ASP.NET http_request pipeline cannot await an inter-canister reply (it lands
// in a later message), which is why DM key derivation lives in dedicated methods
// the JS client calls directly via the @dfinity agent over the same signed
// transport.
//
// TRUST MODEL: vetkd_derive binds the derived key to input = the CANONICAL DM
// conversation id (ordinal-sorted pair of principal texts), built ON THE
// CANISTER from msg_caller + the peer the client names. A caller can therefore
// only ever derive the key for a conversation it is itself a participant of —
// the participation gate is structural. The canister never sees the derived key
// (vetKD returns it ENCRYPTED to the caller's transport key) nor any plaintext.
public static class VetkdService
{
    // Domain separator for ALL DM key derivation. MUST be byte-identical on the
    // browser side (same context for vetkd_pubkey and vetkd_derive, or
    // decryptAndVerify fails).
    private static readonly byte[] DmContext =
        System.Text.Encoding.UTF8.GetBytes("wasp-bzzz/dm/v1");

    // vetKD key name. "test_key_1" = mainnet TEST key — available now, but
    // DFINITY labels it non-production and MAY rotate/wipe it (which would
    // orphan anything encrypted under it). Switch to "key_1" for production once
    // you accept the ~26G-cycles-per-derive cost and confirm key_1 is enabled on
    // razoronic's subnet. Local dfx uses "dfx_test_key".
    private const string KeyName = "test_key_1";

    // Cycles attached to vetkd_derive_key (test_key_1 ~10G, key_1 ~26G); 30G
    // covers both with margin. vetkd_public_key is free (0 cycles).
    private const ulong DeriveCycles = 30_000_000_000UL;

    // MUST match DmService.KeySep (the unit separator used in CanonicalKey) so
    // the conversation id is identical to the DM thread's canonical pairing.
    private const char KeySep = '';

    // canister_update vetkd_pubkey : () -> (record { public_key : blob })
    // Returns the app's master vetKD public key for the DM context. Public and
    // identical for everyone; the client caches it and uses it to verify derived
    // keys. (No auth required — it leaks nothing.)
    [UnmanagedCallersOnly(EntryPoint = "canister_update__vetkd_pubkey")]
    public static void VetkdPubkey()
    {
        try
        {
            var args = VetkdCandid.EncodePublicKeyArgs(DmContext, KeyName);
            VetkdCall.Call("vetkd_public_key", args, cycles: 0UL,
                onReply: raw => Reply.Bytes(Candid.EncodeBlob(VetkdCandid.DecodePublicKeyReply(raw))),
                onReject: (c, m) => Reply.Trap($"vetkd_public_key reject {c}: {m}"));
        }
        catch (Exception ex) { Reply.Trap("vetkd_pubkey: " + ex.Message); }
    }

    // canister_update vetkd_derive :
    //   (record { peer : text; transport_public_key : blob })
    //   -> (record { encrypted_key : blob })
    // Derives the per-conversation key for {msg_caller, peer}. Signed-only.
    [UnmanagedCallersOnly(EntryPoint = "canister_update__vetkd_derive")]
    public static void VetkdDerive()
    {
        try
        {
            byte[] caller = MessageContext.CallerPrincipal();
            if (AdminService.IsAnonymous(caller)) { Reply.Trap("vetkd_derive: sign in"); return; }

            var (peerText, tpk) = VetkdCandid.ReadDeriveArg(MessageContext.ArgData());
            if (tpk is null || tpk.Length == 0) { Reply.Trap("vetkd_derive: missing transport key"); return; }

            string me = AdminService.ToText(caller);
            string? peer = NormalizePrincipal(peerText);
            if (peer is null) { Reply.Trap("vetkd_derive: bad peer principal"); return; }
            if (string.Equals(me, peer, StringComparison.Ordinal)) { Reply.Trap("vetkd_derive: no self-conversation"); return; }

            // Canonical conversation id — identical for both participants, built
            // ONLY from the authenticated caller + named peer, so a caller can
            // only ever derive a key for a conversation it belongs to.
            string convId = string.CompareOrdinal(me, peer) <= 0
                ? me + KeySep + peer
                : peer + KeySep + me;
            byte[] input = System.Text.Encoding.UTF8.GetBytes(convId);

            var args = VetkdCandid.EncodeDeriveArgs(input, DmContext, tpk, KeyName);
            VetkdCall.Call("vetkd_derive_key", args, cycles: DeriveCycles,
                onReply: raw => Reply.Bytes(Candid.EncodeBlob(VetkdCandid.DecodeDeriveReply(raw))),
                onReject: (c, m) => Reply.Trap($"vetkd_derive_key reject {c}: {m}"));
        }
        catch (Exception ex) { Reply.Trap("vetkd_derive: " + ex.Message); }
    }

    // Normalize a principal text via AdminService round-trip (CRC-checked);
    // returns null if invalid/anonymous. Mirrors DmService's peer normalization
    // so the conversation id matches the DM thread key.
    private static string? NormalizePrincipal(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            var blob = AdminService.FromText(raw);
            if (AdminService.IsAnonymous(blob)) return null;
            return AdminService.ToText(blob);
        }
        catch { return null; }
    }
}
