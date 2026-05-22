using System;
using System.Text;
using Wasp.IcCdk;

namespace Wasp.OrthogonalPersistence;

/// <summary>
/// Single-chunk stable-memory snapshot. Layout at offset
/// <c>StableOffset</c>:
///   uint32  magic ("WAOP" = 0x57414f50)
///   uint32  version (1)
///   uint32  utf8 byte length
///   bytes   JSON payload
///
/// The chunk grows on write — pages are added in 64 KB increments as
/// needed.
/// </summary>
internal static unsafe class Storage
{
    private const uint Magic = 0x57414f50;  // "WAOP"
    private const uint Version = 1;
    private const int HeaderLength = 12;

    internal static void Write(ulong offset, string json)
    {
        var data = Encoding.UTF8.GetBytes(json);
        ulong needed = offset + (ulong)HeaderLength + (ulong)data.Length;
        ulong haveBytes = Ic0.stable64_size() * 65536UL;
        if (needed > haveBytes)
        {
            ulong morePages = ((needed - haveBytes) + 65535UL) / 65536UL;
            Ic0.stable64_grow(morePages);
        }
        Span<byte> header = stackalloc byte[HeaderLength];
        BitConverter.TryWriteBytes(header, Magic);
        BitConverter.TryWriteBytes(header.Slice(4), Version);
        BitConverter.TryWriteBytes(header.Slice(8), data.Length);
        fixed (byte* p = header) Ic0.stable64_write(offset, (ulong)(nint)p, (ulong)HeaderLength);
        fixed (byte* p = data) Ic0.stable64_write(offset + (ulong)HeaderLength, (ulong)(nint)p, (ulong)data.Length);
    }

    internal static string? Read(ulong offset)
    {
        if (Ic0.stable64_size() == 0) return null;
        Span<byte> header = stackalloc byte[HeaderLength];
        fixed (byte* p = header) Ic0.stable64_read((ulong)(nint)p, offset, (ulong)HeaderLength);
        if (BitConverter.ToUInt32(header) != Magic) return null;
        if (BitConverter.ToUInt32(header.Slice(4)) != Version) return null;
        var length = BitConverter.ToInt32(header.Slice(8));
        if (length <= 0 || length > 64 * 1024 * 1024) return null;  // 64 MB cap
        var data = new byte[length];
        fixed (byte* p = data) Ic0.stable64_read((ulong)(nint)p, offset + (ulong)HeaderLength, (ulong)length);
        return Encoding.UTF8.GetString(data);
    }
}
