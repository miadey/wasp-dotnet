using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// Spike 3: extend HelloWorld with stable memory I/O so we can verify that
// (a) C# can call ic0.stable64_grow/read/write, (b) data round-trips
// correctly via Candid blob encoding, and (c) stable memory survives
// `dfx canister install --mode upgrade` while wasm linear memory does not.

namespace WaspSample.HelloWorld;

internal static unsafe class Ic0
{
    [DllImport("ic0", EntryPoint = "msg_reply")]
    [WasmImportLinkage]
    internal static extern void MsgReply();

    [DllImport("ic0", EntryPoint = "msg_reply_data_append")]
    [WasmImportLinkage]
    internal static extern void MsgReplyDataAppend(nint src, nint size);

    [DllImport("ic0", EntryPoint = "debug_print")]
    [WasmImportLinkage]
    internal static extern void DebugPrint(nint src, nint size);

    [DllImport("ic0", EntryPoint = "trap")]
    [WasmImportLinkage]
    internal static extern void Trap(nint src, nint size);

    [DllImport("ic0", EntryPoint = "msg_arg_data_size")]
    [WasmImportLinkage]
    internal static extern uint MsgArgDataSize();

    [DllImport("ic0", EntryPoint = "msg_arg_data_copy")]
    [WasmImportLinkage]
    internal static extern void MsgArgDataCopy(nint dst, uint offset, uint size);

    [DllImport("ic0", EntryPoint = "stable64_size")]
    [WasmImportLinkage]
    internal static extern ulong Stable64Size();

    [DllImport("ic0", EntryPoint = "stable64_grow")]
    [WasmImportLinkage]
    internal static extern ulong Stable64Grow(ulong newPages);

    [DllImport("ic0", EntryPoint = "stable64_write")]
    [WasmImportLinkage]
    internal static extern void Stable64Write(ulong offset, ulong src, ulong size);

    [DllImport("ic0", EntryPoint = "stable64_read")]
    [WasmImportLinkage]
    internal static extern void Stable64Read(ulong dst, ulong offset, ulong size);
}

// Tiny hand-rolled Candid encoder for the values we need in this spike:
// `text`, `nat64`, `blob` (= `vec nat8`), and the empty tuple.
internal static class Candid
{
    private static readonly byte[] Magic = { 0x44, 0x49, 0x44, 0x4C };

    // Empty value: "DIDL" + 0 type-table entries + 0 value types.
    public static readonly byte[] Empty = { 0x44, 0x49, 0x44, 0x4C, 0x00, 0x00 };

    // Header for a `blob` (= `vec nat8`) reply:
    //   DIDL | 01 (type-table count) | 6d (vec opcode) | 7b (nat8) |
    //   01 (value-types count) | 00 (refers to type-table entry 0) |
    //   ULEB128(length) | <bytes>
    private static readonly byte[] BlobHeader = { 0x44, 0x49, 0x44, 0x4C, 0x01, 0x6D, 0x7B, 0x01, 0x00 };

    public static byte[] EncodeText(string s)
    {
        ReadOnlySpan<byte> utf8 = System.Text.Encoding.UTF8.GetBytes(s);
        var leb = LebEncode((ulong)utf8.Length);
        var buf = new byte[Magic.Length + 1 + 1 + 1 + leb.Length + utf8.Length];
        int i = 0;
        Magic.CopyTo(buf, i); i += Magic.Length;
        buf[i++] = 0x00; // type-table count
        buf[i++] = 0x01; // value-types count
        buf[i++] = 0x71; // text type code (sleb128 -15)
        leb.CopyTo(buf, i); i += leb.Length;
        utf8.CopyTo(buf.AsSpan(i));
        return buf;
    }

