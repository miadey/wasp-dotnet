using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Wasp.WebSockets;

// Candid codecs for every top-level type the four ws_* methods use.
// Hand-rolled — no general-purpose Candid framework here. Each method
// builds its type table inline and emits the value section in matching
// field order.
//
// Field-name → hash table (computed once via the standard Candid
// hash):
//   Ok=17724  Err=3456837  key=5343647  msg=5446209
//   sequence_num=352253448  content=427265337  OpenMessage=428171005
//   gateway_principal=647083891  messages=889051340
//   client_key=1025972843  cert=1102915300
//   ClosedByApplication=1198956877  tree=1292081502
//   client_principal=1384677498  InvalidServiceMessage=1409524553
//   KeepAliveMessage=2525358527  nonce=2680573167
//   WrongSequenceNumber=2743637463  timestamp=2781795542
//   last_incoming_sequence_num=2804597848  is_service_message=2815490792
//   CloseMessage=2918480143  AckMessage=3049003934
//   KeepAliveTimeout=3580612249  is_end_of_queue=3733147138
//   client_nonce=3921324219  reason=4238151620

internal static class CandidWs
{
    private const byte CODE_BOOL    = 0x7E;
    private const byte CODE_NAT8    = 0x7B;
    private const byte CODE_NAT64   = 0x78;
    private const byte CODE_TEXT    = 0x71;
    private const byte CODE_NULL    = 0x7F;
    private const byte CODE_OPT     = 0x6E;
    private const byte CODE_VEC     = 0x6D;
    private const byte CODE_RECORD  = 0x6C;
    private const byte CODE_VARIANT = 0x6B;
    private const byte CODE_PRINCIPAL = 0x68; // -24

    private const uint H_Ok                       = 17724;
    private const uint H_Err                      = 3456837;
    private const uint H_key                      = 5343647;
    private const uint H_msg                      = 5446209;
    private const uint H_sequence_num             = 352253448;
    private const uint H_content                  = 427265337;
    private const uint H_OpenMessage              = 428171005;
    private const uint H_gateway_principal        = 647083891;
    private const uint H_messages                 = 889051340;
    private const uint H_client_key               = 1025972843;
    private const uint H_cert                     = 1102915300;
    private const uint H_ClosedByApplication      = 1198956877;
    private const uint H_tree                     = 1292081502;
    private const uint H_client_principal         = 1384677498;
    private const uint H_InvalidServiceMessage    = 1409524553;
    private const uint H_KeepAliveMessage         = 2525358527;
    private const uint H_nonce                    = 2680573167;
    private const uint H_WrongSequenceNumber      = 2743637463;
    private const uint H_timestamp                = 2781795542;
    private const uint H_last_incoming_seq_num    = 2804597848;
    private const uint H_is_service_message       = 2815490792;
    private const uint H_CloseMessage             = 2918480143;
    private const uint H_AckMessage               = 3049003934;
    private const uint H_KeepAliveTimeout         = 3580612249;
    private const uint H_is_end_of_queue          = 3733147138;
    private const uint H_client_nonce             = 3921324219;
    private const uint H_reason                   = 4238151620;

    // ─── Decoders for inbound argument types ─────────────────────────────
    public static (ulong clientNonce, byte[] gatewayPrincipal) DecodeWsOpenArgs(ReadOnlySpan<byte> arg)
    {
        var r = OpenReader(arg);
        // Field IDs sorted: gateway_principal (647…), client_nonce (3921…)
        byte[] gateway = r.ReadPrincipal();
        ulong nonce = r.ReadNat64();
        return (nonce, gateway);
    }

    public static ClientKey DecodeWsCloseArgs(ReadOnlySpan<byte> arg)
    {
        var r = OpenReader(arg);
        // CanisterWsCloseArguments has one field: client_key (1025972843)
        return ReadClientKey(ref r);
    }

    public static WebsocketMessage DecodeWsMessageArgs(ReadOnlySpan<byte> arg)
    {
        var r = OpenReader(arg);
        // CanisterWsMessageArguments has one field: msg (5446209)
        return ReadWebsocketMessage(ref r);
    }

    public static ulong DecodeWsGetMessagesArgs(ReadOnlySpan<byte> arg)
    {
        var r = OpenReader(arg);
        // CanisterWsGetMessagesArguments has one field: nonce (2680573167)
        return r.ReadNat64();
    }

