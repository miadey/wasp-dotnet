using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Wasp.IcCdk;

namespace WaspSample.BlazorWasp;

/// <summary>
/// HONESTLY-private 1:1 direct messages, stored ADDITIVELY at offset
/// 6_500_000. Threads are keyed by the canonical (ordinal-sorted) pair of
/// the two participants' principal TEXTS, so {A,B} and {B,A} map to one
/// thread. EVERY entry point takes the AUTHENTICATED caller raw bytes
/// (AdminService.CurrentCaller() = ic0.msg_caller from a SIGNED
/// @dfinity/agent update call) and enforces participants-only:
///   - anonymous caller (empty bytes / 2vxsx-fae) is rejected before any
///     storage access, so an unsigned fetch() or query path can never read
///     or write a thread;
///   - the canonical key makes it impossible to name a thread you are not
///     a participant of.
/// This is real privacy, not privacy-by-obscurity. The Program.cs DM
/// endpoints are wired UPDATE-ONLY (MapPost, no RegisterQueryHandler /
/// no RegisterPassThroughPath) — same posture as /api/admin/stable_edit.
///
/// Does NOT touch ChatService's region. ChatService Offset=4096 /
/// Magic 0x43484154 / Version 3 stay byte-identical.
///
/// Stable layout at Offset (IdentityService header idiom — magic+version
/// eight bytes below the data offset):
///   uint32 magic "DMSG"            (at MagicOffset)
///   uint32 version 1               (at MagicOffset+4)
///   uint32 threadCount             (at Offset)
///   foreach thread:
///     int32  utf8len(participantA); bytes A   (principal text, lo)
///     int32  utf8len(participantB); bytes B   (principal text, hi)
///     uint32 messageCount  (FIFO-capped at MaxMsgsPerThread)
///     foreach message:
///       int64  atMs
///       int32  utf8len(sender); bytes sender  (principal text; == A or B)
///       int32  utf8len(text);   bytes text
/// </summary>
public sealed unsafe class DmService
{
    private const ulong Offset = 6_500_000;
    private const ulong MagicOffset = Offset - 8;   // magic(4)+version(4)
    private const uint Magic = 0x444D5347;          // "DMSG"
    private const uint Version = 1;
    // DMs own [6.5M, RegionEnd). Persist refuses to write past this so DM
    // growth can never overrun the Threads store (relocated to 16M). ~9.4MB.
    private const ulong RegionEnd = 15_900_000;

    private const int MaxMsgsPerThread = 200;       // FIFO cap per conversation
    private const int MaxTextLen = 1000;
    private const int MaxPrincipalTextLen = 96;
    private const char KeySep = '';           // unit separator; not in a sanitised principal

    public sealed record DmMessage(string SenderPrincipal, long AtMs, string Text);
    public sealed record ThreadSummary(string Peer, string LastText, long LastAtMs, int MsgCount);

    private Dictionary<string, List<DmMessage>>? _threads;

    // ─── Public API (authenticated caller bytes only) ─────────────────

    public bool SendDm(byte[]? caller, string? peerPrincipalText, string? text)
    {
        var me = RequireAuthed(caller);
        if (me is null) return false;
        var peer = ParsePrincipalText(peerPrincipalText);
        if (peer is null) return false;
        if (string.Equals(me, peer, StringComparison.Ordinal)) return false;   // no self-DM
        text = SanitiseText(text);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var key = CanonicalKey(me, peer);
        var threads = Load();
        if (!threads.TryGetValue(key, out var msgs)) { msgs = new(); threads[key] = msgs; }
        msgs.Add(new DmMessage(me, NowMs(), text));
        if (msgs.Count > MaxMsgsPerThread)
            threads[key] = msgs.GetRange(msgs.Count - MaxMsgsPerThread, MaxMsgsPerThread);
        // Region-end guard: reject (and drop the in-memory mutation) rather
        // than let the DM blob overrun into the neighbouring Threads region.
        if (!Persist()) { _threads = null; return false; }
        return true;
    }

    /// <summary>Thread summaries the authenticated caller participates in,
    /// newest activity first. Empty for anonymous.</summary>
    public IReadOnlyList<ThreadSummary> ThreadsFor(byte[]? caller)
    {
        var me = RequireAuthed(caller);
        if (me is null) return Array.Empty<ThreadSummary>();
        var result = new List<ThreadSummary>();
        foreach (var kv in Load())
        {
            var (lo, hi) = SplitKey(kv.Key);
            string peer;
            if (string.Equals(lo, me, StringComparison.Ordinal)) peer = hi;
            else if (string.Equals(hi, me, StringComparison.Ordinal)) peer = lo;
            else continue;
            if (kv.Value.Count == 0) continue;
            var last = kv.Value[^1];
            result.Add(new ThreadSummary(peer, last.Text, last.AtMs, kv.Value.Count));
        }
        result.Sort((a, b) => b.LastAtMs.CompareTo(a.LastAtMs));
        return result;
    }