    public static byte[] EncodeNat64(ulong v)
    {
        var buf = new byte[Magic.Length + 1 + 1 + 1 + 8];
        int i = 0;
        Magic.CopyTo(buf, i); i += Magic.Length;
        buf[i++] = 0x00;
        buf[i++] = 0x01;
        buf[i++] = 0x78; // nat64 type code (sleb128 -8)
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(i), v);
        return buf;
    }

    public static byte[] EncodeBlob(ReadOnlySpan<byte> bytes)
    {
        var leb = LebEncode((ulong)bytes.Length);
        var buf = new byte[BlobHeader.Length + leb.Length + bytes.Length];
        BlobHeader.CopyTo(buf, 0);
        leb.CopyTo(buf, BlobHeader.Length);
        bytes.CopyTo(buf.AsSpan(BlobHeader.Length + leb.Length));
        return buf;
    }

    // Decode a Candid argument tuple consisting of a single `blob` value.
    // Returns the inner bytes. Throws on malformed input.
    public static byte[] DecodeSingleBlob(ReadOnlySpan<byte> arg)
    {
        if (arg.Length < 6 || arg[0] != 0x44 || arg[1] != 0x49 || arg[2] != 0x44 || arg[3] != 0x4C)
            throw new InvalidOperationException("missing DIDL magic");
        int pos = 4;
        ulong typeCount = ReadUleb(arg, ref pos);
        // Skip each type-table entry. We expect at most one entry: vec(nat8).
        // Each entry is opcode SLEB128 followed by zero or more parameters.
        for (ulong i = 0; i < typeCount; i++)
        {
            long opcode = ReadSleb(arg, ref pos);
            if (opcode == -19L)         // vec
                ReadSleb(arg, ref pos); // element type
            else if (opcode == -20L)    // record (skip count + (id, type) pairs)
            {
                ulong fields = ReadUleb(arg, ref pos);
                for (ulong f = 0; f < fields; f++) { ReadUleb(arg, ref pos); ReadSleb(arg, ref pos); }
            }
            // Other constructed types omitted — Spike 3 only sends blobs.
        }
        ulong valueCount = ReadUleb(arg, ref pos);
        if (valueCount != 1) throw new InvalidOperationException("expected 1 arg");
        long valueType = ReadSleb(arg, ref pos);
        if (valueType < 0) throw new InvalidOperationException("expected blob (table-referenced vec nat8)");
        ulong len = ReadUleb(arg, ref pos);
        if (pos + (int)len > arg.Length) throw new InvalidOperationException("truncated blob payload");
        var dst = new byte[(int)len];
        arg.Slice(pos, (int)len).CopyTo(dst);
        return dst;
    }

    private static ulong ReadUleb(ReadOnlySpan<byte> data, ref int pos)
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            byte b = data[pos++];
            result |= ((ulong)(b & 0x7F)) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }
    }

    private static long ReadSleb(ReadOnlySpan<byte> data, ref int pos)
    {
        long result = 0;
        int shift = 0;
        byte b;
        do
        {
            b = data[pos++];
            result |= ((long)(b & 0x7F)) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);
        if (shift < 64 && (b & 0x40) != 0)
            result |= -1L << shift;
        return result;
    }

    private static byte[] LebEncode(ulong value)
    {
        Span<byte> tmp = stackalloc byte[10];
        int i = 0;
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) b |= 0x80;
            tmp[i++] = b;
        } while (value != 0);
        return tmp.Slice(0, i).ToArray();
    }
}

public static unsafe class HelloCanister
{
    private static ulong _counter;

    private static ReadOnlySpan<byte> HelloLog => "[wasp-dotnet] hello query"u8;
    private static ReadOnlySpan<byte> IncLog => "[wasp-dotnet] increment update"u8;
    private static ReadOnlySpan<byte> CountLog => "[wasp-dotnet] count query"u8;
    private static ReadOnlySpan<byte> WriteLog => "[wasp-dotnet] write_blob update"u8;
    private static ReadOnlySpan<byte> ReadLog => "[wasp-dotnet] read_blob query"u8;
    private static ReadOnlySpan<byte> SizeLog => "[wasp-dotnet] stable_size query"u8;

