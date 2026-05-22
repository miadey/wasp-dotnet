using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Wasp.IcCdk;

// Phase 2 stable-memory layout: variable-size bump allocator that coexists
// with the Phase-1 StableCell slots.
//
// Memory map:
//   [0          .. 8 KiB)   Phase-1 StableCell slots (256 slots × 32 bytes)
//   [8192       .. 8200)    StableRegion header: u64 next_alloc_offset
//   [8200       .. ∞)       Allocations, each:
//                              u64 size      (8 bytes, little-endian)
//                              payload       (size bytes)
//
// `Alloc(data)` returns the **payload offset** (the byte just past the size
// header). `Read(offset)` reverses by reading the u64 at offset-8 to recover
// the length.
//
// What this is NOT:
//   - Not garbage-collected. Each Alloc bumps the high-water mark; rewriting
//     a per-principal snapshot leaks the old region. A future iteration
//     should add free-list or compaction; tracked in M4.S5 followup.
//   - Not thread-safe. The IC canister model is single-threaded per-message,
//     which is the constraint we're targeting.
//
// Testability: takes an IStableMemoryBackend so tests can inject an in-memory
// fake without touching real ic0_stable64_* imports.
public sealed class StableRegion
{
    public const ulong HeaderOffset = 8192;        // first byte after StableCell slots
    public const ulong PayloadBase = HeaderOffset + 8; // skip the next_offset u64
    private const ulong AllocHeaderSize = 8;       // u64 size prefix per allocation

    private readonly IStableMemoryBackend _backend;
    private bool _initialized;

    public StableRegion() : this(Ic0StableMemoryBackend.Instance) { }

    public StableRegion(IStableMemoryBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    /// <summary>
    /// Reserve `size` bytes of storage, write the payload, and return the
    /// offset of the payload's first byte. The returned offset is what
    /// callers should remember to read the data back later.
    /// </summary>
    public ulong Alloc(ReadOnlySpan<byte> payload)
    {
        EnsureInitialized();
        ulong nextOffset = ReadU64(HeaderOffset);
        ulong needed = AllocHeaderSize + (ulong)payload.Length;
        EnsureCapacity(nextOffset + needed);

        // Write [size : u64][payload].
        Span<byte> hdr = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(hdr, (ulong)payload.Length);
        _backend.Write(nextOffset, hdr);
        ulong payloadOffset = nextOffset + AllocHeaderSize;
        if (!payload.IsEmpty) _backend.Write(payloadOffset, payload);

        // Bump the high-water mark.
        WriteU64(HeaderOffset, nextOffset + needed);
        return payloadOffset;
    }

    /// <summary>
    /// Read the allocation at `payloadOffset`, returning its bytes.
    /// </summary>
    public byte[] Read(ulong payloadOffset)
    {
        EnsureInitialized();
        if (payloadOffset < PayloadBase)
            throw new ArgumentOutOfRangeException(nameof(payloadOffset),
                $"offset {payloadOffset} is in the header region (< {PayloadBase})");

        ulong size = ReadU64(payloadOffset - AllocHeaderSize);
        if (size > int.MaxValue)
            throw new InvalidOperationException($"allocation size {size} exceeds Int32.MaxValue");
        var buf = new byte[(int)size];
        if (size > 0) _backend.Read(payloadOffset, buf);
        return buf;
    }

    /// <summary>High-water allocation offset (bytes from start of stable memory).</summary>
    public ulong UsedBytes
    {
        get
        {
            EnsureInitialized();
            return ReadU64(HeaderOffset);
        }
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;
        _backend.EnsureCapacity(PayloadBase);
        // Fresh stable memory is zero-initialized; the header u64 reads as 0,
        // which is the "uninitialized" sentinel. Seed it to PayloadBase so
        // the first allocation lands cleanly past the header.
        ulong next = ReadU64(HeaderOffset);
        if (next < PayloadBase)
        {
            WriteU64(HeaderOffset, PayloadBase);
        }
        _initialized = true;
    }

    private void EnsureCapacity(ulong neededBytes)
    {
        _backend.EnsureCapacity(neededBytes);
    }

    private ulong ReadU64(ulong offset)
    {
        Span<byte> buf = stackalloc byte[8];
        _backend.Read(offset, buf);
        return BinaryPrimitives.ReadUInt64LittleEndian(buf);
    }

    private void WriteU64(ulong offset, ulong value)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buf, value);
        _backend.Write(offset, buf);
    }
}

// ─── Backend abstraction ─────────────────────────────────────────────────

public interface IStableMemoryBackend
{
    /// <summary>Grow the underlying stable memory if needed so the byte
    /// at offset `requiredOffset - 1` is addressable.</summary>
    void EnsureCapacity(ulong requiredOffset);

    void Read(ulong offset, Span<byte> destination);
    void Write(ulong offset, ReadOnlySpan<byte> source);
}

// Real backend wiring through ic0 stable64_* imports. Single-canister-call
// semantics, single-threaded — no synchronization needed.
public sealed class Ic0StableMemoryBackend : IStableMemoryBackend
{
    public static readonly Ic0StableMemoryBackend Instance = new();

    public void EnsureCapacity(ulong requiredOffset)
    {
        const ulong PageBytes = 65536;
        ulong haveBytes = Ic0.stable64_size() * PageBytes;
        if (haveBytes >= requiredOffset) return;

        ulong missingBytes = requiredOffset - haveBytes;
        ulong missingPages = (missingBytes + PageBytes - 1) / PageBytes;
        ulong old = Ic0.stable64_grow(missingPages);
        if (old == ulong.MaxValue) Reply.Trap($"stable64_grow({missingPages}) failed");
    }

    public unsafe void Read(ulong offset, Span<byte> destination)
    {
        if (destination.IsEmpty) return;
        fixed (byte* p = destination) Ic0.stable64_read((ulong)(nint)p, offset, (ulong)destination.Length);
    }

    public unsafe void Write(ulong offset, ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty) return;
        fixed (byte* p = source) Ic0.stable64_write(offset, (ulong)(nint)p, (ulong)source.Length);
    }
}

// In-memory backend for unit tests. Auto-grows like the real ic0 backend.
public sealed class InMemoryStableMemoryBackend : IStableMemoryBackend
{
    private byte[] _buffer = Array.Empty<byte>();

    public void EnsureCapacity(ulong requiredOffset)
    {
        if ((ulong)_buffer.Length >= requiredOffset) return;
        ulong newSize = Math.Max(requiredOffset, (ulong)_buffer.Length * 2);
        if (newSize > int.MaxValue) throw new OutOfMemoryException("test backend > int.MaxValue");
        Array.Resize(ref _buffer, (int)newSize);
    }

    public void Read(ulong offset, Span<byte> destination)
    {
        if (destination.IsEmpty) return;
        EnsureCapacity(offset + (ulong)destination.Length);
        _buffer.AsSpan((int)offset, destination.Length).CopyTo(destination);
    }

    public void Write(ulong offset, ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty) return;
        EnsureCapacity(offset + (ulong)source.Length);
        source.CopyTo(_buffer.AsSpan((int)offset, source.Length));
    }

    public byte[] Snapshot() => _buffer.AsSpan(0, _buffer.Length).ToArray();
    public int Capacity => _buffer.Length;
}
