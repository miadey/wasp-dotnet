using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Wasp.AspNetCore.Blazor.Server;

// Inbound mirror of BlazorPackWriter. Parses a single framed BlazorPack
// InvocationMessage produced by blazor.web.js into (target, args[]).
//
// Implements only the subset of MessagePack values that the 13 inbound
// ComponentHub methods carry (docs/m4-s2-circuit-coupling.md "Inbound
// seam"): string, long, int, bool, byte[], nullable string. The S4
// source-gen dispatch table consumes (target, args[]) and unboxes
// positionally.
//
// Spec sources: msgpack-spec
// (https://github.com/msgpack/msgpack/blob/master/spec.md) +
// dotnet/aspnetcore SignalR BinaryMessageFormatter for the 7-bit VLQ
// length prefix.
public static class BlazorPackReader
{
    public sealed class DecodedInvocation
    {
        public DecodedInvocation(int type, string? invocationId, string target, object?[] arguments)
        {
            Type = type;
            InvocationId = invocationId;
            Target = target;
            Arguments = arguments;
        }
        public int Type { get; }
        public string? InvocationId { get; }
        public string Target { get; }
        public object?[] Arguments { get; }
    }

    public sealed class DecodedCompletion
    {
        public DecodedCompletion(string? invocationId, string? errorMessage, bool hasResult, object? result)
        {
            InvocationId = invocationId;
            ErrorMessage = errorMessage;
            HasResult = hasResult;
            Result = result;
        }
        public string? InvocationId { get; }
        public string? ErrorMessage { get; }
        public bool HasResult { get; }
        public object? Result { get; }
    }

    /// <summary>
    /// Parse a complete framed Completion message (type=3).
    /// Body shape per SignalR Hub protocol:
    /// [3, headers, invocationId, resultKind, errorOrResult]
    ///   resultKind = 1 (error: next field is string)
    ///                2 (void:  no next field)
    ///                3 (non-void: next field is the result value)
    /// </summary>
    public static DecodedCompletion ReadCompletion(ReadOnlySpan<byte> frame)
    {
        int offset = 0;
        int bodyLen = ReadLengthPrefix(frame, ref offset);
        if (bodyLen > frame.Length - offset)
            throw new FormatException($"length prefix claims {bodyLen} bytes but only {frame.Length - offset} remain");
        var body = frame.Slice(offset, bodyLen);
        int p = 0;

        int arrayLen = ReadArrayHeader(body, ref p);
        if (arrayLen < 4)
            throw new FormatException($"CompletionMessage array must have >= 4 elements, got {arrayLen}");

        int type = (int)ReadInt64(body, ref p);
        if (type != 3)
            throw new FormatException($"expected Completion message type=3, got {type}");

        SkipValue(body, ref p);                              // [1] headers
        string? invocationId = ReadStringOrNull(body, ref p); // [2]
        int resultKind = (int)ReadInt64(body, ref p);        // [3]

        string? error = null;
        bool hasResult = false;
        object? result = null;

        switch (resultKind)
        {
            case 1: // error
                if (arrayLen < 5) throw new FormatException("error Completion missing payload");
                error = ReadString(body, ref p);
                break;
            case 2: // void
                break;
            case 3: // non-void
                if (arrayLen < 5) throw new FormatException("non-void Completion missing result");
                hasResult = true;
                result = ReadValue(body, ref p);
                break;
            default:
                throw new FormatException($"unknown Completion resultKind={resultKind}");
        }

        return new DecodedCompletion(invocationId, error, hasResult, result);
    }

