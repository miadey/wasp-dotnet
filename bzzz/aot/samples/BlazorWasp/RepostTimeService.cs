using System;
using System.Collections.Generic;
using Wasp.IcCdk;

namespace WaspSample.BlazorWasp;

/// <summary>
/// F2-A repost TIMESTAMPS — when a (reposter, post) repost happened, so a repost
/// can BUMP the post to the top of a follower's home. Kept in a NEW region rather
/// than widening <see cref="RepostService"/> (RPST @95M is already near its 5 MB
/// cap and a Version bump would wipe it). RPST stays the membership/count source;
/// this is a parallel (reposter,msgId) → ms map. Additive, zero migration: a repost
/// with no recorded time reads back 0 and the home falls back to the post's AtMs.
///
/// Region at offset 110M, magic "RPTS" v1, header-below-data idiom, RegionEnd 120M.
///   uint32 magic "RPTS"  uint32 version 1  uint32 count
///   foreach: int32 len(reposter); bytes; int64 msgId; int64 ms
/// </summary>
public sealed unsafe class RepostTimeService
{
    private const ulong Offset = 110_000_000;
    private const ulong MagicOffset = Offset - 8;
    private const ulong RegionEnd = 120_000_000;
    private const uint Magic = 0x52505453;   // "RPTS"
    private const uint Version = 1;
    private const int MaxEntries = 60_000;   // 1:1 with RepostService.MaxReposts

    // msgId -> (reposter principal text -> repost ms)
    private Dictionary<long, Dictionary<string, long>>? _byMsg;
    private int _count;

    /// <summary>Record (upsert) when this principal reposted this post.</summary>
    public void SetRepostTime(string reposter, long msgId, long ms)
    {
        if (string.IsNullOrEmpty(reposter) || msgId <= 0 || ms <= 0) return;
        var map = Load();
        if (!map.TryGetValue(msgId, out var inner)) { inner = new Dictionary<string, long>(); map[msgId] = inner; }
        if (!inner.ContainsKey(reposter))
        {
            if (_count >= MaxEntries) return;   // capacity — skip (post still ranks by AtMs)
            _count++;
        }
        inner[reposter] = ms;
        Persist();
    }

    /// <summary>Drop the timestamp when a repost is undone.</summary>
    public void ClearRepostTime(string reposter, long msgId)
    {
        if (string.IsNullOrEmpty(reposter) || msgId <= 0) return;
        var map = Load();
        if (map.TryGetValue(msgId, out var inner) && inner.Remove(reposter))
        {
            _count--;
            if (inner.Count == 0) map.Remove(msgId);
            Persist();
        }
    }

    /// <summary>The ms at which this principal reposted this post (0 if none).</summary>
    public long RepostTimeOf(string reposter, long msgId)
    {
        if (string.IsNullOrEmpty(reposter) || msgId <= 0) return 0;
        var map = Load();
        return map.TryGetValue(msgId, out var inner) && inner.TryGetValue(reposter, out var ms) ? ms : 0;
    }

    // ─── storage ──────────────────────────────────────────────────────
    private Dictionary<long, Dictionary<string, long>> Load()
    {
        if (_byMsg is not null) return _byMsg;
        EnsureGrown(Offset);
        _byMsg = new Dictionary<long, Dictionary<string, long>>();
        _count = 0;
        if (ReadU32(MagicOffset) != Magic || ReadU32(MagicOffset + 4) != Version)
            return _byMsg;
        var r = new MemoryReader(Offset);
        var count = (int)r.ReadU32();
        if (count < 0 || count > MaxEntries) { _byMsg = new(); return _byMsg; }
        for (int i = 0; i < count; i++)
        {
            var reposter = r.ReadString();
            var mid = r.ReadI64();
            var ms = r.ReadI64();
            if (string.IsNullOrEmpty(reposter) || mid <= 0 || ms <= 0) continue;
            if (!_byMsg.TryGetValue(mid, out var inner)) { inner = new Dictionary<string, long>(); _byMsg[mid] = inner; }
            if (!inner.ContainsKey(reposter)) _count++;
            inner[reposter] = ms;
        }
        return _byMsg;
    }

    private void Persist()
    {
        var map = _byMsg!;
        var buf = new MemoryWriter();
        var entries = new List<(string r, long m, long ts)>();
        foreach (var kv in map) foreach (var e in kv.Value) entries.Add((e.Key, kv.Key, e.Value));
        buf.WriteU32((uint)entries.Count);
        foreach (var e in entries) { buf.WriteString(e.r); buf.WriteI64(e.m); buf.WriteI64(e.ts); }
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
        public int  ReadI32() { var b = new byte[4]; ReadBytes(Cursor, b); Cursor += 4; return BitConverter.ToInt32(b); }
        public long ReadI64() { var b = new byte[8]; ReadBytes(Cursor, b); Cursor += 8; return BitConverter.ToInt64(b); }
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
