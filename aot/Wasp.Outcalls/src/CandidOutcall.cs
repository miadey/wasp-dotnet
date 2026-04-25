using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Wasp.Outcalls;

// Hand-rolled Candid codec for the IC management canister's
// http_request input/output types. Encodes the inbound
// CanisterHttpRequestArgs WITHOUT the optional transform field so we
// don't have to model func-ref types — Candid treats absent opt fields
// on the sender side as None.
internal static class CandidOutcall
{
    private const byte CODE_NULL    = 0x7F;
    private const byte CODE_NAT8    = 0x7B;
    private const byte CODE_NAT16   = 0x7A;
    private const byte CODE_NAT32   = 0x79;
    private const byte CODE_NAT64   = 0x78;
    private const byte CODE_TEXT    = 0x71;
    private const byte CODE_OPT     = 0x6E;
    private const byte CODE_VEC     = 0x6D;
    private const byte CODE_RECORD  = 0x6C;
    private const byte CODE_VARIANT = 0x6B;

    // Candid field-name hashes (computed once).
    private const uint H_get                 = 5144726;
    private const uint H_url                 = 5843823;
    private const uint H_status              = 100394802;
    private const uint H_method              = 156956385;
    private const uint H_max_response_bytes  = 309734248;
    private const uint H_value               = 834174833;
    private const uint H_body                = 1092319906;
    private const uint H_head                = 1158359328;
    private const uint H_name                = 1224700491;
    private const uint H_post                = 1247577184;
    private const uint H_headers             = 1661489734;

    /// <summary>Encode CanisterHttpRequestArgs (without `transform`).</summary>
    public static byte[] EncodeRequest(
        string url,
        OutcallMethod method,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        byte[]? body,
        ulong? maxResponseBytes)
    {
        var buf = new List<byte>(128 + (body?.Length ?? 0));
        buf.Add(0x44); buf.Add(0x49); buf.Add(0x44); buf.Add(0x4C);

        // ─── Type table (7 entries) ──────────────────────────────────────
        WriteUleb(buf, 7);

        // 0: record { name: text, value: text }
        // Field IDs sorted: value(834174833), name(1224700491)
        buf.Add(CODE_RECORD); WriteUleb(buf, 2);
        WriteUleb(buf, H_value); buf.Add(CODE_TEXT);
        WriteUleb(buf, H_name);  buf.Add(CODE_TEXT);

        // 1: vec(0)
        buf.Add(CODE_VEC); WriteUleb(buf, 0);

        // 2: opt(nat64)
        buf.Add(CODE_OPT); buf.Add(CODE_NAT64);

        // 3: vec(nat8)  (blob)
        buf.Add(CODE_VEC); buf.Add(CODE_NAT8);

        // 4: opt(3)  (opt blob)
        buf.Add(CODE_OPT); WriteUleb(buf, 3);

        // 5: variant { get; head; post } — all arms are null
        // Sorted: get, head, post
        buf.Add(CODE_VARIANT); WriteUleb(buf, 3);
        WriteUleb(buf, H_get);  buf.Add(CODE_NULL);
        WriteUleb(buf, H_head); buf.Add(CODE_NULL);
        WriteUleb(buf, H_post); buf.Add(CODE_NULL);

        // 6: record {
        //      url: text, method: 5, max_response_bytes: 2,
        //      body: 4, headers: 1
        //    }    (transform omitted — receiver treats as None)
        // Field IDs sorted: url, method, max_response_bytes, body, headers
        buf.Add(CODE_RECORD); WriteUleb(buf, 5);
        WriteUleb(buf, H_url);                 buf.Add(CODE_TEXT);
        WriteUleb(buf, H_method);              WriteUleb(buf, 5);
        WriteUleb(buf, H_max_response_bytes);  WriteUleb(buf, 2);
        WriteUleb(buf, H_body);                WriteUleb(buf, 4);
        WriteUleb(buf, H_headers);             WriteUleb(buf, 1);

        // ─── Value section: 1 value of type 6 ────────────────────────────
        WriteUleb(buf, 1);
        WriteUleb(buf, 6);

        // url
        WriteText(buf, url);
        // method (variant tag)
        WriteUleb(buf, (ulong)method);
        // max_response_bytes (opt nat64)
        if (maxResponseBytes is ulong m)
        {
            buf.Add(0x01);
            Span<byte> tmp = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(tmp, m);
            for (int i = 0; i < 8; i++) buf.Add(tmp[i]);
        }
        else buf.Add(0x00);
        // body (opt blob)
        if (body is not null)
        {
            buf.Add(0x01);
            WriteUleb(buf, (ulong)body.Length);
            for (int i = 0; i < body.Length; i++) buf.Add(body[i]);
        }
        else buf.Add(0x00);
        // headers (vec record { value, name })
        WriteUleb(buf, (ulong)headers.Count);
        for (int i = 0; i < headers.Count; i++)
        {
            // Field order matches type-table sort: value, name
            WriteText(buf, headers[i].Value);
            WriteText(buf, headers[i].Key);
        }

        return buf.ToArray();
    }

    /// <summary>Decode the management canister's HttpResponse value.</summary>
    public static OutcallResponse DecodeResponse(ReadOnlySpan<byte> arg)
    {
        var r = new RawReader(arg);
        r.ReadMagic();
        ulong typeCount = r.ReadUleb();
        for (ulong i = 0; i < typeCount; i++) r.SkipTypeEntry();
        ulong valueCount = r.ReadUleb();
        for (ulong i = 0; i < valueCount; i++) r.ReadSleb();

        // HttpResponse fields by hash (ascending):
        //   status(100394802), body(1092319906), headers(1661489734)
        // status is `nat` (unbounded). For HTTP this fits in u16.
        ulong status = r.ReadNat();
        byte[] body = r.ReadBlob();
        var headers = r.ReadHeaderVec();

        return new OutcallResponse((ushort)status, headers, body);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────
    private static void WriteText(List<byte> sink, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        WriteUleb(sink, (ulong)b.Length);
        for (int i = 0; i < b.Length; i++) sink.Add(b[i]);
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

        public string ReadText()
        {
            ulong len = ReadUleb();
            var span = _data.Slice(_pos, (int)len);
            _pos += (int)len;
            return Encoding.UTF8.GetString(span);
        }

        public byte[] ReadBlob()
        {
            ulong len = ReadUleb();
            var dst = _data.Slice(_pos, (int)len).ToArray();
            _pos += (int)len;
            return dst;
        }

        public KeyValuePair<string, string>[] ReadHeaderVec()
        {
            ulong count = ReadUleb();
            var arr = new KeyValuePair<string, string>[(int)count];
            for (ulong i = 0; i < count; i++)
            {
                // Wire order matches type-table sort: value, name.
                string value = ReadText();
                string name = ReadText();
                arr[i] = new KeyValuePair<string, string>(name, value);
            }
            return arr;
        }

        public ulong ReadNat()
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

        public ulong ReadUleb() => ReadNat();

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
