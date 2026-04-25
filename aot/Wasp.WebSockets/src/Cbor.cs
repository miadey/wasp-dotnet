using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Wasp.WebSockets;

// Minimal CBOR encoder/decoder covering the subset Omnia's ic-websocket
// protocol uses on the wire:
//   - unsigned ints (major 0)
//   - byte strings (major 2)
//   - text strings (major 3)
//   - arrays (major 4)
//   - maps with text-string keys (major 5)
//   - the `bool` simple values (true=0xF5, false=0xF4)
//   - the self-describe tag (0xD9D9F7)
//
// Hand-rolled to avoid pulling System.Formats.Cbor into the trim graph
// — it works under NativeAOT-LLVM but ships ~150 KB of extra code we
// don't need.

internal static class Cbor
{
    public sealed class Writer
    {
        private readonly List<byte> _buf = new();

        public byte[] ToArray() => _buf.ToArray();
        public int Length => _buf.Count;

        public Writer SelfDescribeTag()
        {
            _buf.Add(0xD9); _buf.Add(0xD9); _buf.Add(0xF7);
            return this;
        }

        public Writer WriteMapHeader(int count) { WriteHeader(5, (ulong)count); return this; }
        public Writer WriteArrayHeader(int count) { WriteHeader(4, (ulong)count); return this; }
        public Writer WriteUInt(ulong value) { WriteHeader(0, value); return this; }
        public Writer WriteByteString(ReadOnlySpan<byte> bytes)
        {
            WriteHeader(2, (ulong)bytes.Length);
            for (int i = 0; i < bytes.Length; i++) _buf.Add(bytes[i]);
            return this;
        }
        public Writer WriteTextString(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            WriteHeader(3, (ulong)bytes.Length);
            for (int i = 0; i < bytes.Length; i++) _buf.Add(bytes[i]);
            return this;
        }
        public Writer WriteBool(bool b) { _buf.Add(b ? (byte)0xF5 : (byte)0xF4); return this; }

        private void WriteHeader(int majorType, ulong value)
        {
            byte mt = (byte)(majorType << 5);
            if (value < 24) _buf.Add((byte)(mt | (byte)value));
            else if (value <= 0xFF)        { _buf.Add((byte)(mt | 24)); _buf.Add((byte)value); }
            else if (value <= 0xFFFF)
            {
                _buf.Add((byte)(mt | 25));
                Span<byte> tmp = stackalloc byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(tmp, (ushort)value);
                _buf.Add(tmp[0]); _buf.Add(tmp[1]);
            }
            else if (value <= 0xFFFFFFFF)
            {
                _buf.Add((byte)(mt | 26));
                Span<byte> tmp = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(tmp, (uint)value);
                for (int i = 0; i < 4; i++) _buf.Add(tmp[i]);
            }
            else
            {
                _buf.Add((byte)(mt | 27));
                Span<byte> tmp = stackalloc byte[8];
                BinaryPrimitives.WriteUInt64BigEndian(tmp, value);
                for (int i = 0; i < 8; i++) _buf.Add(tmp[i]);
            }
        }
    }

    public ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;
        public Reader(ReadOnlySpan<byte> data) { _data = data; _pos = 0; }
        public bool HasRemaining => _pos < _data.Length;

        public void TrySkipSelfDescribeTag()
        {
            if (_data.Length - _pos >= 3 &&
                _data[_pos] == 0xD9 && _data[_pos + 1] == 0xD9 && _data[_pos + 2] == 0xF7)
            {
                _pos += 3;
            }
        }

        public int ReadMapHeader() { var (mt, v) = ReadHeader(); Expect(mt, 5); return (int)v; }
        public int ReadArrayHeader() { var (mt, v) = ReadHeader(); Expect(mt, 4); return (int)v; }
        public ulong ReadUInt() { var (mt, v) = ReadHeader(); Expect(mt, 0); return v; }
        public byte[] ReadByteString()
        {
            var (mt, v) = ReadHeader();
            Expect(mt, 2);
            int n = (int)v;
            var dst = _data.Slice(_pos, n).ToArray();
            _pos += n;
            return dst;
        }
        public string ReadTextString()
        {
            var (mt, v) = ReadHeader();
            Expect(mt, 3);
            int n = (int)v;
            var s = Encoding.UTF8.GetString(_data.Slice(_pos, n));
            _pos += n;
            return s;
        }
        public bool ReadBool()
        {
            byte b = _data[_pos++];
            if (b == 0xF5) return true;
            if (b == 0xF4) return false;
            throw new InvalidOperationException($"CBOR: expected bool, got 0x{b:X2}");
        }

        private static void Expect(int got, int want)
        {
            if (got != want) throw new InvalidOperationException($"CBOR: expected major type {want}, got {got}");
        }

        private (int majorType, ulong value) ReadHeader()
        {
            byte ib = _data[_pos++];
            int mt = ib >> 5;
            int info = ib & 0x1F;
            ulong v;
            if (info < 24) v = (ulong)info;
            else if (info == 24) v = _data[_pos++];
            else if (info == 25) { v = BinaryPrimitives.ReadUInt16BigEndian(_data.Slice(_pos, 2)); _pos += 2; }
            else if (info == 26) { v = BinaryPrimitives.ReadUInt32BigEndian(_data.Slice(_pos, 4)); _pos += 4; }
            else if (info == 27) { v = BinaryPrimitives.ReadUInt64BigEndian(_data.Slice(_pos, 8)); _pos += 8; }
            else throw new InvalidOperationException($"CBOR: unsupported additional info {info}");
            return (mt, v);
        }
    }
}
