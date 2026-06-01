using System;
using System.Collections.Generic;
using Wasp.IcCdk;

namespace WaspSample.BlazorWasp;

/// <summary>
/// The KIND of a server, chosen once at create time: a live-chat
/// <see cref="ServerKind.Discussion"/> (today's behaviour), a Discourse-style
/// <see cref="ServerKind.Forum"/> (category → topics → posts), or an
/// X-style <see cref="ServerKind.Feed"/> (a reverse-chron timeline of posts).
///
/// ADDITIVE by construction. The kind is stored in THIS new region keyed by
/// serverId — it never touches ServerService's "SRVR" record (offset 6M,
/// v1), so existing servers' persisted layout stays byte-identical and the
/// live mainnet servers survive --mode upgrade. ABSENCE of an entry ⇒
/// <see cref="ServerKind.Discussion"/>, so every pre-existing server AND the
/// virtual default server 0 read back as Discussion with ZERO migration and
/// zero bytes written until someone actually creates a forum/feed server.
///
/// Kind is IMMUTABLE after create in this slice (changing kind would orphan
/// forum/feed-specific state). Server 0 is always Discussion; SetKind on it
/// is refused (mirrors MembershipService's serverId&lt;=0 guard).
///
/// Stable layout at Offset (header is the universal idiom: magic(4)+version(4)
/// eight bytes BELOW the data offset):
///   uint32 magic "SKND"            (at MagicOffset)
///   uint32 version 1               (at MagicOffset+4)
///   uint32 entryCount              (at Offset)
///   foreach entry: int32 serverId, int32 kind
/// </summary>
public sealed unsafe class ServerKindService
{
    private const ulong Offset = 80_000_000;
    private const ulong MagicOffset = Offset - 8;       // magic(4)+version(4)
    private const ulong RegionEnd = 81_000_000;         // hard guard; ForumService (FRUM) starts at 81M
    private const uint Magic = 0x534B4E44;              // "SKND"
    private const uint Version = 1;
    private const int MaxEntries = 4000;                // bounded write guard

    private Dictionary<int, ServerKind>? _kinds;

    // ─── Public API ───────────────────────────────────────────────────

    /// <summary>The kind of a server. Absent / server 0 ⇒ Discussion.</summary>
    public ServerKind KindOf(int serverId)
    {
        if (serverId <= ServerService.DefaultServerId) return ServerKind.Discussion;
        var map = Load();
        return map.TryGetValue(serverId, out var k) ? k : ServerKind.Discussion;
    }

    /// <summary>Set a server's kind. Rejects the virtual default (id 0) and
    /// negative ids. Idempotent. Intended to be called ONCE inside the signed
    /// create-server handler, right after CreateServer + SetOwner.</summary>
    public bool SetKind(int serverId, ServerKind kind)
    {
        if (serverId <= ServerService.DefaultServerId) return false;
        var map = Load();
        if (map.TryGetValue(serverId, out var existing) && existing == kind) return true;
        if (!map.ContainsKey(serverId) && map.Count >= MaxEntries) return false;
        map[serverId] = kind;
        Persist();
        return true;
    }

    // ─── storage ──────────────────────────────────────────────────────
    private Dictionary<int, ServerKind> Load()
    {
        if (_kinds is not null) return _kinds;
        EnsureGrown(Offset);
        _kinds = new Dictionary<int, ServerKind>();
        if (ReadU32(MagicOffset) != Magic || ReadU32(MagicOffset + 4) != Version)
            return _kinds;
        var r = new MemoryReader(Offset);
        var count = (int)r.ReadU32();
        if (count < 0 || count > MaxEntries) { _kinds = new(); return _kinds; }
        for (int i = 0; i < count; i++)
        {
            var id = r.ReadI32();
            var kindRaw = r.ReadI32();
            var kind = kindRaw switch
            {
                (int)ServerKind.Forum => ServerKind.Forum,
                (int)ServerKind.Feed  => ServerKind.Feed,
                _                     => ServerKind.Discussion,
            };
            if (id > ServerService.DefaultServerId) _kinds[id] = kind;
        }
        return _kinds;
    }

    private void Persist()
    {
        var map = _kinds!;
        var buf = new MemoryWriter();
        buf.WriteU32((uint)map.Count);
        foreach (var kv in map)
        {
            buf.WriteI32(kv.Key);
            buf.WriteI32((int)kv.Value);
        }
        var bytes = buf.ToArray();
        if (Offset + (ulong)bytes.Length > RegionEnd - 8) return;   // reserve top 8 bytes for the next region magic header   // refuse to overrun the region
        WriteBytes(Offset, bytes);
        WriteU32(MagicOffset, Magic);
        WriteU32(MagicOffset + 4, Version);
    }

    // ─── stable-memory primitives (ServerService idiom) ───────────────
    private static uint ReadU32(ulong offset) { var b = new byte[4]; ReadBytes(offset, b); return BitConverter.ToUInt32(b); }
    private static void WriteU32(ulong offset, uint v) => WriteBytes(offset, BitConverter.GetBytes(v));

    private static void EnsureGrown(ulong dataOffset)
    {
        ulong needed = dataOffset + 65536UL;
        ulong haveBytes = Ic0.stable64_size() * 65536UL;
        if (needed > haveBytes) Ic0.stable64_grow(((needed - haveBytes) + 65535UL) / 65536UL);
    }
    private static void ReadBytes(ulong offset, byte[] buf)
    { fixed (byte* p = buf) Ic0.stable64_read((ulong)(nint)p, offset, (ulong)buf.Length); }
    private static void WriteBytes(ulong offset, byte[] buf)
    {
        ulong needed = offset + (ulong)buf.Length;
        ulong haveBytes = Ic0.stable64_size() * 65536UL;
        if (needed > haveBytes) Ic0.stable64_grow(((needed - haveBytes) + 65535UL) / 65536UL);
        fixed (byte* p = buf) Ic0.stable64_write(offset, (ulong)(nint)p, (ulong)buf.Length);
    }

    private sealed class MemoryWriter
    {
        private readonly List<byte> _buf = new();
        public byte[] ToArray() => _buf.ToArray();
        public void WriteU32(uint v) => _buf.AddRange(BitConverter.GetBytes(v));
        public void WriteI32(int v) => _buf.AddRange(BitConverter.GetBytes(v));
    }

    private sealed class MemoryReader
    {
        public ulong Cursor { get; private set; }
        public MemoryReader(ulong offset) { Cursor = offset; }
        public uint ReadU32() { var b = new byte[4]; ReadBytes(Cursor, b); Cursor += 4; return BitConverter.ToUInt32(b); }
        public int  ReadI32() { var b = new byte[4]; ReadBytes(Cursor, b); Cursor += 4; return BitConverter.ToInt32(b); }
    }
}

/// <summary>The three server kinds chosen at creation. 0 == Discussion is the
/// implicit default for every pre-existing server (absent from the SKND map).</summary>
public enum ServerKind { Discussion = 0, Forum = 1, Feed = 2 }
