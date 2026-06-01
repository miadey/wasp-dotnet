using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Wasp.IcCdk;

namespace WaspSample.BlazorWasp;

/// <summary>
/// Public message THREADS — a side-conversation anchored to a parent chat
/// message. Anyone can read or post (same open trust model as the main
/// chat; the author name is cosmetic, exactly like a chat username). A
/// thread is opened from the reply ↩ action or the 💬 reply-count badge,
/// and rendered in the right-hand panel (replacing the members list).
///
/// Additive stable store at offset 7_000_000 with its own magic+version
/// header (header sits 8 bytes below the data offset, matching the
/// AdminService/IdentityService/DmService convention). It NEVER touches
/// ChatService's region — threads are keyed by the parent message's id but
/// stored entirely here, so the live chat layout at offset 4096 is
/// unchanged and survives an --mode upgrade.
///
/// Layout (at <see cref="Offset"/>):
///   uint32 magic ("THRD")        ── at MagicOffset
///   uint32 version (1)           ── at MagicOffset+4
///   uint32 threadCount
///   foreach thread:
///     int64  parentMsgId
///     uint32 messageCount        (FIFO-capped at MaxMsgsPerThread)
///     foreach message:
///       int64  atMs
///       int32  utf8len(username); bytes username
///       int32  utf8len(text);     bytes text
/// </summary>
public sealed unsafe class ThreadService
{
    // Moved up from 7_000_000 to leave DMs (6.5M) a wide ~9.4MB runway and a
    // hard guard between the two, so neither store can overrun the other.
    private const ulong Offset = 16_000_000;
    private const ulong MagicOffset = Offset - 8;   // magic(4)+version(4)
    private const uint Magic = 0x54485244;          // "THRD" (legacy: 2 strings/msg)
    private const uint Magic2 = 0x54485232;         // "THR2" (M2-B: +authorPrincipal, 3 strings/msg)
    private const uint Version = 1;
    // Threads own [16M, RegionEnd). Persist refuses to write past this, so the
    // store can never grow into whatever region comes next (next free >= 32M).
    private const ulong RegionEnd = 31_000_000;

    private const int MaxMsgsPerThread = 500;       // FIFO cap per thread
    private const int MaxTextLen = 2000;
    private const int MaxNameLen = 24;

    public sealed record ThreadMsg(string Username, long AtMs, string Text, string AuthorPrincipal = "")
    {
        public bool IsOwned => AuthorPrincipal.Length > 0;   // verified II author ("" = legacy)
    }

    private readonly ChatService _chat;
    public ThreadService(ChatService chat) { _chat = chat; }

    private Dictionary<long, List<ThreadMsg>>? _threads;

    // ─── public API (open, no auth — same as the main chat) ───────────

    /// <summary>Append a reply to the thread anchored on <paramref name="parentMsgId"/>.</summary>
    public bool Post(long parentMsgId, string? username, string? text, string authorPrincipal = "")
    {
        if (parentMsgId <= 0) return false;
        // Parent-liveness: only thread on a real, still-present chat message.
        // This also structurally caps the number of distinct threads to the
        // set of live messages, so the key space can't be spammed unbounded.
        if (_chat.FindMessage(parentMsgId) is null) return false;
        text = SanitiseText(text);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var name = SanitiseName(username);
        var threads = Load();
        if (!threads.TryGetValue(parentMsgId, out var msgs)) { msgs = new(); threads[parentMsgId] = msgs; }
        msgs.Add(new ThreadMsg(name, NowMs(), text, authorPrincipal));
        if (msgs.Count > MaxMsgsPerThread)
            threads[parentMsgId] = msgs.GetRange(msgs.Count - MaxMsgsPerThread, MaxMsgsPerThread);
        // Region-end guard: if this write would overrun Threads' region, reject
        // it and drop the in-memory mutation (reload the last good state on next
        // access) rather than corrupt the neighbouring region.
        if (!Persist()) { _threads = null; return false; }
        return true;
    }