    /// <summary>
    /// Parse a complete framed message. Reads exactly one (length-prefix +
    /// body) tuple. Throws FormatException on malformed input.
    /// </summary>
    public static DecodedInvocation ReadInvocation(ReadOnlySpan<byte> frame)
    {
        int offset = 0;
        int bodyLen = ReadLengthPrefix(frame, ref offset);
        if (bodyLen > frame.Length - offset)
            throw new FormatException($"length prefix claims {bodyLen} bytes but only {frame.Length - offset} remain");

        var body = frame.Slice(offset, bodyLen);
        int p = 0;

        int arrayLen = ReadArrayHeader(body, ref p);
        if (arrayLen < 5)
            throw new FormatException($"InvocationMessage array must have >= 5 elements, got {arrayLen}");

        // [0] type
        int type = (int)ReadInt64(body, ref p);

        // [1] headers map -- we don't surface headers; skip
        SkipValue(body, ref p);

        // [2] invocationId (string or nil)
        string? invocationId = ReadStringOrNull(body, ref p);

        // [3] target
        string target = ReadString(body, ref p)
            ?? throw new FormatException("target must be a non-nil string");

        // [4] args
        int argCount = ReadArrayHeader(body, ref p);
        var args = new object?[argCount];
        for (int i = 0; i < argCount; i++) args[i] = ReadValue(body, ref p);

        // [5] streamIds (optional in array)
        if (arrayLen >= 6) SkipValue(body, ref p);

        return new DecodedInvocation(type, invocationId, target, args);
    }

    // ─── SignalR varint ──────────────────────────────────────────────────

    public static int ReadLengthPrefix(ReadOnlySpan<byte> buf, ref int offset)
    {
        int value = 0;
        int shift = 0;
        for (int i = 0; i < 5; i++)
        {
            if (offset >= buf.Length) throw new FormatException("varint truncated");
            byte b = buf[offset++];
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
            shift += 7;
        }
        throw new FormatException("varint > 5 bytes");
    }

    // ─── MessagePack primitives ──────────────────────────────────────────

