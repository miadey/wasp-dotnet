using System;
using System.Buffers.Binary;

namespace Wasp.IcCdk;

// Stable-memory layout (Phase 1, single-cell only):
//
//   Cells are addressed by `memoryId` (0..255). Each cell occupies a
//   fixed slot in the first wasm page: `offset = memoryId * SlotSize`.
//   This is the simplest layout that lets multiple StableCell<T>
//   instances coexist without colliding. Phase 2 will introduce a
//   stable-memory allocator that allocates dynamic-size regions.
//
//   Slot layout (32 bytes):
//     [0..8]   u64 magic 0x57415350444F544E ("WASPDOTN")
//     [8..16]  u64 size in bytes of the cell's payload (must be ≤ 16)
//     [16..32] up to 16 bytes payload (T fits in a u128)
//
// 32-byte slots × 256 ids = 8 KiB total — well under one 64 KiB page.

public static class StableMemory
{
    public const ulong PageSize = 65536;
    public const ulong SlotSize = 32;
    public const ulong MaxSlots = 256;
    public const ulong PayloadOffset = 16;
    public const ulong MaxPayload = SlotSize - PayloadOffset;
    private const ulong Magic = 0x4E_54_4F_44_50_53_41_57UL; // "WASPDOTN" little-endian

    /// <summary>Ensure stable memory has at least one page allocated.</summary>
    public static unsafe void EnsureBootstrap()
    {
        if (Ic0.stable64_size() == 0)
        {
            ulong oldPages = Ic0.stable64_grow(1);
            if (oldPages == ulong.MaxValue) Reply.Trap("stable64_grow(1) failed during bootstrap");
        }
    }

    internal static ulong SlotOffset(byte memoryId) => (ulong)memoryId * SlotSize;

    internal static unsafe bool TryReadPayload(byte memoryId, Span<byte> dst, out ulong size)
    {
        EnsureBootstrap();
        Span<byte> hdr = stackalloc byte[16];
        fixed (byte* p = hdr) Ic0.stable64_read((ulong)(nint)p, SlotOffset(memoryId), 16);
        ulong magic = BinaryPrimitives.ReadUInt64LittleEndian(hdr);
        size = BinaryPrimitives.ReadUInt64LittleEndian(hdr.Slice(8));
        if (magic != Magic) return false;
        if (size > (ulong)dst.Length) Reply.Trap($"StableMemory slot {memoryId}: payload {size} > buffer {dst.Length}");
        if (size > 0)
        {
            fixed (byte* p = dst) Ic0.stable64_read((ulong)(nint)p, SlotOffset(memoryId) + PayloadOffset, size);
        }
        return true;
    }

    internal static unsafe void WritePayload(byte memoryId, ReadOnlySpan<byte> payload)
    {
        if ((ulong)payload.Length > MaxPayload) Reply.Trap($"StableMemory slot {memoryId}: payload {payload.Length} > {MaxPayload}");
        EnsureBootstrap();

        Span<byte> hdr = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(hdr, Magic);
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(8), (ulong)payload.Length);
        fixed (byte* p = hdr) Ic0.stable64_write(SlotOffset(memoryId), (ulong)(nint)p, 16);
        if (!payload.IsEmpty)
        {
            fixed (byte* p = payload) Ic0.stable64_write(SlotOffset(memoryId) + PayloadOffset, (ulong)(nint)p, (ulong)payload.Length);
        }
    }
}

// Typed wrapper: a single cell holding a value of type T. Reads + writes
// hit stable memory directly — no in-memory cache so post-upgrade reads
// always see the persisted value.
public sealed class StableCell<T> where T : unmanaged
{
    private readonly byte _memoryId;

    public StableCell(byte memoryId)
    {
        _memoryId = memoryId;
    }

    public unsafe T Value
    {
        get
        {
            int size = sizeof(T);
            Span<byte> buf = stackalloc byte[Math.Max(size, 1)];
            if (!StableMemory.TryReadPayload(_memoryId, buf, out ulong written) || (int)written != size)
            {
                return default;
            }
            return System.Runtime.InteropServices.MemoryMarshal.Read<T>(buf.Slice(0, size));
        }
        set
        {
            int size = sizeof(T);
            Span<byte> buf = stackalloc byte[size];
            System.Runtime.InteropServices.MemoryMarshal.Write(buf, in value);
            StableMemory.WritePayload(_memoryId, buf);
        }
    }
}
