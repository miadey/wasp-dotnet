using System;
using System.Collections.Generic;
using System.Text;

namespace WaspSample.BlazorWasp;

// Hand-rolled Candid codec for the IC management canister (aaaaa-aa) vetKD API.
// Mirrors the proven style of Wasp.Outcalls/CandidOutcall.cs. AOT-safe:
// no RNG, no System.Security.Cryptography, no reflection.
//
// vetkd_public_key : (record { canister_id: opt principal; context: blob;
//     key_id: record { curve: variant { bls12_381_g2 }; name: text } })
//   -> (record { public_key: blob });
// vetkd_derive_key : (record { input: blob; context: blob;
//     transport_public_key: blob;
//     key_id: record { curve: variant { bls12_381_g2 }; name: text } })
//   -> (record { encrypted_key: blob });
//
// Field-name hashes verified against the constants in
// Wasp.Outcalls/CandidOutcall.cs (e.g. name == 1224700491). Candid emits record
// fields in ASCENDING field-hash order in BOTH the type table and the value
// section, so the orderings below are load-bearing.
internal static class VetkdCandid
{
    private const byte CODE_NULL      = 0x7F;
    private const byte CODE_NAT8      = 0x7B;
    private const byte CODE_TEXT      = 0x71;
    private const byte CODE_OPT       = 0x6E;
    private const byte CODE_VEC       = 0x6D;
    private const byte CODE_RECORD    = 0x6C;
    private const byte CODE_VARIANT   = 0x6B;
    private const byte CODE_PRINCIPAL = 0x68;

    // Candid field-name hashes: H = (H*223 + c) mod 2^32, H starts 0.
    private const uint H_encrypted_key        = 58727076;     // 0x03801AA4
    private const uint H_context              = 427267567;    // 0x197795EF
    private const uint H_public_key           = 881350601;    // 0x348857C9
    private const uint H_key_id               = 1072395707;   // 0x3FEB75BB
    private const uint H_peer                 = 1247076770;   // 0x4A53E1A2
    private const uint H_name                 = 1224700491;   // 0x48FF724B
    private const uint H_canister_id          = 1313628723;   // 0x4E4C6233
    private const uint H_curve                = 1314409647;   // 0x4E584CAF
    private const uint H_input                = 3189621770;   // 0xBE1DC40A
    private const uint H_transport_public_key = 3581318111;   // 0xD57693DF
    // variant arm bls12_381_g2 (used only in the type table, not the value).
    private const uint H_bls12_381_g2         = 2825341939;   // 0xA8696EB3

    // ── derive args: record { context, key_id, input, transport_public_key } ──
    public static byte[] EncodeDeriveArgs(
        byte[] input, byte[] context, byte[] transportPublicKey, string keyName)
    {
        var buf = new List<byte>(
            96 + input.Length + context.Length + transportPublicKey.Length);
        buf.Add(0x44); buf.Add(0x49); buf.Add(0x44); buf.Add(0x4C); // DIDL

        // Type table: 4 entries.
        WriteUleb(buf, 4);
        // T0: vec nat8 (blob)
        buf.Add(CODE_VEC); buf.Add(CODE_NAT8);
        // T1: variant { bls12_381_g2 } -> null  (1 arm)
        buf.Add(CODE_VARIANT); WriteUleb(buf, 1);
        WriteUleb(buf, H_bls12_381_g2); buf.Add(CODE_NULL);
        // T2: record key_id { name: text, curve: T1 }   (ascending: name, curve)
        buf.Add(CODE_RECORD); WriteUleb(buf, 2);
        WriteUleb(buf, H_name);  buf.Add(CODE_TEXT);
        WriteUleb(buf, H_curve); WriteUleb(buf, 1);
        // T3: record args { context: T0, key_id: T2, input: T0, transport: T0 }
        // ascending: context, key_id, input, transport_public_key
        buf.Add(CODE_RECORD); WriteUleb(buf, 4);
        WriteUleb(buf, H_context);              WriteUleb(buf, 0);
        WriteUleb(buf, H_key_id);               WriteUleb(buf, 2);
        WriteUleb(buf, H_input);                WriteUleb(buf, 0);
        WriteUleb(buf, H_transport_public_key); WriteUleb(buf, 0);

        // Value section: 1 value of type T3.
        WriteUleb(buf, 1); WriteUleb(buf, 3);
        // Values in ascending-hash order.
        WriteBlob(buf, context);                  // context
        WriteKeyId(buf, keyName);                 // key_id { name, curve }
        WriteBlob(buf, input);                    // input
        WriteBlob(buf, transportPublicKey);       // transport_public_key
        return buf.ToArray();
    }