    /// <summary>All replies in the thread, oldest first. Empty if none.</summary>
    public IReadOnlyList<ThreadMsg> Read(long parentMsgId)
        => Load().TryGetValue(parentMsgId, out var msgs) ? msgs.ToArray() : Array.Empty<ThreadMsg>();

    /// <summary>Reply count for one parent message (for the 💬 badge).</summary>
    public int Count(long parentMsgId)
        => Load().TryGetValue(parentMsgId, out var msgs) ? msgs.Count : 0;

    /// <summary>parentMsgId → reply count, for badging a whole room at once.</summary>
    public IReadOnlyDictionary<long, int> Counts()
        => Load().ToDictionary(kv => kv.Key, kv => kv.Value.Count);

    // ─── sanitising ───────────────────────────────────────────────────
    private static string SanitiseText(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        raw = raw.Replace('', ' ').Replace('', ' ').Trim();
        if (raw.Length > MaxTextLen) raw = raw.Substring(0, MaxTextLen);
        return raw;
    }
    private static string SanitiseName(string? raw)
    {
        var n = (raw ?? "").Replace('', ' ').Replace('', ' ').Trim();
        if (n.StartsWith("@")) n = n.TrimStart('@');
        if (n.Length > MaxNameLen) n = n.Substring(0, MaxNameLen);
        return string.IsNullOrWhiteSpace(n) ? "Anonymous" : n;
    }

    // ─── storage ──────────────────────────────────────────────────────
    private Dictionary<long, List<ThreadMsg>> Load()
    {
        if (_threads is not null) return _threads;
        EnsureGrown(Offset);
        _threads = new Dictionary<long, List<ThreadMsg>>();
        var magic = ReadU32(MagicOffset);
        if ((magic != Magic && magic != Magic2) || ReadU32(MagicOffset + 4) != Version)
            return _threads;
        // THR2 carries a 3rd per-message string (authorPrincipal); legacy THRD
        // blobs have only 2. Read the SAME local magic for arity so a THR2 blob
        // is never read as 2-string (which would desync the binary cursor).
        var hasAuthor = magic == Magic2;
        var r = new MemoryReader(Offset);
        var threadCount = (int)r.ReadU32();
        for (int i = 0; i < threadCount; i++)
        {
            var parent = r.ReadI64();
            var n = (int)r.ReadU32();
            var msgs = new List<ThreadMsg>(n > 0 && n <= MaxMsgsPerThread ? n : 0);
            for (int j = 0; j < n; j++)
            {
                var at = r.ReadI64();
                var name = r.ReadString();
                var text = r.ReadString();
                var author = hasAuthor ? r.ReadString() : "";
                msgs.Add(new ThreadMsg(name, at, text, author));
            }
            if (parent > 0) _threads[parent] = msgs;
        }
        return _threads;
    }

    /// <summary>Returns false (without writing) if the serialized store would
    /// overrun <see cref="RegionEnd"/> — the caller then rejects the change.</summary>
    private bool Persist()
    {
        var threads = _threads!;
        var buf = new MemoryWriter();
        buf.WriteU32((uint)threads.Count);
        foreach (var kv in threads)
        {
            buf.WriteI64(kv.Key);
            buf.WriteU32((uint)kv.Value.Count);
            foreach (var m in kv.Value)
            {
                buf.WriteI64(m.AtMs);
                buf.WriteString(m.Username);
                buf.WriteString(m.Text);
                buf.WriteString(m.AuthorPrincipal);   // THR2: 3rd per-message string
            }
        }
        var bytes = buf.ToArray();
        if (Offset + (ulong)bytes.Length > RegionEnd) return false;   // region-end guard
        WriteBytes(Offset, bytes);
        WriteU32(MagicOffset, Magic2);                // write the v2 (THR2) magic
        WriteU32(MagicOffset + 4, Version);
        return true;
    }

    // ─── helpers (mirror the other stable stores) ─────────────────────
    private static long NowMs() => (long)(Ic0.time() / 1_000_000UL);
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
            var data = Encoding.UTF8.GetBytes(s ?? "");
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
            return Encoding.UTF8.GetString(b);
        }
    }
}
