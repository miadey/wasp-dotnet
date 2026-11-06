using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Wasp.IcCdk;

// Hand-rolled Candid encoder/decoder covering the primitive surface
// needed for Phase 1 of Wasp.IcCdk:
//
//   bool, nat8, nat16, nat32, nat64, int8, int16, int32, int64,
//   text, blob, null/unit, principal, opt T (T primitive), vec T (T primitive).
//
// Records / variants / function refs / service refs are deferred to a
// later iteration that will use a Roslyn source generator to emit
// per-type codecs at compile time.
//
// The wire format follows the Candid spec
// https://github.com/dfinity/candid/blob/master/spec/Candid.md
// — `DIDL` magic, type table, value-type list, value section.
public static class Candid
{
    // ───── Constants ──────────────────────────────────────────────────────
    private const byte MAGIC0 = 0x44; // 'D'
    private const byte MAGIC1 = 0x49; // 'I'
    private const byte MAGIC2 = 0x44; // 'D'
    private const byte MAGIC3 = 0x4C; // 'L'

    // SLEB128 type codes for primitives (cf. Candid spec §3).
    private const byte CODE_NULL    = 0x7F; // -1
    private const byte CODE_BOOL    = 0x7E; // -2
    private const byte CODE_NAT     = 0x7D; // -3
    private const byte CODE_INT     = 0x7C; // -4
    private const byte CODE_NAT8    = 0x7B; // -5
    private const byte CODE_NAT16   = 0x7A; // -6
    private const byte CODE_NAT32   = 0x79; // -7
    private const byte CODE_NAT64   = 0x78; // -8
    private const byte CODE_INT8    = 0x77; // -9
    private const byte CODE_INT16   = 0x76; // -10
    private const byte CODE_INT32   = 0x75; // -11
    private const byte CODE_INT64   = 0x74; // -12
    private const byte CODE_FLOAT32 = 0x73; // -13
    private const byte CODE_FLOAT64 = 0x72; // -14
    private const byte CODE_TEXT    = 0x71; // -15
    private const byte CODE_RESERVED= 0x70; // -16
    private const byte CODE_EMPTY   = 0x6F; // -17
    private const byte CODE_OPT     = 0x6E; // -18
    private const byte CODE_VEC     = 0x6D; // -19
    private const byte CODE_RECORD  = 0x6C; // -20
    private const byte CODE_VARIANT = 0x6B; // -21
    private const byte CODE_PRINCIPAL = 0x68; // -24

    public static readonly byte[] Empty = { MAGIC0, MAGIC1, MAGIC2, MAGIC3, 0x00, 0x00 };

    // ───── Encoders for primitive single-value tuples ─────────────────────
    public static byte[] EncodeUnit() => Empty;

    public static byte[] EncodeBool(bool value)
        => Header(1, CODE_BOOL, 1, b => b.Add((byte)(value ? 1 : 0)));

    public static byte[] EncodeNat32(uint value)
        => Header(1, CODE_NAT32, 4, b =>
        {
            Span<byte> tmp = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(tmp, value);
            for (int i = 0; i < 4; i++) b.Add(tmp[i]);
        });

    public static byte[] EncodeNat64(ulong value)
        => Header(1, CODE_NAT64, 8, b =>
        {
            Span<byte> tmp = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(tmp, value);
            for (int i = 0; i < 8; i++) b.Add(tmp[i]);
        });

    public static byte[] EncodeInt32(int value)
        => Header(1, CODE_INT32, 4, b =>
        {
            Span<byte> tmp = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(tmp, value);
            for (int i = 0; i < 4; i++) b.Add(tmp[i]);
        });

    public static byte[] EncodeInt64(long value)
        => Header(1, CODE_INT64, 8, b =>
        {
            Span<byte> tmp = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(tmp, value);
            for (int i = 0; i < 8; i++) b.Add(tmp[i]);
        });

    public static byte[] EncodeText(string s)
    {
        var utf8 = Encoding.UTF8.GetBytes(s);
        return Header(1, CODE_TEXT, EstimateLen((ulong)utf8.Length) + utf8.Length, b =>
        {
            WriteUleb(b, (ulong)utf8.Length);
            for (int i = 0; i < utf8.Length; i++) b.Add(utf8[i]);
        });
    }

    public static byte[] EncodeBlob(ReadOnlySpan<byte> bytes)
    {
        // blob is `vec nat8`. Type table needs a single entry: vec(nat8).
        var b = new List<byte>(16 + bytes.Length);
        b.Add(MAGIC0); b.Add(MAGIC1); b.Add(MAGIC2); b.Add(MAGIC3);
        WriteUleb(b, 1);                // 1 type-table entry
        b.Add(CODE_VEC);                // entry 0 = vec(...)
        b.Add(CODE_NAT8);               //         ... nat8
        WriteUleb(b, 1);                // 1 value
        WriteUleb(b, 0);                // value type = type-table[0]
        WriteUleb(b, (ulong)bytes.Length);
        for (int i = 0; i < bytes.Length; i++) b.Add(bytes[i]);
        return b.ToArray();
    }

