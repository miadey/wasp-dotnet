using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Wasp.AspNetCore.Blazor.Server;

// Minimal MessagePack + SignalR binary-framing writer that produces the
// exact byte format BlazorPackHubProtocol.WriteMessage emits for an
// outbound InvocationMessage (server -> browser).
//
// Wire format:
//
//   <varint length prefix>  // 7-bit VLQ, LSB first, top-bit = continuation
//   <msgpack array(6)>      // [type, headers, invocationId, target, args, streamIds]
//
// For server-initiated fire-and-forget calls (the only kind RemoteRenderer
// + RemoteJSRuntime emit -- see docs/m4-s2-circuit-coupling.md):
//   - type           = 1 (Invocation)
//   - headers        = {}   (empty fixmap)
//   - invocationId   = nil  (NonBlocking)
//   - target         = "JS.AttachComponent" | "JS.RenderBatch" | ...
//   - args           = [ ... positional arguments ... ]
//   - streamIds      = []
//
// We only implement the subset of MessagePack BlazorPack actually emits for
// the 6 outbound call sites from S2. Reference: msgpack-spec
// (https://github.com/msgpack/msgpack/blob/master/spec.md) and
// dotnet/aspnetcore src/Components/Server/src/BlazorPack/.
//
// Wire-compat against the unmodified blazor.web.js reader is NOT yet
// validated end-to-end -- see BlazorPackWriterTests for what is covered and
// docs/m4-s2-circuit-coupling.md "Risks surfaced" #4.
public static class BlazorPackWriter
{
    private const byte HubMessageType_Invocation = 1;

    /// <summary>
    /// Writes a complete fire-and-forget SignalR Invocation frame
    /// (length-prefixed) for `target(args)`. Allocates a single byte[].
    /// </summary>
    public static byte[] WriteInvocation(string target, object?[] args)
        => WriteInvocationCore(invocationId: null, target, args);

    /// <summary>
    /// Same as <see cref="WriteInvocation"/> but with a non-nil invocationId
    /// so the recipient is expected to reply with a Completion frame.
    /// Used by <c>IcCircuitTransport.InvokeCoreAsync</c>.
    /// </summary>
    public static byte[] WriteInvocationWithId(string invocationId, string target, object?[] args)
    {
        if (string.IsNullOrEmpty(invocationId))
            throw new ArgumentException("invocationId required", nameof(invocationId));
        return WriteInvocationCore(invocationId, target, args);
    }

    private static byte[] WriteInvocationCore(string? invocationId, string target, object?[] args)
    {
        var body = new List<byte>(64);

        WriteArrayHeader(body, 6);                    // outer array(6)
        body.Add(HubMessageType_Invocation);          // [0] type = 1
        body.Add(0x80);                               // [1] headers = empty fixmap

        if (invocationId is null) body.Add(0xC0);     // [2] invocationId = nil
        else WriteString(body, invocationId);

        WriteString(body, target);                    // [3] target

        WriteArrayHeader(body, args.Length);          // [4] args
        for (int i = 0; i < args.Length; i++) WriteValue(body, args[i]);

        body.Add(0x90);                               // [5] streamIds = empty fixarray

        Span<byte> lenBuf = stackalloc byte[5];
        int lenBufLen = WriteLengthPrefix(body.Count, lenBuf);

        var result = new byte[lenBufLen + body.Count];
        for (int i = 0; i < lenBufLen; i++) result[i] = lenBuf[i];
        body.CopyTo(result, lenBufLen);
        return result;
    }

    /// <summary>
    /// Build a Completion frame (type=3): used in tests to round-trip
    /// InvokeCoreAsync. Mirror of <see cref="BlazorPackReader.ReadCompletion"/>.
    /// </summary>
    public static byte[] WriteCompletion(string invocationId, object? result)
    {
        if (string.IsNullOrEmpty(invocationId))
            throw new ArgumentException("invocationId required", nameof(invocationId));
        var body = new List<byte>(48);
        WriteArrayHeader(body, 5);
        body.Add(0x03);                  // [0] type = 3 (Completion)
        body.Add(0x80);                  // [1] headers
        WriteString(body, invocationId); // [2] invocationId
        body.Add(0x03);                  // [3] resultKind = 3 (non-void)
        WriteValue(body, result);        // [4] result

        Span<byte> lenBuf = stackalloc byte[5];
        int lenLen = WriteLengthPrefix(body.Count, lenBuf);
        var resultBytes = new byte[lenLen + body.Count];
        for (int i = 0; i < lenLen; i++) resultBytes[i] = lenBuf[i];
        body.CopyTo(resultBytes, lenLen);
        return resultBytes;
    }

    public static byte[] WriteCompletionError(string invocationId, string error)
    {
        var body = new List<byte>(48);
        WriteArrayHeader(body, 5);
        body.Add(0x03);
        body.Add(0x80);
        WriteString(body, invocationId);
        body.Add(0x01);                  // resultKind = 1 (error)
        WriteString(body, error);

        Span<byte> lenBuf = stackalloc byte[5];
        int lenLen = WriteLengthPrefix(body.Count, lenBuf);
        var resultBytes = new byte[lenLen + body.Count];
        for (int i = 0; i < lenLen; i++) resultBytes[i] = lenBuf[i];
        body.CopyTo(resultBytes, lenLen);
        return resultBytes;
    }

    // ─── SignalR binary framing ──────────────────────────────────────────