    // ── public_key args: record { context, key_id, canister_id } ──
    // canister_id = opt principal = None.
    public static byte[] EncodePublicKeyArgs(byte[] context, string keyName)
    {
        var buf = new List<byte>(96 + context.Length);
        buf.Add(0x44); buf.Add(0x49); buf.Add(0x44); buf.Add(0x4C); // DIDL

        // Type table: 5 entries.
        WriteUleb(buf, 5);
        // T0: vec nat8 (blob)
        buf.Add(CODE_VEC); buf.Add(CODE_NAT8);
        // T1: variant { bls12_381_g2 } -> null
        buf.Add(CODE_VARIANT); WriteUleb(buf, 1);
        WriteUleb(buf, H_bls12_381_g2); buf.Add(CODE_NULL);
        // T2: record key_id { name: text, curve: T1 }
        buf.Add(CODE_RECORD); WriteUleb(buf, 2);
        WriteUleb(buf, H_name);  buf.Add(CODE_TEXT);
        WriteUleb(buf, H_curve); WriteUleb(buf, 1);
        // T3: opt principal
        buf.Add(CODE_OPT); buf.Add(CODE_PRINCIPAL);
        // T4: record args { context: T0, key_id: T2, canister_id: T3 }
        // ascending: context, key_id, canister_id
        buf.Add(CODE_RECORD); WriteUleb(buf, 3);
        WriteUleb(buf, H_context);     WriteUleb(buf, 0);
        WriteUleb(buf, H_key_id);      WriteUleb(buf, 2);
        WriteUleb(buf, H_canister_id); WriteUleb(buf, 3);

        // Value section: 1 value of type T4.
        WriteUleb(buf, 1); WriteUleb(buf, 4);
        WriteBlob(buf, context);     // context
        WriteKeyId(buf, keyName);    // key_id { name, curve }
        buf.Add(0x00);               // canister_id = None
        return buf.ToArray();
    }

    // ── reply decoders: single-field record { blob } ──
    public static byte[] DecodeDeriveReply(byte[] reply) => ReadFirstBlob(reply);     // encrypted_key
    public static byte[] DecodePublicKeyReply(byte[] reply) => ReadFirstBlob(reply);  // public_key

    // ── inbound vetkd_derive arg: record { peer: text; transport_public_key: blob } ──
    // Values are emitted in ascending field-hash order: peer (1247076770) then
    // transport_public_key (3581318111), so read text then blob.
    public static (string peer, byte[] tpk) ReadDeriveArg(byte[] incomingArgData)
    {
        var r = new RawReader(incomingArgData);
        r.ReadMagic();
        ulong typeCount = r.ReadUleb();
        for (ulong i = 0; i < typeCount; i++) r.SkipTypeEntry();
        ulong valueCount = r.ReadUleb();
        for (ulong i = 0; i < valueCount; i++) r.ReadSleb();
        string peer = r.ReadText();
        byte[] tpk = r.ReadBlob();
        return (peer, tpk);
    }