    public static int ReadArrayHeader(ReadOnlySpan<byte> buf, ref int p)
    {
        byte t = buf[p++];
        if ((t & 0xF0) == 0x90) return t & 0x0F;
        if (t == 0xDC) { int len = (buf[p] << 8) | buf[p + 1]; p += 2; return len; }
        if (t == 0xDD)
        {
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.Slice(p, 4));
            p += 4;
            return len;
        }
        throw new FormatException($"expected array header, got 0x{t:X2}");
    }

    internal static int ReadMapHeader(ReadOnlySpan<byte> buf, ref int p)
    {
        byte t = buf[p++];
        if ((t & 0xF0) == 0x80) return t & 0x0F;
        if (t == 0xDE) { int len = (buf[p] << 8) | buf[p + 1]; p += 2; return len; }
        if (t == 0xDF)
        {
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.Slice(p, 4));
            p += 4;
            return len;
        }
        throw new FormatException($"expected map header, got 0x{t:X2}");
    }

    public static long ReadInt64(ReadOnlySpan<byte> buf, ref int p)
    {
        byte t = buf[p++];
        if ((t & 0x80) == 0x00) return t;                          // positive fixint
        if ((t & 0xE0) == 0xE0) return (sbyte)t;                   // negative fixint
        switch (t)
        {
            case 0xCC: return buf[p++];                            // uint8
            case 0xCD: { int v = (buf[p] << 8) | buf[p + 1]; p += 2; return v; }
            case 0xCE: { uint v = BinaryPrimitives.ReadUInt32BigEndian(buf.Slice(p, 4)); p += 4; return v; }
            case 0xCF: { ulong v = BinaryPrimitives.ReadUInt64BigEndian(buf.Slice(p, 8)); p += 8;
                         if (v > long.MaxValue) throw new FormatException("uint64 overflows long"); return (long)v; }
            case 0xD0: return (sbyte)buf[p++];                     // int8
            case 0xD1: { short v = (short)((buf[p] << 8) | buf[p + 1]); p += 2; return v; }
            case 0xD2: { int v = BinaryPrimitives.ReadInt32BigEndian(buf.Slice(p, 4)); p += 4; return v; }
            case 0xD3: { long v = BinaryPrimitives.ReadInt64BigEndian(buf.Slice(p, 8)); p += 8; return v; }
        }
        throw new FormatException($"expected int, got 0x{t:X2}");
    }

    internal static string? ReadString(ReadOnlySpan<byte> buf, ref int p)
    {
        byte t = buf[p];
        if (t == 0xC0) { p++; return null; }
        p++;
        int len;
        if ((t & 0xE0) == 0xA0) len = t & 0x1F;                    // fixstr
        else if (t == 0xD9) { len = buf[p++]; }
        else if (t == 0xDA) { len = (buf[p] << 8) | buf[p + 1]; p += 2; }
        else if (t == 0xDB)
        {
            len = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.Slice(p, 4));
            p += 4;
        }
        else throw new FormatException($"expected string, got 0x{t:X2}");
        var s = Encoding.UTF8.GetString(buf.Slice(p, len));
        p += len;
        return s;
    }

    internal static string? ReadStringOrNull(ReadOnlySpan<byte> buf, ref int p) => ReadString(buf, ref p);

    internal static byte[] ReadBinary(ReadOnlySpan<byte> buf, ref int p)
    {
        byte t = buf[p++];
        int len;
        if (t == 0xC4) len = buf[p++];
        else if (t == 0xC5) { len = (buf[p] << 8) | buf[p + 1]; p += 2; }
        else if (t == 0xC6)
        {
            len = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.Slice(p, 4));
            p += 4;
        }
        else throw new FormatException($"expected bin, got 0x{t:X2}");
        var data = buf.Slice(p, len).ToArray();
        p += len;
        return data;
    }

    internal static bool ReadBool(ReadOnlySpan<byte> buf, ref int p)
    {
        byte t = buf[p++];
        return t switch
        {
            0xC2 => false,
            0xC3 => true,
            _ => throw new FormatException($"expected bool, got 0x{t:X2}"),
        };
    }

    /// <summary>
    /// Decode the next value into a CLR object. Returns int for fixint values
    /// that fit, long otherwise; string for str types; byte[] for bin types;
    /// bool for booleans; null for nil.
    /// </summary>
    public static object? ReadValue(ReadOnlySpan<byte> buf, ref int p)
    {
        byte t = buf[p];
        if (t == 0xC0) { p++; return null; }
        if (t == 0xC2 || t == 0xC3) return ReadBool(buf, ref p);
        if (t == 0xC4 || t == 0xC5 || t == 0xC6) return ReadBinary(buf, ref p);
        if ((t & 0xE0) == 0xA0 || t == 0xD9 || t == 0xDA || t == 0xDB) return ReadString(buf, ref p);
        // Int family (positive fixint, negative fixint, uint8/16/32/64, int8/16/32/64)
        if ((t & 0x80) == 0x00 || (t & 0xE0) == 0xE0
            || (t >= 0xCC && t <= 0xCF) || (t >= 0xD0 && t <= 0xD3))
        {
            return ReadInt64(buf, ref p);
        }
        // Maps / arrays / extensions are not used by ComponentHub args.
        throw new NotSupportedException($"BlazorPackReader does not decode 0x{t:X2}; extend if a new Hub arg type appears.");
    }

    /// <summary>Advance past one msgpack value without materializing it (for headers map).</summary>
    internal static void SkipValue(ReadOnlySpan<byte> buf, ref int p)
    {
        byte t = buf[p];
        if (t == 0xC0 || t == 0xC2 || t == 0xC3) { p++; return; }

        if ((t & 0x80) == 0x00 || (t & 0xE0) == 0xE0) { p++; return; }
        if (t >= 0xCC && t <= 0xCF) { ReadInt64(buf, ref p); return; }
        if (t >= 0xD0 && t <= 0xD3) { ReadInt64(buf, ref p); return; }

        if ((t & 0xE0) == 0xA0 || t == 0xD9 || t == 0xDA || t == 0xDB) { ReadString(buf, ref p); return; }
        if (t == 0xC4 || t == 0xC5 || t == 0xC6) { ReadBinary(buf, ref p); return; }

        if ((t & 0xF0) == 0x90 || t == 0xDC || t == 0xDD)
        {
            int n = ReadArrayHeader(buf, ref p);
            for (int i = 0; i < n; i++) SkipValue(buf, ref p);
            return;
        }
        if ((t & 0xF0) == 0x80 || t == 0xDE || t == 0xDF)
        {
            int n = ReadMapHeader(buf, ref p);
            for (int i = 0; i < n; i++) { SkipValue(buf, ref p); SkipValue(buf, ref p); }
            return;
        }
        throw new NotSupportedException($"SkipValue: unknown tag 0x{t:X2}");
    }
}