    public static int WriteLengthPrefix(int length, Span<byte> dest)
    {
        // 7-bit VLQ, little-endian: low 7 bits first, top bit set = more.
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        int i = 0;
        do
        {
            byte b = (byte)(length & 0x7F);
            length >>= 7;
            if (length > 0) b |= 0x80;
            dest[i++] = b;
        } while (length > 0);
        return i;
    }

    // ─── MessagePack primitives ──────────────────────────────────────────

    internal static void WriteArrayHeader(List<byte> sink, int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (count <= 15)
        {
            sink.Add((byte)(0x90 | count));
        }
        else if (count <= ushort.MaxValue)
        {
            sink.Add(0xDC);
            sink.Add((byte)(count >> 8));
            sink.Add((byte)count);
        }
        else
        {
            sink.Add(0xDD);
            sink.Add((byte)(count >> 24));
            sink.Add((byte)(count >> 16));
            sink.Add((byte)(count >> 8));
            sink.Add((byte)count);
        }
    }

    internal static void WriteString(List<byte> sink, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        int len = bytes.Length;
        if (len <= 31)
        {
            sink.Add((byte)(0xA0 | len));
        }
        else if (len <= byte.MaxValue)
        {
            sink.Add(0xD9);
            sink.Add((byte)len);
        }
        else if (len <= ushort.MaxValue)
        {
            sink.Add(0xDA);
            sink.Add((byte)(len >> 8));
            sink.Add((byte)len);
        }
        else
        {
            sink.Add(0xDB);
            sink.Add((byte)(len >> 24));
            sink.Add((byte)(len >> 16));
            sink.Add((byte)(len >> 8));
            sink.Add((byte)len);
        }
        for (int i = 0; i < bytes.Length; i++) sink.Add(bytes[i]);
    }

    internal static void WriteBinary(List<byte> sink, ReadOnlySpan<byte> data)
    {
        int len = data.Length;
        if (len <= byte.MaxValue)
        {
            sink.Add(0xC4);
            sink.Add((byte)len);
        }
        else if (len <= ushort.MaxValue)
        {
            sink.Add(0xC5);
            sink.Add((byte)(len >> 8));
            sink.Add((byte)len);
        }
        else
        {
            sink.Add(0xC6);
            sink.Add((byte)(len >> 24));
            sink.Add((byte)(len >> 16));
            sink.Add((byte)(len >> 8));
            sink.Add((byte)len);
        }
        for (int i = 0; i < data.Length; i++) sink.Add(data[i]);
    }

    internal static void WriteInt64(List<byte> sink, long v)
    {
        if (v >= 0)
        {
            ulong u = (ulong)v;
            if (u <= 127) { sink.Add((byte)u); return; }
            if (u <= byte.MaxValue) { sink.Add(0xCC); sink.Add((byte)u); return; }
            if (u <= ushort.MaxValue)
            {
                sink.Add(0xCD);
                sink.Add((byte)(u >> 8)); sink.Add((byte)u); return;
            }
            if (u <= uint.MaxValue)
            {
                sink.Add(0xCE);
                Span<byte> b4 = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(b4, (uint)u);
                for (int i = 0; i < 4; i++) sink.Add(b4[i]);
                return;
            }
            sink.Add(0xCF);
            Span<byte> b8 = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(b8, u);
            for (int i = 0; i < 8; i++) sink.Add(b8[i]);
        }
        else
        {
            // Negative fixint range: -32..-1 => 0xE0..0xFF
            if (v >= -32) { sink.Add((byte)(sbyte)v); return; }
            if (v >= sbyte.MinValue) { sink.Add(0xD0); sink.Add((byte)(sbyte)v); return; }
            if (v >= short.MinValue)
            {
                sink.Add(0xD1);
                short s = (short)v;
                sink.Add((byte)(s >> 8)); sink.Add((byte)s); return;
            }
            if (v >= int.MinValue)
            {
                sink.Add(0xD2);
                Span<byte> b4 = stackalloc byte[4];
                BinaryPrimitives.WriteInt32BigEndian(b4, (int)v);
                for (int i = 0; i < 4; i++) sink.Add(b4[i]);
                return;
            }
            sink.Add(0xD3);
            Span<byte> b8 = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(b8, v);
            for (int i = 0; i < 8; i++) sink.Add(b8[i]);
        }
    }

    internal static void WriteBool(List<byte> sink, bool b) => sink.Add(b ? (byte)0xC3 : (byte)0xC2);

    internal static void WriteValue(List<byte> sink, object? v)
    {
        switch (v)
        {
            case null: sink.Add(0xC0); break;
            case bool b: WriteBool(sink, b); break;
            case sbyte sb: WriteInt64(sink, sb); break;
            case byte by: WriteInt64(sink, by); break;
            case short sh: WriteInt64(sink, sh); break;
            case ushort us: WriteInt64(sink, us); break;
            case int i: WriteInt64(sink, i); break;
            case uint u: WriteInt64(sink, u); break;
            case long l: WriteInt64(sink, l); break;
            case string s: WriteString(sink, s); break;
            case byte[] arr: WriteBinary(sink, arr); break;
            case ArraySegment<byte> seg: WriteBinary(sink, seg.AsSpan()); break;
            case ReadOnlyMemory<byte> rom: WriteBinary(sink, rom.Span); break;
            default:
                throw new NotSupportedException(
                    $"BlazorPackWriter does not encode {v.GetType().FullName}. " +
                    "Supported types match the 6 outbound JS.* call sites; add a new case if Blazor 10.x grows another.");
        }
    }
}
