using System;
using System.Collections.Generic;
using Wasp.IcCdk;

namespace WaspSample.BlazorWasp;

/// <summary>
/// Identity-weighted VOTING — one vote per principal per message, up (+1) or
/// down (-1); a message's SCORE is the sum. Used by the forum (vote on the OP +
/// replies) to rank quality independently of emoji reactions. New additive
/// region; never touches any existing region.
///
/// Region at offset 120M, magic "VOTE" v1, header-below-data idiom, RegionEnd
/// 130M. (next free region after this = 130M+)
///   uint32 magic "VOTE"  uint32 version 1  uint32 count
///   foreach: int64 msgId; int32 len(principal); bytes principal; int32 dir(-1|+1)
/// </summary>
public sealed unsafe class VoteService
{
    private const ulong Offset = 120_000_000;
    private const ulong MagicOffset = Offset - 8;
    private const ulong RegionEnd = 130_000_000;
    private const uint Magic = 0x564F5445;   // "VOTE"
    private const uint Version = 1;
    private const int MaxVotes = 100_000;

    // msgId -> (principal text -> dir +1/-1)
    private Dictionary<long, Dictionary<string, int>>? _byMsg;
    private int _count;

    /// <summary>The caller's current vote on a message: +1, -1, or 0 (none).</summary>
    public int VoteOf(string principal, long msgId)
    {
        if (string.IsNullOrEmpty(principal) || msgId <= 0) return 0;
        var map = Load();
        return map.TryGetValue(msgId, out var inner) && inner.TryGetValue(principal, out var d) ? d : 0;
    }

    /// <summary>Net score of a message = sum of all up/down votes.</summary>
    public int Score(long msgId)
    {
        if (msgId <= 0) return 0;
        var map = Load();
        if (!map.TryGetValue(msgId, out var inner)) return 0;
        int s = 0; foreach (var d in inner.Values) s += d;
        return s;
    }

    /// <summary>Set the caller's vote: dir +1 (up) / -1 (down) / 0 (clear).
    /// One vote per principal — re-voting the same direction is idempotent;
    /// the opposite flips it; 0 removes it. Returns true on success, false only
    /// when a brand-new vote is rejected at capacity.</summary>
    public bool SetVote(string principal, long msgId, int dir)
    {
        if (string.IsNullOrEmpty(principal) || msgId <= 0) return false;
        dir = dir > 0 ? 1 : (dir < 0 ? -1 : 0);
        var map = Load();
        bool changed = false;
        if (dir == 0)
        {
            if (map.TryGetValue(msgId, out var inner) && inner.Remove(principal))
            { _count--; if (inner.Count == 0) map.Remove(msgId); changed = true; }
        }
        else
        {
            if (!map.TryGetValue(msgId, out var inner)) { inner = new Dictionary<string, int>(); map[msgId] = inner; }
            if (inner.TryGetValue(principal, out var cur))
            { if (cur != dir) { inner[principal] = dir; changed = true; } }
            else
            {
                if (_count >= MaxVotes) { if (inner.Count == 0) map.Remove(msgId); return false; }
                inner[principal] = dir; _count++; changed = true;
            }
        }
        if (changed) Persist();
        return true;
    }

    // ─── storage ──────────────────────────────────────────────────────
    private Dictionary<long, Dictionary<string, int>> Load()
    {
        if (_byMsg is not null) return _byMsg;
        EnsureGrown(Offset);
        _byMsg = new Dictionary<long, Dictionary<string, int>>();
        _count = 0;
        if (ReadU32(MagicOffset) != Magic || ReadU32(MagicOffset + 4) != Version)
            return _byMsg;
        var r = new MemoryReader(Offset);
        var count = (int)r.ReadU32();
        if (count < 0 || count > MaxVotes) { _byMsg = new(); return _byMsg; }
        for (int i = 0; i < count; i++)
        {
            var mid = r.ReadI64();
            var principal = r.ReadString();
            var dir = r.ReadI32();
            if (mid <= 0 || string.IsNullOrEmpty(principal) || (dir != 1 && dir != -1)) continue;
            if (!_byMsg.TryGetValue(mid, out var inner)) { inner = new Dictionary<string, int>(); _byMsg[mid] = inner; }
            if (!inner.ContainsKey(principal)) _count++;
            inner[principal] = dir;
        }
        return _byMsg;
    }

    private void Persist()
    {
        var map = _byMsg!;
        var buf = new MemoryWriter();
        var entries = new List<(long m, string p, int d)>();
        foreach (var kv in map) foreach (var e in kv.Value) entries.Add((kv.Key, e.Key, e.Value));
        buf.WriteU32((uint)entries.Count);
        foreach (var e in entries) { buf.WriteI64(e.m); buf.WriteString(e.p); buf.WriteI32(e.d); }
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