    // ── value helpers ──
    private static void WriteKeyId(List<byte> buf, string keyName)
    {
        // key_id value: fields in ascending order name, curve.
        WriteText(buf, keyName);   // name: text
        WriteUleb(buf, 0);         // curve: variant arm index 0 (bls12_381_g2),
                                   //        null payload => nothing follows.
    }

    private static void WriteBlob(List<byte> buf, byte[] bytes)
    {
        WriteUleb(buf, (ulong)bytes.Length);
        for (int i = 0; i < bytes.Length; i++) buf.Add(bytes[i]);
    }

    private static void WriteText(List<byte> buf, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        WriteUleb(buf, (ulong)b.Length);
        for (int i = 0; i < b.Length; i++) buf.Add(b[i]);
    }

    private static void WriteUleb(List<byte> sink, ulong value)
    {
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) b |= 0x80;
            sink.Add(b);
        } while (value != 0);
    }

    // Skip DIDL magic + type table + value-type refs, then read first blob.
    private static byte[] ReadFirstBlob(ReadOnlySpan<byte> data)
    {
        var r = new RawReader(data);
        r.ReadMagic();
        ulong typeCount = r.ReadUleb();
        for (ulong i = 0; i < typeCount; i++) r.SkipTypeEntry();
        ulong valueCount = r.ReadUleb();
        for (ulong i = 0; i < valueCount; i++) r.ReadSleb();
        return r.ReadBlob();
    }

    private ref struct RawReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;
        public RawReader(ReadOnlySpan<byte> data) { _data = data; _pos = 0; }

        public void ReadMagic()
        {
            if (_data.Length < 4 || _data[0] != 0x44 || _data[1] != 0x49 ||
                _data[2] != 0x44 || _data[3] != 0x4C)
                throw new InvalidOperationException("Candid: missing DIDL magic");
            _pos = 4;
        }

        public byte[] ReadBlob()
        {
            ulong len = ReadUleb();
            var dst = _data.Slice(_pos, (int)len).ToArray();
            _pos += (int)len;
            return dst;
        }

        public string ReadText()
        {
            ulong len = ReadUleb();
            var s = Encoding.UTF8.GetString(_data.Slice(_pos, (int)len));
            _pos += (int)len;
            return s;
        }

        public ulong ReadUleb()
        {
            ulong result = 0; int shift = 0;
            while (true)
            {
                byte b = _data[_pos++];
                result |= ((ulong)(b & 0x7F)) << shift;
                if ((b & 0x80) == 0) return result;
                shift += 7;
            }
        }

        public long ReadSleb()
        {
            long result = 0; int shift = 0; byte b;
            do
            {
                b = _data[_pos++];
                result |= ((long)(b & 0x7F)) << shift;
                shift += 7;
            } while ((b & 0x80) != 0);
            if (shift < 64 && (b & 0x40) != 0) result |= -1L << shift;
            return result;
        }

        public void SkipTypeEntry()
        {
            long opcode = ReadSleb();
            switch (opcode)
            {
                case -18: ReadSleb(); break;                 // opt T
                case -19: ReadSleb(); break;                 // vec T
                case -20:                                    // record
                case -21:                                    // variant
                {
                    ulong fields = ReadUleb();
                    for (ulong f = 0; f < fields; f++) { ReadUleb(); ReadSleb(); }
                    break;
                }
                case -22:                                    // func ref
                {
                    ulong args = ReadUleb();
                    for (ulong a = 0; a < args; a++) ReadSleb();
                    ulong results = ReadUleb();
                    for (ulong rr = 0; rr < results; rr++) ReadSleb();
                    ulong anns = ReadUleb();
                    _pos += (int)anns;
                    break;
                }
                case -23:                                    // service ref
                {
                    ulong methods = ReadUleb();
                    for (ulong m = 0; m < methods; m++)
                    { ulong nl = ReadUleb(); _pos += (int)nl; ReadSleb(); }
                    break;
                }
                default: break;                              // primitive
            }
        }
    }
}