    // ─── Encoders for outbound result types ──────────────────────────────
    public static byte[] EncodeUnitOk()
    {
        // Result<(), String> = variant { Ok : null; Err : text }
        // Type table (1 entry):
        //   0: variant { Ok=17724:null, Err=3456837:text }   (Ok < Err)
        var b = new List<byte>(32);
        Magic(b);
        WriteUleb(b, 1);                   // 1 type-table entry
        b.Add(CODE_VARIANT); WriteUleb(b, 2);
        WriteUleb(b, H_Ok);  b.Add(CODE_NULL);
        WriteUleb(b, H_Err); b.Add(CODE_TEXT);
        // value types: 1 value of type 0
        WriteUleb(b, 1);
        WriteUleb(b, 0);
        // value: variant tag 0 (Ok), no value bytes (null type)
        WriteUleb(b, 0);
        return b.ToArray();
    }

    public static byte[] EncodeUnitErr(string message)
    {
        var b = new List<byte>(32 + message.Length);
        Magic(b);
        WriteUleb(b, 1);
        b.Add(CODE_VARIANT); WriteUleb(b, 2);
        WriteUleb(b, H_Ok);  b.Add(CODE_NULL);
        WriteUleb(b, H_Err); b.Add(CODE_TEXT);
        WriteUleb(b, 1);
        WriteUleb(b, 0);
        WriteUleb(b, 1); // variant tag 1 = Err
        WriteText(b, message);
        return b.ToArray();
    }

    /// <summary>
    /// Encode CanisterWsGetMessagesResult = variant { Ok : CanisterOutputCertifiedMessages; Err : text }.
    /// </summary>
    public static byte[] EncodeGetMessagesOk(CanisterOutputCertifiedMessages msgs)
    {
        // We need a multi-entry type table because we have nested records:
        //   0: record { client_principal: principal, client_nonce: nat64 }            (ClientKey)
        //   1: vec(nat8)                                                              (blob)
        //   2: record { client_key: 0, key: text, content: 1 }                        (CanisterOutputMessage)
        //   3: vec(2)                                                                 (vec messages)
        //   4: record { messages: 3, cert: 1, tree: 1, is_end_of_queue: bool }        (CanisterOutputCertifiedMessages)
        //   5: variant { Ok : 4, Err : text }
        var b = new List<byte>(256 + EstimateMessagesSize(msgs));
        Magic(b);
        WriteUleb(b, 6);

        // 0: ClientKey record
        // Field IDs sorted: client_principal(1384677498), client_nonce(3921324219)
        b.Add(CODE_RECORD); WriteUleb(b, 2);
        WriteUleb(b, H_client_principal); b.Add(CODE_PRINCIPAL);
        WriteUleb(b, H_client_nonce);     b.Add(CODE_NAT64);

        // 1: vec(nat8)  (blob)
        b.Add(CODE_VEC); b.Add(CODE_NAT8);

        // 2: CanisterOutputMessage record
        // Field IDs sorted: key(5343647), content(427265337), client_key(1025972843)
        // wait — sort by hash ascending: key(5343647) < content(427265337)? 5343647 < 427265337 yes.
        // 5343647 < 427265337 < 1025972843
        b.Add(CODE_RECORD); WriteUleb(b, 3);
        WriteUleb(b, H_key);        b.Add(CODE_TEXT);
        WriteUleb(b, H_content);    WriteUleb(b, 1);
        WriteUleb(b, H_client_key); WriteUleb(b, 0);

        // 3: vec(2)
        b.Add(CODE_VEC); WriteUleb(b, 2);

        // 4: CanisterOutputCertifiedMessages record
        // Field IDs sorted: messages(889051340), cert(1102915300), tree(1292081502), is_end_of_queue(3733147138)
        b.Add(CODE_RECORD); WriteUleb(b, 4);
        WriteUleb(b, H_messages);        WriteUleb(b, 3);
        WriteUleb(b, H_cert);            WriteUleb(b, 1);
        WriteUleb(b, H_tree);            WriteUleb(b, 1);
        WriteUleb(b, H_is_end_of_queue); b.Add(CODE_BOOL);

        // 5: variant { Ok : 4, Err : text }
        b.Add(CODE_VARIANT); WriteUleb(b, 2);
        WriteUleb(b, H_Ok);  WriteUleb(b, 4);
        WriteUleb(b, H_Err); b.Add(CODE_TEXT);

        // value types: 1 value of type 5
        WriteUleb(b, 1);
        WriteUleb(b, 5);

        // value: variant tag 0 (Ok), then CanisterOutputCertifiedMessages (type 4)
        // Fields in sorted order: messages, cert, tree, is_end_of_queue
        WriteUleb(b, 0);
        // messages: vec of records
        WriteUleb(b, (ulong)msgs.Messages.Count);
        foreach (var m in msgs.Messages)
        {
            // CanisterOutputMessage record: key, content, client_key
            WriteText(b, m.Key);
            WriteBlob(b, m.Content);
            WriteClientKeyValue(b, m.ClientKey);
        }
        WriteBlob(b, msgs.Cert);
        WriteBlob(b, msgs.Tree);
        b.Add((byte)(msgs.IsEndOfQueue ? 1 : 0));

        return b.ToArray();
    }