    private static void Print(ReadOnlySpan<byte> msg)
    {
        fixed (byte* p = msg) Ic0.DebugPrint((nint)p, msg.Length);
    }

    private static void Reply(ReadOnlySpan<byte> bytes)
    {
        fixed (byte* p = bytes)
        {
            Ic0.MsgReplyDataAppend((nint)p, bytes.Length);
            Ic0.MsgReply();
        }
    }

    private static void Trap(string reason)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(reason);
        fixed (byte* p = bytes) Ic0.Trap((nint)p, bytes.Length);
    }

    [UnmanagedCallersOnly(EntryPoint = "canister_query__hello")]
    public static void Hello()
    {
        Print(HelloLog);
        Reply(Candid.EncodeText("Hello from C# compiled to wasm by .NET 10"));
    }

    [UnmanagedCallersOnly(EntryPoint = "canister_update__increment")]
    public static void Increment()
    {
        Print(IncLog);
        _counter++;
        Reply(Candid.Empty);
    }

    [UnmanagedCallersOnly(EntryPoint = "canister_query__count")]
    public static void Count()
    {
        Print(CountLog);
        Reply(Candid.EncodeNat64(_counter));
    }

    // Stable-memory layout: first 8 bytes are a u64 length prefix, then
    // <length> bytes of payload. Trivial layout for the spike — Phase 1
    // will replace this with `Wasp.StableMemory` typed collections.
    private const ulong PageSize = 65536;

    [UnmanagedCallersOnly(EntryPoint = "canister_update__write_blob")]
    public static void WriteBlob()
    {
        Print(WriteLog);

        // Read the candid-encoded argument into a managed buffer.
        uint argLen = Ic0.MsgArgDataSize();
        var arg = new byte[(int)argLen];
        fixed (byte* p = arg) Ic0.MsgArgDataCopy((nint)p, 0, argLen);

        var blob = Candid.DecodeSingleBlob(arg);

        ulong neededBytes = 8 + (ulong)blob.Length;
        ulong currentPages = Ic0.Stable64Size();
        ulong currentBytes = currentPages * PageSize;
        if (neededBytes > currentBytes)
        {
            ulong neededPages = (neededBytes + PageSize - 1) / PageSize;
            ulong toGrow = neededPages - currentPages;
            ulong res = Ic0.Stable64Grow(toGrow);
            if (res == ulong.MaxValue) Trap("stable64_grow failed");
        }

        // Write 8-byte length prefix followed by the payload.
        ulong len = (ulong)blob.Length;
        Span<byte> lenBuf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(lenBuf, len);
        fixed (byte* p = lenBuf) Ic0.Stable64Write(0, (ulong)(nint)p, 8);
        if (len > 0)
        {
            fixed (byte* p = blob) Ic0.Stable64Write(8, (ulong)(nint)p, len);
        }

        Reply(Candid.Empty);
    }

    [UnmanagedCallersOnly(EntryPoint = "canister_query__read_blob")]
    public static void ReadBlob()
    {
        Print(ReadLog);

        ulong pages = Ic0.Stable64Size();
        if (pages == 0)
        {
            Reply(Candid.EncodeBlob(ReadOnlySpan<byte>.Empty));
            return;
        }

        Span<byte> lenBuf = stackalloc byte[8];
        fixed (byte* p = lenBuf) Ic0.Stable64Read((ulong)(nint)p, 0, 8);
        ulong len = BinaryPrimitives.ReadUInt64LittleEndian(lenBuf);

        var data = new byte[(int)len];
        if (len > 0)
        {
            fixed (byte* p = data) Ic0.Stable64Read((ulong)(nint)p, 8, len);
        }

        Reply(Candid.EncodeBlob(data));
    }

    [UnmanagedCallersOnly(EntryPoint = "canister_query__stable_size")]
    public static void StableSize()
    {
        Print(SizeLog);
        Reply(Candid.EncodeNat64(Ic0.Stable64Size()));
    }
}
