using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Wasp.IcCdk;

namespace WaspSample.BlazorWasp;

/// <summary>
/// Tetris high-score leaderboard backed by stable memory.
///
/// Layout at offset 2_000_000 (safely past ImageStore at 1 MB):
///   uint32 magic "TETR"
///   uint32 version (1)
///   uint32 count
///   foreach entry (kept sorted by score desc):
///     int32  utf8len(username)
///     bytes  username
///     int64  score
///     int32  lines
///     int32  level
///     int64  atMs
///
/// Capped at 100 entries — once full, a new score either bumps the
/// lowest existing one (if higher) or is silently dropped.
/// </summary>
public sealed unsafe class TetrisService
{
    private const ulong BaseOffset = 2_000_000;
    private const uint  Magic = 0x54455452;   // "TETR"
    private const uint  Version = 1;
    private const int   MaxEntries = 100;

    public sealed record Entry(string Username, long Score, int Lines, int Level, long AtMs);

    private List<Entry>? _entries;

    public IReadOnlyList<Entry> Top(int limit)
    {
        EnsureLoaded();
        return _entries!.Take(limit).ToList();
    }

    public int? Submit(string username, long score, int lines, int level)
    {
        EnsureLoaded();
        username = SanitiseUsername(username);
        if (score <= 0) return null;
        // Per-user single entry — keep only the user's best.
        var existing = _entries!.FindIndex(e => string.Equals(e.Username, username, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0 && _entries[existing].Score >= score)
        {
            return existing < 0 ? null : (int?)(existing + 1);
        }
        if (existing >= 0) _entries.RemoveAt(existing);
        var entry = new Entry(username, score, lines, level, NowMs());
        // Insert maintaining desc sort.
        var pos = _entries.FindIndex(e => e.Score < score);
        if (pos < 0) _entries.Add(entry);
        else         _entries.Insert(pos, entry);
        if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        Persist();
        var rank = _entries.IndexOf(entry) + 1;
        return rank;
    }

    // ─── storage ─────────────────────────────────────────────────────
    private void EnsureLoaded()
    {
        if (_entries is not null) return;
        EnsurePages(BaseOffset + 12);
        var hdr = new byte[12];
        ReadBytes(BaseOffset, hdr);
        var magic = BitConverter.ToUInt32(hdr, 0);
        var ver   = BitConverter.ToUInt32(hdr, 4);
        var count = (int)BitConverter.ToUInt32(hdr, 8);
        if (magic != Magic || ver != Version || count < 0 || count > MaxEntries)
        {
            _entries = new List<Entry>();
            Persist();
            return;
        }
        _entries = new List<Entry>(count);
        ulong cursor = BaseOffset + 12;
        for (int i = 0; i < count; i++)
        {
            var lenBytes = new byte[4];
            ReadBytes(cursor, lenBytes);
            var ulen = BitConverter.ToInt32(lenBytes, 0);
            if (ulen < 0 || ulen > 100) break;
            cursor += 4;
            var ub = new byte[ulen];
            ReadBytes(cursor, ub);
            cursor += (ulong)ulen;
            var username = Encoding.UTF8.GetString(ub);
            var tail = new byte[8 + 4 + 4 + 8];
            ReadBytes(cursor, tail);
            cursor += (ulong)tail.Length;
            var score = BitConverter.ToInt64(tail, 0);
            var lines = BitConverter.ToInt32(tail, 8);
            var level = BitConverter.ToInt32(tail, 12);
            var atMs  = BitConverter.ToInt64(tail, 16);
            _entries.Add(new Entry(username, score, lines, level, atMs));
        }
    }

    private void Persist()
    {
        var buf = new List<byte>();
        buf.AddRange(BitConverter.GetBytes(Magic));
        buf.AddRange(BitConverter.GetBytes(Version));
        buf.AddRange(BitConverter.GetBytes((uint)(_entries?.Count ?? 0)));
        foreach (var e in _entries!)
        {
            var ub = Encoding.UTF8.GetBytes(e.Username);
            buf.AddRange(BitConverter.GetBytes(ub.Length));
            buf.AddRange(ub);
            buf.AddRange(BitConverter.GetBytes(e.Score));
            buf.AddRange(BitConverter.GetBytes(e.Lines));
            buf.AddRange(BitConverter.GetBytes(e.Level));
            buf.AddRange(BitConverter.GetBytes(e.AtMs));
        }
        WriteBytes(BaseOffset, buf.ToArray());
    }

    // ─── helpers ─────────────────────────────────────────────────────
    private static long NowMs() => (long)(Ic0.time() / 1_000_000UL);

    private static string SanitiseUsername(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Anonymous";
        raw = raw.Trim();
        if (raw.Length > 24) raw = raw.Substring(0, 24);
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw) if (c >= 0x20 && c < 0x10000) sb.Append(c);
        return sb.Length == 0 ? "Anonymous" : sb.ToString();
    }

    private static void EnsurePages(ulong needed)
    {
        ulong have = Ic0.stable64_size() * 65536UL;
        if (needed > have) Ic0.stable64_grow(((needed - have) + 65535UL) / 65536UL);
    }
    private static void ReadBytes(ulong offset, byte[] buf)
    { fixed (byte* p = buf) Ic0.stable64_read((ulong)(nint)p, offset, (ulong)buf.Length); }
    private static void WriteBytes(ulong offset, byte[] buf)
    {
        EnsurePages(offset + (ulong)buf.Length);
        fixed (byte* p = buf) Ic0.stable64_write(offset, (ulong)(nint)p, (ulong)buf.Length);
    }
}