    public static byte[] EncodeGetMessagesErr(string message)
    {
        // Simpler shape — same variant but Err arm only.
        // Reuse the full type table to be safe (matches the gateway's expected type).
        var msgs = new CanisterOutputCertifiedMessages(
            Array.Empty<CanisterOutputMessage>(), Array.Empty<byte>(), Array.Empty<byte>(), true);
        // Build same type table but emit Err variant.
        var b = new List<byte>(64 + message.Length);
        Magic(b);
        WriteUleb(b, 6);
        // (Same as EncodeGetMessagesOk for type table 0..5)
        b.Add(CODE_RECORD); WriteUleb(b, 2);
        WriteUleb(b, H_client_principal); b.Add(CODE_PRINCIPAL);
        WriteUleb(b, H_client_nonce);     b.Add(CODE_NAT64);
        b.Add(CODE_VEC); b.Add(CODE_NAT8);
        b.Add(CODE_RECORD); WriteUleb(b, 3);
        WriteUleb(b, H_key);        b.Add(CODE_TEXT);
        WriteUleb(b, H_content);    WriteUleb(b, 1);
        WriteUleb(b, H_client_key); WriteUleb(b, 0);
        b.Add(CODE_VEC); WriteUleb(b, 2);
        b.Add(CODE_RECORD); WriteUleb(b, 4);
        WriteUleb(b, H_messages);        WriteUleb(b, 3);
        WriteUleb(b, H_cert);            WriteUleb(b, 1);
        WriteUleb(b, H_tree);            WriteUleb(b, 1);
        WriteUleb(b, H_is_end_of_queue); b.Add(CODE_BOOL);
        b.Add(CODE_VARIANT); WriteUleb(b, 2);
        WriteUleb(b, H_Ok);  WriteUleb(b, 4);
        WriteUleb(b, H_Err); b.Add(CODE_TEXT);

        WriteUleb(b, 1);
        WriteUleb(b, 5);
        WriteUleb(b, 1); // Err
        WriteText(b, message);
        return b.ToArray();
    }

    // ─── CBOR (de)serialization for WebsocketMessage ─────────────────────
    public static byte[] EncodeWebsocketMessageCbor(WebsocketMessage msg)
    {
        var w = new Cbor.Writer();
        w.SelfDescribeTag();
        w.WriteMapHeader(5);
        // serde_cbor with serde-derive emits fields in declaration order.
        // ic-websocket-cdk's WebsocketMessage declares them as:
        //   client_key, sequence_num, timestamp, is_service_message, content
        w.WriteTextString("client_key");
        w.WriteMapHeader(2);
        w.WriteTextString("client_principal");
        w.WriteByteString(msg.ClientKey.ClientPrincipal);
        w.WriteTextString("client_nonce");
        w.WriteUInt(msg.ClientKey.ClientNonce);

        w.WriteTextString("sequence_num");
        w.WriteUInt(msg.SequenceNum);
        w.WriteTextString("timestamp");
        w.WriteUInt(msg.Timestamp);
        w.WriteTextString("is_service_message");
        w.WriteBool(msg.IsServiceMessage);
        w.WriteTextString("content");
        w.WriteByteString(msg.Content);

        return w.ToArray();
    }

    // ─── Internal Candid helpers ─────────────────────────────────────────
    private static void Magic(List<byte> b)
    {
        b.Add(0x44); b.Add(0x49); b.Add(0x44); b.Add(0x4C);
    }