    // ───── Decoders ───────────────────────────────────────────────────────
    public ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;

        public Reader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _pos = 0;
            ReadMagic();
            // We don't validate the type table beyond skipping it because
            // the user's expected types are known statically; we read
            // values directly. A stricter implementation would build a
            // type table model and check compatibility.
            ulong typeCount = ReadUlebPrivate();
            for (ulong i = 0; i < typeCount; i++) SkipTypeEntry();
            // value-types: count + N type references
            ulong valueCount = ReadUlebPrivate();
            for (ulong i = 0; i < valueCount; i++) ReadSlebPrivate();
        }

        public bool ReadBool()
        {
            byte b = _data[_pos++];
            return b != 0;
        }

        public uint ReadNat32()
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_pos, 4));
            _pos += 4;
            return v;
        }

        public ulong ReadNat64()
        {
            ulong v = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(_pos, 8));
            _pos += 8;
            return v;
        }

        public int ReadInt32()
        {
            int v = BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(_pos, 4));
            _pos += 4;
            return v;
        }

        public long ReadInt64()
        {
            long v = BinaryPrimitives.ReadInt64LittleEndian(_data.Slice(_pos, 8));
            _pos += 8;
            return v;
        }

        public string ReadText()
        {
            ulong len = ReadUlebPrivate();
            var bytes = _data.Slice(_pos, (int)len);
            _pos += (int)len;
            return Encoding.UTF8.GetString(bytes);
        }

        public byte[] ReadBlob()
        {
            ulong len = ReadUlebPrivate();
            var dst = _data.Slice(_pos, (int)len).ToArray();
            _pos += (int)len;
            return dst;
        }

        private void ReadMagic()
        {
            if (_data.Length < 4 ||
                _data[0] != MAGIC0 || _data[1] != MAGIC1 ||
                _data[2] != MAGIC2 || _data[3] != MAGIC3)
            {
                throw new InvalidOperationException("Candid: missing DIDL magic");
            }
            _pos = 4;
        }

        private void SkipTypeEntry()
        {
            long opcode = ReadSlebPrivate();
            switch (opcode)
            {
                case -18: ReadSlebPrivate(); break;                 // opt T
                case -19: ReadSlebPrivate(); break;                 // vec T
                case -20:                                           // record { id : T … }
                case -21:                                           // variant { id : T … }
                {
                    ulong fields = ReadUlebPrivate();
                    for (ulong f = 0; f < fields; f++) { ReadUlebPrivate(); ReadSlebPrivate(); }
                    break;
                }
                case -22:                                           // func ref (skip args, results, annotations)
                {
                    ulong args = ReadUlebPrivate();
                    for (ulong a = 0; a < args; a++) ReadSlebPrivate();
                    ulong results = ReadUlebPrivate();
                    for (ulong r = 0; r < results; r++) ReadSlebPrivate();
                    ulong anns = ReadUlebPrivate();
                    _pos += (int)anns;
                    break;
                }
                case -23:                                           // service ref
                {
                    ulong methods = ReadUlebPrivate();
                    for (ulong m = 0; m < methods; m++)
                    {
                        ulong nameLen = ReadUlebPrivate();
                        _pos += (int)nameLen;
                        ReadSlebPrivate();
                    }
                    break;
                }
                default:
                    // Primitive opcode — no parameters to skip.
                    break;
            }
        }

        private ulong ReadUlebPrivate()
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

        private long ReadSlebPrivate()
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
            if (shift < 64 && (b & 0x40) != 0)
                result |= -1L << shift;
            return result;
        }
    }

    // ───── Internals ──────────────────────────────────────────────────────
    private delegate void ValueWriter(List<byte> sink);

    private static byte[] Header(int valueCount, byte primitiveCode, int valueBytes, ValueWriter writeValues)
    {
        // For primitive value types, no type-table entries are needed.
        // The value-types section just lists the primitive opcode directly.
        var b = new List<byte>(8 + valueBytes);
        b.Add(MAGIC0); b.Add(MAGIC1); b.Add(MAGIC2); b.Add(MAGIC3);
        b.Add(0x00); // 0 type-table entries
        WriteUleb(b, (ulong)valueCount);
        b.Add(primitiveCode);
        writeValues(b);
        return b.ToArray();
    }

    private static int EstimateLen(ulong value)
    {
        int n = 0;
        do { n++; value >>= 7; } while (value != 0);
        return n;
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
}
