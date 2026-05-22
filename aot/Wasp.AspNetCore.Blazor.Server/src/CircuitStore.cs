using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Wasp.IcCdk;

namespace Wasp.AspNetCore.Blazor.Server;

// Per-principal circuit-state snapshot store backed by StableRegion.
//
// Lifecycle:
//   - Snapshot(principal, state): allocates a fresh region, writes the state
//     bytes, updates the in-memory principal→offset index, and serializes
//     the index into a second StableRegion allocation so it survives
//     post-upgrade.
//   - Restore(principal): looks the principal up in the in-memory index;
//     returns the state bytes or null if no snapshot exists.
//   - LoadFromStable(): called by the canister's post_upgrade entry point —
//     finds the latest index allocation and rebuilds the in-memory map.
//
// Index serialization format (little-endian):
//   u32 entryCount
//   entryCount × {
//     u8  principalLen
//     principalLen bytes
//     u64 stateOffset       ← payload offset in StableRegion
//     u64 stateLength       ← length stored at offset (also recoverable via
//                              StableRegion.Read, but cached here so Restore
//                              doesn't need to know StableRegion internals)
//   }
//
// What this is NOT:
//   - No compaction. Each Snapshot leaks the old region; over time the
//     stable memory footprint grows. Acceptable for MVP; tracked in M4.S5
//     followup.
//   - No upgrade-time integrity check beyond "the magic is right and the
//     entryCount parses sanely". A corruption-recovery strategy is deferred.
public sealed class CircuitStore
{
    private const uint IndexMagic = 0x43_49_52_43u; // "CIRC"

    private readonly StableRegion _region;
    private readonly Dictionary<byte[], SnapshotRef> _index = new(PrincipalEqualityComparer.Instance);
    private ulong _latestIndexOffset; // most recent serialized-index allocation

    public CircuitStore() : this(new StableRegion()) { }

    public CircuitStore(StableRegion region)
    {
        _region = region ?? throw new ArgumentNullException(nameof(region));
    }

    /// <summary>Persist a fresh snapshot for the given principal.</summary>
    public void Save(byte[] clientPrincipal, ReadOnlySpan<byte> state)
    {
        if (clientPrincipal is null) throw new ArgumentNullException(nameof(clientPrincipal));
        if (clientPrincipal.Length > 255) throw new ArgumentException("principal > 255 bytes", nameof(clientPrincipal));

        ulong stateOffset = _region.Alloc(state);
        _index[clientPrincipal] = new SnapshotRef(stateOffset, (ulong)state.Length);
        PersistIndex();
    }

    /// <summary>Get the most recently snapshotted bytes for the principal.</summary>
    public byte[]? Restore(byte[] clientPrincipal)
    {
        if (!_index.TryGetValue(clientPrincipal, out var snap)) return null;
        return _region.Read(snap.Offset);
    }

    /// <summary>Number of principals currently with a snapshot.</summary>
    public int Count => _index.Count;

    /// <summary>
    /// Rebuild the in-memory index from the latest serialized version stored
    /// in stable memory. Call from the canister's post_upgrade hook.
    ///
    /// Returns the number of entries restored. Returns 0 (and leaves the
    /// in-memory index empty) if no valid index allocation is found.
    /// </summary>
    public int LoadFromStable(ulong indexOffset)
    {
        if (indexOffset < StableRegion.PayloadBase) return 0;
        byte[] raw = _region.Read(indexOffset);
        if (!TryDeserializeIndex(raw, out var rebuilt)) return 0;

        _index.Clear();
        foreach (var (principal, snap) in rebuilt)
        {
            _index[principal] = snap;
        }
        _latestIndexOffset = indexOffset;
        return _index.Count;
    }

    /// <summary>
    /// Last persisted index offset — canister authors should save this in a
    /// StableCell&lt;ulong&gt; so post_upgrade knows where to call
    /// LoadFromStable from.
    /// </summary>
    public ulong LatestIndexOffset => _latestIndexOffset;

    // ─── Index (de)serialization ─────────────────────────────────────────

    private void PersistIndex()
    {
        // Layout: [magic u32][count u32] then (u8 len, bytes, u64 offset, u64 size)*.
        int sizeBytes = 4 + 4;
        foreach (var (p, _) in _index)
        {
            sizeBytes += 1 + p.Length + 8 + 8;
        }
        var buf = new byte[sizeBytes];
        var span = buf.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, IndexMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4), (uint)_index.Count);
        int pos = 8;
        foreach (var (p, snap) in _index)
        {
            buf[pos++] = (byte)p.Length;
            p.AsSpan().CopyTo(span.Slice(pos, p.Length));
            pos += p.Length;
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(pos), snap.Offset);
            pos += 8;
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(pos), snap.Length);
            pos += 8;
        }

        _latestIndexOffset = _region.Alloc(buf);
    }

    private static bool TryDeserializeIndex(
        byte[] raw, out List<(byte[] principal, SnapshotRef snap)> rebuilt)
    {
        rebuilt = new List<(byte[], SnapshotRef)>();
        if (raw.Length < 8) return false;

        var span = raw.AsSpan();
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(span);
        if (magic != IndexMagic) return false;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4));
        int pos = 8;
        for (uint i = 0; i < count; i++)
        {
            if (pos + 1 > raw.Length) return false;
            int len = raw[pos++];
            if (pos + len + 16 > raw.Length) return false;
            var principal = raw.AsSpan(pos, len).ToArray();
            pos += len;
            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(pos));
            pos += 8;
            ulong size = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(pos));
            pos += 8;
            rebuilt.Add((principal, new SnapshotRef(offset, size)));
        }
        return true;
    }

    public readonly record struct SnapshotRef(ulong Offset, ulong Length);

    private sealed class PrincipalEqualityComparer : IEqualityComparer<byte[]>
    {
        public static readonly PrincipalEqualityComparer Instance = new();
        public bool Equals(byte[]? x, byte[]? y)
            => x is null ? y is null : y is not null && x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] obj)
        {
            var hc = new HashCode();
            hc.AddBytes(obj);
            return hc.ToHashCode();
        }
    }
}