    private static void WriteUleb(List<byte> sink, ulong value)
    {
        do
        {
            byte by = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) by |= 0x80;
            sink.Add(by);
        } while (value != 0);
    }

    private static void WriteText(List<byte> sink, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        WriteUleb(sink, (ulong)bytes.Length);
        for (int i = 0; i < bytes.Length; i++) sink.Add(bytes[i]);
    }

    private static void WriteBlob(List<byte> sink, byte[] bytes)
    {
        WriteUleb(sink, (ulong)bytes.Length);
        for (int i = 0; i < bytes.Length; i++) sink.Add(bytes[i]);
    }

    private static void WritePrincipal(List<byte> sink, byte[] principal)
    {
        // Candid principal value: 1-byte form tag (0x01 = transparent),
        // then ULEB length, then bytes.
        sink.Add(0x01);
        WriteUleb(sink, (ulong)principal.Length);
        for (int i = 0; i < principal.Length; i++) sink.Add(principal[i]);
    }

    private static void WriteClientKeyValue(List<byte> sink, ClientKey ck)
    {
        // Fields in sorted order: client_principal, client_nonce
        WritePrincipal(sink, ck.ClientPrincipal);
        Span<byte> tmp = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(tmp, ck.ClientNonce);
        for (int i = 0; i < 8; i++) sink.Add(tmp[i]);
    }

    private static int EstimateMessagesSize(CanisterOutputCertifiedMessages msgs)
    {
        int n = msgs.Cert.Length + msgs.Tree.Length + 16;
        foreach (var m in msgs.Messages)
            n += m.Key.Length + m.Content.Length + 80;
        return n;
    }

    // ─── Reader helpers for nested records ───────────────────────────────
    private static ValueReader OpenReader(ReadOnlySpan<byte> arg)
    {
        var r = new ValueReader(arg);
        r.ReadMagic();
        ulong typeCount = r.ReadUleb();
        for (ulong i = 0; i < typeCount; i++) r.SkipTypeEntry();
        ulong valueCount = r.ReadUleb();
        for (ulong i = 0; i < valueCount; i++) r.ReadSleb();
        return r;
    }

    private static ClientKey ReadClientKey(ref ValueReader r)
    {
        // Fields in sort order: client_principal, client_nonce
        byte[] principal = r.ReadPrincipal();
        ulong nonce = r.ReadNat64();
        return new ClientKey(principal, nonce);
    }

    private static WebsocketMessage ReadWebsocketMessage(ref ValueReader r)
    {
        // WebsocketMessage fields by hash:
        //   sequence_num(352253448), content(427265337), client_key(1025972843),
        //   timestamp(2781795542), is_service_message(2815490792)
        ulong sequenceNum = r.ReadNat64();
        byte[] content = r.ReadBlob();
        ClientKey ck = ReadClientKey(ref r);
        ulong timestamp = r.ReadNat64();
        bool isServiceMessage = r.ReadBool();
        return new WebsocketMessage(ck, sequenceNum, timestamp, isServiceMessage, content);
    }

    private ref struct ValueReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;
        public ValueReader(ReadOnlySpan<byte> data) { _data = data; _pos = 0; }

        public void ReadMagic()
        {
            if (_data.Length < 4 || _data[0] != 0x44 || _data[1] != 0x49 ||
                _data[2] != 0x44 || _data[3] != 0x4C)
                throw new InvalidOperationException("Candid: missing DIDL magic");
            _pos = 4;
        }

        public byte[] ReadPrincipal()
        {
            // Principal value: 1 byte (always 0x01 for "non-anonymous" form), then ULEB length + bytes.
            byte tag = _data[_pos++];
            if (tag != 0x01) throw new InvalidOperationException($"Candid principal: unexpected tag 0x{tag:X2}");
            ulong len = ReadUleb();
            var dst = _data.Slice(_pos, (int)len).ToArray();
            _pos += (int)len;
            return dst;
        }

        public ulong ReadNat64()
        {
            ulong v = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(_pos, 8));
            _pos += 8;
            return v;
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

        public bool ReadBool() => _data[_pos++] != 0;

        public ulong ReadUleb()
        {
            ulong result = 0;
            int shift = 0;
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
            long result = 0;
            int shift = 0;
            byte b;
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
                case -18: ReadSleb(); break;
                case -19: ReadSleb(); break;
                case -20:
                case -21:
                {
                    ulong fields = ReadUleb();
                    for (ulong f = 0; f < fields; f++) { ReadUleb(); ReadSleb(); }
                    break;
                }
                case -22:
                {
                    ulong args = ReadUleb();
                    for (ulong a = 0; a < args; a++) ReadSleb();
                    ulong results = ReadUleb();
                    for (ulong r = 0; r < results; r++) ReadSleb();
                    ulong anns = ReadUleb();
                    _pos += (int)anns;
                    break;
                }
                case -23:
                {
                    ulong methods = ReadUleb();
                    for (ulong m = 0; m < methods; m++)
                    {
                        ulong nameLen = ReadUleb();
                        _pos += (int)nameLen;
                        ReadSleb();
                    }
                    break;
                }
                default: break;
            }
        }
    }
}
