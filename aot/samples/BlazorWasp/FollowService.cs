using System;
using System.Collections.Generic;
using Wasp.IcCdk;

namespace WaspSample.BlazorWasp;

/// <summary>
/// F2 FEED kind — who follows which feed (a feed = a Feed-kind ServerService
/// server with a single "timeline" room). A follow is (followerPrincipalText →
/// followeeServerId). Drives follower counts now and a personalized home feed
/// later. Additive region, keyed independently of ServerService; never touches
/// CHAT/SRVR/FRUM. Self-sovereign: the follower is the signed msg_caller.
///
/// Region at offset 90M, magic "FLLW" v1, header-below-data idiom, RegionEnd
/// guard 95M.
///   uint32 magic "FLLW"   uint32 version 1   uint32 count
///   foreach: int32 utf8len(follower); bytes follower; int32 followeeServerId
/// </summary>
public sealed unsafe class FollowService
{
    private const ulong Offset = 90_000_000;
    private const ulong MagicOffset = Offset - 8;
    private const ulong RegionEnd = 95_000_000;
    private const uint Magic = 0x464C4C57;   // "FLLW"
    private const uint Version = 1;
    private const int MaxFollows = 60_000;   // ~71 B/entry × 60k ≈ 4.3 MB < 5 MB region

    // serverId -> set of follower principal texts
    private Dictionary<int, HashSet<string>>? _byServer;
    private int _count;

    public bool IsFollowing(string follower, int serverId)
    {
        if (string.IsNullOrEmpty(follower) || serverId <= 0) return false;
        var map = Load();
        return map.TryGetValue(serverId, out var set) && set.Contains(follower);
    }

    public int FollowerCount(int serverId)
    {
        if (serverId <= 0) return 0;
        var map = Load();
        return map.TryGetValue(serverId, out var set) ? set.Count : 0;
    }

    /// <summary>Set follow on/off. Returns true on success (incl. no-op), false
    /// only when a NEW follow is rejected because capacity is reached — the caller
    /// must surface that so the change isn't silently dropped on the next load.</summary>
    public bool SetFollow(string follower, int serverId, bool on)
    {
        if (string.IsNullOrEmpty(follower) || serverId <= 0) return false;
        var map = Load();
        if (on)
        {
            if (map.TryGetValue(serverId, out var s0) && s0.Contains(follower)) return true;   // already following
            if (_count >= MaxFollows) return false;                                            // capacity — reject
            if (!map.TryGetValue(serverId, out var set)) { set = new HashSet<string>(); map[serverId] = set; }
            set.Add(follower); _count++;
        }
        else
        {
            if (!map.TryGetValue(serverId, out var set) || !set.Remove(follower)) return true;  // wasn't following
            _count--;
        }
        Persist();   // always persist a real change; Persist self-guards RegionEnd
        return true;
    }

    public IReadOnlyList<int> FollowingOf(string follower)
    {
        var list = new List<int>();
        if (string.IsNullOrEmpty(follower)) return list;
        foreach (var kv in Load())
            if (kv.Value.Contains(follower)) list.Add(kv.Key);
        return list;
    }

    // ─── storage ──────────────────────────────────────────────────────
    private Dictionary<int, HashSet<string>> Load()
    {
        if (_byServer is not null) return _byServer;
        EnsureGrown(Offset);
        _byServer = new Dictionary<int, HashSet<string>>();
        _count = 0;
        if (ReadU32(MagicOffset) != Magic || ReadU32(MagicOffset + 4) != Version)
            return _byServer;
        var r = new MemoryReader(Offset);
        var count = (int)r.ReadU32();
        if (count < 0 || count > MaxFollows) { _byServer = new(); return _byServer; }
        for (int i = 0; i < count; i++)
        {
            var follower = r.ReadString();
            var sid = r.ReadI32();
            if (string.IsNullOrEmpty(follower) || sid <= 0) continue;
            if (!_byServer.TryGetValue(sid, out var set)) { set = new HashSet<string>(); _byServer[sid] = set; }
            if (set.Add(follower)) _count++;
        }
        return _byServer;
    }

    private void Persist()
    {
        var map = _byServer!;
        var buf = new MemoryWriter();
        var entries = new List<(string f, int s)>();
        foreach (var kv in map) foreach (var f in kv.Value) entries.Add((f, kv.Key));
        buf.WriteU32((uint)entries.Count);
        foreach (var e in entries) { buf.WriteString(e.f); buf.WriteI32(e.s); }
        var bytes = buf.ToArray();
        if (Offset + (ulong)bytes.Length > RegionEnd - 8) return;   // reserve top 8 bytes for the next region magic header
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
        public void WriteString(string s)
        {
            var data = System.Text.Encoding.UTF8.GetBytes(s ?? "");
            _buf.AddRange(BitConverter.GetBytes(data.Length));
            _buf.AddRange(data);
        }
    }
    private sealed class MemoryReader
    {
        public ulong Cursor { get; private set; }
        public MemoryReader(ulong offset) { Cursor = offset; }
        public uint ReadU32() { var b = new byte[4]; ReadBytes(Cursor, b); Cursor += 4; return BitConverter.ToUInt32(b); }
        public int  ReadI32() { var b = new byte[4]; ReadBytes(Cursor, b); Cursor += 4; return BitConverter.ToInt32(b); }
        public string ReadString()
        {
            var len = ReadI32();
            if (len <= 0) return string.Empty;
            var b = new byte[len];
            ReadBytes(Cursor, b);
            Cursor += (ulong)len;
            return System.Text.Encoding.UTF8.GetString(b);
        }
    }
}
