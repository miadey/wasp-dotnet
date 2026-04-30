using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Wasp.Http;

// Hand-rolled Candid codec for HttpRequest and HttpResponse. Matches the
// IC boundary node's expected wire format. Records are encoded with
// fields sorted by Candid field-name hash, as the spec mandates.
//
// Future iterations (post-Phase 2) will replace this with source-gen
// codecs derived from arbitrary C# record types.
public static class CandidHttp
{
    // ─── Type-table opcodes (SLEB128 negatives, byte form) ────────────────
    private const byte CODE_OPT     = 0x6E; // -18
    private const byte CODE_VEC     = 0x6D; // -19
    private const byte CODE_RECORD  = 0x6C; // -20
    private const byte CODE_BOOL    = 0x7E; // -2
    private const byte CODE_NAT8    = 0x7B; // -5
    private const byte CODE_NAT16   = 0x7A; // -6
    private const byte CODE_TEXT    = 0x71; // -15

    // ─── Well-known Candid field-name hashes ──────────────────────────────
    // Computed via the standard hash: h(s) = (sum c * 223^pos) mod 2^32
    // (see candid-spec §3 Field IDs).
    public const uint H_url                 = 5843823;
    public const uint H_method              = 156956385;
    public const uint H_body                = 1092319906;
    public const uint H_headers             = 1661489734;
    public const uint H_certificate_version = 1661892784;
    public const uint H_status_code         = 3475804314;
    public const uint H_upgrade             = 1664201884;
    public const uint H_streaming_strategy  = 3427158832;

    // ─── Decode HttpRequest ───────────────────────────────────────────────
    // We're opinionated: the IC gateway always sends the same record
    // shape, so we skip the type table and read the value section in the
    // canonical sort order.
    public static HttpRequest DecodeRequest(ReadOnlySpan<byte> arg)
    {
        var r = new RawReader(arg);
        r.ReadMagic();
        ulong typeCount = r.ReadUleb();
        for (ulong i = 0; i < typeCount; i++) r.SkipTypeEntry();
        ulong valueCount = r.ReadUleb();
        for (ulong i = 0; i < valueCount; i++) r.ReadSleb();

        // The IC ships two flavours that share most fields:
        //   - HttpRequest         (query path)  — 5 fields, includes
        //                                          certificate_version
        //   - HttpUpdateRequest   (update path) — 4 fields, no cert_version
        //
        // We sniff which one we have by checking whether any bytes remain
        // after the four mandatory fields. Field order is hash-ascending:
        //   url, method, body, headers, [certificate_version].
        string url = r.ReadText();
        string method = r.ReadText();
        byte[] body = r.ReadBlob();
        var headers = r.ReadHeaderVec();
        ushort? certVersion = r.HasRemaining ? r.ReadOptNat16() : null;

        return new HttpRequest
        {
            Method = method,
            Url = url,
            Headers = headers,
            Body = body,
            CertificateVersion = certVersion,
        };
    }

    // ─── Encode HttpResponse ──────────────────────────────────────────────
    // Type table layout we emit (5 entries):
    //   0: record { 0: text; 1: text }                  (header tuple)
    //   1: vec(0)                                        (vec of header tuples)
    //   2: vec(nat8)                                     (blob)
    //   3: opt(bool)                                     (upgrade)
    //   4: record {
    //        body: vec(nat8) → type 2,
    //        headers: vec(0,1) → type 1,
    //        upgrade: opt(bool) → type 3,
    //        status_code: nat16,
    //      }                                            (HttpResponse — no streaming for v0.1)
    public static byte[] EncodeResponse(HttpResponse resp)
    {
        var buf = new List<byte>(64 + resp.Body.Length);
        // Magic
        buf.Add(0x44); buf.Add(0x49); buf.Add(0x44); buf.Add(0x4C);

        // Type table (5 entries)
        WriteUleb(buf, 5);

        // entry 0: record { 0: text, 1: text }
        buf.Add(CODE_RECORD);
        WriteUleb(buf, 2);
        WriteUleb(buf, 0); WriteSlebPositive(buf, CODE_TEXT);
        WriteUleb(buf, 1); WriteSlebPositive(buf, CODE_TEXT);

        // entry 1: vec(type 0)
        buf.Add(CODE_VEC);
        WriteUleb(buf, 0);

        // entry 2: vec(nat8)  (= blob)
        buf.Add(CODE_VEC);
        WriteSlebPositive(buf, CODE_NAT8);

        // entry 3: opt(bool)
        buf.Add(CODE_OPT);
        WriteSlebPositive(buf, CODE_BOOL);

        // entry 4: record { body, headers, upgrade, status_code }
        // Field IDs sorted ascending: body(1092…), headers(1661489…), upgrade(1664…), status_code(3475…)
        buf.Add(CODE_RECORD);
        WriteUleb(buf, 4);
        WriteUleb(buf, H_body);            WriteUleb(buf, 2); // → type 2
        WriteUleb(buf, H_headers);         WriteUleb(buf, 1); // → type 1
        WriteUleb(buf, H_upgrade);         WriteUleb(buf, 3); // → type 3
        WriteUleb(buf, H_status_code);     WriteSlebPositive(buf, CODE_NAT16);

        // value-types: 1 value of type 4
        WriteUleb(buf, 1);
        WriteUleb(buf, 4);

        // value: fields in same sort order as type-table entry 4
        // body
        WriteUleb(buf, (ulong)resp.Body.Length);
        for (int i = 0; i < resp.Body.Length; i++) buf.Add(resp.Body[i]);
        // headers
        WriteUleb(buf, (ulong)resp.Headers.Count);
        for (int i = 0; i < resp.Headers.Count; i++)
        {
            var h = resp.Headers[i];
            WriteText(buf, h.Key);
            WriteText(buf, h.Value);
        }
        // upgrade (opt bool: 0 = none, 1 + value byte = some)
        if (resp.Upgrade is bool up)
        {
            buf.Add(0x01);
            buf.Add((byte)(up ? 1 : 0));
        }
        else
        {
            buf.Add(0x00);
        }
        // status_code
        Span<byte> tmp = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(tmp, resp.StatusCode);
        buf.Add(tmp[0]); buf.Add(tmp[1]);

        return buf.ToArray();
    }

    // ─── Helpers ──────────────────────────────────────────────────────────
    // SLEB128 of a single-byte negative type code (most opcodes fit in one byte).
    private static void WriteSlebPositive(List<byte> sink, byte negativeOpcode)
    {
        // The byte form 0x71..0x7F decodes as -15..-1 in single-byte SLEB128.
        // We wrote them with the leading bit unset, which is correct.
        sink.Add(negativeOpcode);
    }

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

        public bool HasRemaining => _pos < _data.Length;

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
                string name = ReadText();
                string value = ReadText();
                arr[i] = new KeyValuePair<string, string>(name, value);
            }
            return arr;
        }

        public ushort? ReadOptNat16()
        {
            byte tag = _data[_pos++];
            if (tag == 0) return null;
            ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(_pos, 2));
            _pos += 2;
            return v;
        }

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
                case -18: ReadSleb(); break;                                  // opt T
                case -19: ReadSleb(); break;                                  // vec T
                case -20:                                                      // record { id : T … }
                case -21:                                                      // variant
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
                default: break; // primitive — no params
            }
        }
    }
}