    /// <summary>Full message list of the {caller,peer} thread. Empty for
    /// anonymous, or if caller is not a participant (the canonical key
    /// already guarantees this).</summary>
    public IReadOnlyList<DmMessage> ReadThread(byte[]? caller, string? peerPrincipalText)
    {
        var me = RequireAuthed(caller);
        if (me is null) return Array.Empty<DmMessage>();
        var peer = ParsePrincipalText(peerPrincipalText);
        if (peer is null) return Array.Empty<DmMessage>();
        var key = CanonicalKey(me, peer);
        var (lo, hi) = SplitKey(key);
        if (!string.Equals(lo, me, StringComparison.Ordinal) && !string.Equals(hi, me, StringComparison.Ordinal))
            return Array.Empty<DmMessage>();
        return Load().TryGetValue(key, out var msgs) ? msgs.ToArray() : Array.Empty<DmMessage>();
    }

    // ─── auth + principal validation ──────────────────────────────────
    private static string? RequireAuthed(byte[]? caller)
    {
        if (caller is null || caller.Length == 0) return null;       // anonymous: empty bytes
        if (caller.Length == 1 && caller[0] == 0x04) return null;    // 2vxsx-fae raw blob
        var text = AdminService.ToText(caller);
        if (string.IsNullOrEmpty(text) || text == "2vxsx-fae") return null;
        if (text.Length > MaxPrincipalTextLen) return null;
        return text;
    }

    // Round-trip the peer text through AdminService (CRC-checked) so only a
    // real principal can be a peer, and the stored text is canonical.
    private static string? ParsePrincipalText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (raw.Length > MaxPrincipalTextLen) return null;
        try
        {
            var blob = AdminService.FromText(raw);
            if (blob.Length == 0) return null;
            if (blob.Length == 1 && blob[0] == 0x04) return null;     // anonymous
            return AdminService.ToText(blob);
        }
        catch { return null; }
    }

    private static string CanonicalKey(string a, string b)
        => string.CompareOrdinal(a, b) <= 0 ? a + KeySep + b : b + KeySep + a;
    private static (string lo, string hi) SplitKey(string key)
    { var i = key.IndexOf(KeySep); return i < 0 ? (key, "") : (key.Substring(0, i), key.Substring(i + 1)); }

    private static string SanitiseText(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        if (raw.IndexOf('') >= 0 || raw.IndexOf('') >= 0)
            raw = raw.Replace('', ' ').Replace('', ' ');
        raw = raw.Trim();
        if (raw.Length > MaxTextLen) raw = raw.Substring(0, MaxTextLen);
        return raw;
    }

    // ─── storage ──────────────────────────────────────────────────────
    private Dictionary<string, List<DmMessage>> Load()
    {
        if (_threads is not null) return _threads;
        EnsureGrown(Offset);
        _threads = new Dictionary<string, List<DmMessage>>(StringComparer.Ordinal);
        if (ReadU32(MagicOffset) != Magic || ReadU32(MagicOffset + 4) != Version)
            return _threads;
        var r = new MemoryReader(Offset);
        var threadCount = (int)r.ReadU32();
        for (int i = 0; i < threadCount; i++)
        {
            var a = r.ReadString();
            var b = r.ReadString();
            var n = (int)r.ReadU32();
            var msgs = new List<DmMessage>(n > 0 && n <= MaxMsgsPerThread ? n : 0);
            for (int j = 0; j < n; j++)
            {
                var at = r.ReadI64();
                var sender = r.ReadString();
                var text = r.ReadString();
                msgs.Add(new DmMessage(sender, at, text));
            }
            if (!string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b))
                _threads[CanonicalKey(a, b)] = msgs;   // re-canonicalise defensively
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
            var (a, b) = SplitKey(kv.Key);
            buf.WriteString(a);
            buf.WriteString(b);
            buf.WriteU32((uint)kv.Value.Count);
            foreach (var m in kv.Value)
            {
                buf.WriteI64(m.AtMs);
                buf.WriteString(m.SenderPrincipal);
                buf.WriteString(m.Text);
            }
        }
        var bytes = buf.ToArray();
        if (Offset + (ulong)bytes.Length > RegionEnd) return false;   // region-end guard
        WriteBytes(Offset, bytes);
        WriteU32(MagicOffset, Magic);
        WriteU32(MagicOffset + 4, Version);
        return true;
    }

    // ─── helpers ──────────────────────────────────────────────────────
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

