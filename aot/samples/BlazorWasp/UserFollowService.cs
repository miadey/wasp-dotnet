using System;
using System.Collections.Generic;
using Wasp.IcCdk;

namespace WaspSample.BlazorWasp;

/// <summary>
/// F2-A USER follows — follow a PERSON (principal), distinct from following a FEED
/// (see <see cref="FollowService"/>). This is what lets reposts surface: a post
/// reposted by someone you follow shows up in your following-home. Stored as
/// (followerPrincipalText → followeePrincipalText). Additive NEW region (does not
/// touch the deployed FLLW region); self-sovereign — the follower is the signed
/// msg_caller.
///
/// Region at offset 100M, magic "UFOL" v1, header-below-data idiom, RegionEnd 110M.
///   uint32 magic "UFOL"  uint32 version 1  uint32 count
///   foreach: int32 len(follower); bytes; int32 len(followee); bytes
/// </summary>
public sealed unsafe class UserFollowService
{
    private const ulong Offset = 100_000_000;
    private const ulong MagicOffset = Offset - 8;
    private const ulong RegionEnd = 110_000_000;
    private const uint Magic = 0x55464F4C;   // "UFOL"
    private const uint Version = 1;
    private const int MaxFollows = 60_000;

    // follower -> set of followee principals
    private Dictionary<string, HashSet<string>>? _byFollower;
    private int _count;

    public bool IsFollowing(string follower, string followee)
    {
        if (string.IsNullOrEmpty(follower) || string.IsNullOrEmpty(followee)) return false;
        var map = Load();
        return map.TryGetValue(follower, out var set) && set.Contains(followee);
    }

    public int FollowerCount(string followee)
    {
        if (string.IsNullOrEmpty(followee)) return 0;
        int n = 0;
        foreach (var kv in Load()) if (kv.Value.Contains(followee)) n++;
        return n;
    }

    public IReadOnlyList<string> FollowedOf(string follower)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(follower)) return list;
        if (Load().TryGetValue(follower, out var set)) list.AddRange(set);
        return list;
    }

    /// <summary>Follow/unfollow a person. Returns true on success (incl. no-op),
    /// false only when a NEW follow is rejected at capacity (caller surfaces it).
    /// Self-follow (follower==followee) is rejected as a no-op success.</summary>
    public bool SetFollow(string follower, string followee, bool on)
    {
        if (string.IsNullOrEmpty(follower) || string.IsNullOrEmpty(followee)) return false;
        if (follower == followee) return true;   // can't follow yourself — silent no-op
        var map = Load();
        if (on)
        {
            if (map.TryGetValue(follower, out var s0) && s0.Contains(followee)) return true;
            if (_count >= MaxFollows) return false;
            if (!map.TryGetValue(follower, out var set)) { set = new HashSet<string>(); map[follower] = set; }
            set.Add(followee); _count++;
        }
        else
        {
            if (!map.TryGetValue(follower, out var set) || !set.Remove(followee)) return true;
            _count--;
        }
        Persist();
        return true;
    }

    // ─── storage ──────────────────────────────────────────────────────
    private Dictionary<string, HashSet<string>> Load()
    {
        if (_byFollower is not null) return _byFollower;
        EnsureGrown(Offset);
        _byFollower = new Dictionary<string, HashSet<string>>();
        _count = 0;
        if (ReadU32(MagicOffset) != Magic || ReadU32(MagicOffset + 4) != Version)
            return _byFollower;
        var r = new MemoryReader(Offset);
        var count = (int)r.ReadU32();
        if (count < 0 || count > MaxFollows) { _byFollower = new(); return _byFollower; }
        for (int i = 0; i < count; i++)
        {
            var follower = r.ReadString();
            var followee = r.ReadString();
            if (string.IsNullOrEmpty(follower) || string.IsNullOrEmpty(followee)) continue;
            if (!_byFollower.TryGetValue(follower, out var set)) { set = new HashSet<string>(); _byFollower[follower] = set; }
            if (set.Add(followee)) _count++;
        }
        return _byFollower;
    }

    private void Persist()
    {
        var map = _byFollower!;
        var buf = new MemoryWriter();
        var entries = new List<(string a, string b)>();
        foreach (var kv in map) foreach (var f in kv.Value) entries.Add((kv.Key, f));
        buf.WriteU32((uint)entries.Count);
        foreach (var e in entries) { buf.WriteString(e.a); buf.WriteString(e.b); }
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
