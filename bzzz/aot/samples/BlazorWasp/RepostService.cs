using System;
using System.Collections.Generic;
using Wasp.IcCdk;

namespace WaspSample.BlazorWasp;

/// <summary>
/// F2 FEED kind — reposts. A repost is (reposterPrincipalText → srcMessageId):
/// the signed caller amplifies an existing post without copying it. Powers the
/// repost COUNT + the viewer's "you reposted" toggle (X-style), and a future
/// home-feed surfacing. Additive region; never touches CHAT/SRVR.
///
/// Region at offset 95M, magic "RPST" v1, header-below-data idiom, RegionEnd
/// guard 100M.
///   uint32 magic "RPST"   uint32 version 1   uint32 count
///   foreach: int32 utf8len(reposter); bytes reposter; int64 srcMessageId
/// </summary>
public sealed unsafe class RepostService
{
    private const ulong Offset = 95_000_000;
    private const ulong MagicOffset = Offset - 8;
    private const ulong RegionEnd = 100_000_000;
    private const uint Magic = 0x52505354;   // "RPST"
    private const uint Version = 1;
    private const int MaxReposts = 60_000;   // ~75 B/entry × 60k ≈ 4.5 MB < 5 MB region

    // srcMessageId -> set of reposter principal texts
    private Dictionary<long, HashSet<string>>? _byMsg;
    private int _count;

    public bool HasReposted(string reposter, long msgId)
    {
        if (string.IsNullOrEmpty(reposter) || msgId <= 0) return false;
        var map = Load();
        return map.TryGetValue(msgId, out var set) && set.Contains(reposter);
    }

    public int RepostCount(long msgId)
    {
        if (msgId <= 0) return 0;
        var map = Load();
        return map.TryGetValue(msgId, out var set) ? set.Count : 0;
    }

    /// <summary>The message ids this principal has reposted (for surfacing a
    /// followed user's reposts in the following-home).</summary>
    public IReadOnlyList<long> RepostsBy(string reposter)
    {
        var list = new List<long>();
        if (string.IsNullOrEmpty(reposter)) return list;
        foreach (var kv in Load()) if (kv.Value.Contains(reposter)) list.Add(kv.Key);
        return list;
    }

    /// <summary>Toggle a repost on/off. Returns true on success (incl. no-op),
    /// false only when a NEW repost is rejected at capacity — the caller must
    /// surface that so the change isn't silently dropped on the next load.</summary>
    public bool SetRepost(string reposter, long msgId, bool on)
    {
        if (string.IsNullOrEmpty(reposter) || msgId <= 0) return false;
        var map = Load();
        if (on)
        {
            if (map.TryGetValue(msgId, out var s0) && s0.Contains(reposter)) return true;   // already reposted
            if (_count >= MaxReposts) return false;                                         // capacity — reject
            if (!map.TryGetValue(msgId, out var set)) { set = new HashSet<string>(); map[msgId] = set; }
            set.Add(reposter); _count++;
        }
        else
        {
            if (!map.TryGetValue(msgId, out var set) || !set.Remove(reposter)) return true; // wasn't reposted
            _count--;
        }
        Persist();   // always persist a real change; Persist self-guards RegionEnd
        return true;
    }

    // ─── storage ──────────────────────────────────────────────────────
    private Dictionary<long, HashSet<string>> Load()
    {
        if (_byMsg is not null) return _byMsg;
        EnsureGrown(Offset);
        _byMsg = new Dictionary<long, HashSet<string>>();
        _count = 0;
        if (ReadU32(MagicOffset) != Magic || ReadU32(MagicOffset + 4) != Version)
            return _byMsg;
        var r = new MemoryReader(Offset);
        var count = (int)r.ReadU32();
        if (count < 0 || count > MaxReposts) { _byMsg = new(); return _byMsg; }
        for (int i = 0; i < count; i++)
        {
            var reposter = r.ReadString();
            var mid = r.ReadI64();
            if (string.IsNullOrEmpty(reposter) || mid <= 0) continue;
            if (!_byMsg.TryGetValue(mid, out var set)) { set = new HashSet<string>(); _byMsg[mid] = set; }
            if (set.Add(reposter)) _count++;
        }
        return _byMsg;
    }

    private void Persist()
    {
        var map = _byMsg!;
        var buf = new MemoryWriter();
        var entries = new List<(string r, long m)>();
        foreach (var kv in map) foreach (var rp in kv.Value) entries.Add((rp, kv.Key));
        buf.WriteU32((uint)entries.Count);
        foreach (var e in entries) { buf.WriteString(e.r); buf.WriteI64(e.m); }
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
        public void WriteI64(long v) => _buf.AddRange(BitConverter.GetBytes(v));
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
        public long ReadI64() { var b = new byte[8]; ReadBytes(Cursor, b); Cursor += 8; return BitConverter.ToInt64(b); }
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
