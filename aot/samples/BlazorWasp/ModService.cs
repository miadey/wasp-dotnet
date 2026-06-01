using System;
using System.Collections.Generic;
using System.Linq;
using Wasp.IcCdk;

namespace WaspSample.BlazorWasp;

/// <summary>M2-B moderation state — pinned messages + locked channels. A brand-new
/// additive stable region at offset 48_000_000 (first free above RoleService's
/// 32M..47M window), so NO existing region is touched and live data survives
/// --mode upgrade untouched. Mutations are only reachable through the signed,
/// role-gated /api/chat/mod/* endpoints (RoleOf &gt;= Moderator on the channel's
/// server, or super-admin); this store just records the flags.
///
/// Two flat sets:
///   • pinned  — message ids shown with a 📌 badge.
///   • locked  — room ids that reject NEW CONTENT from non-mods: top-level
///     posts (/api/chat/post) and thread replies (/api/thread/reply). Mods/super
///     bypass. Reactions and own-message edits to EXISTING messages are NOT
///     frozen by a lock (they don't add new content) — by design.
///
/// Stable layout (header 8 bytes below data, like ServerService/RoleService):
///   uint32 magic "MODS"   (at MagicOffset)
///   uint32 version 1      (at MagicOffset+4)
///   uint32 pinnedCount;  foreach: int64 msgId
///   uint32 lockedCount;  foreach: int32 roomId
/// </summary>
public sealed unsafe class ModService
{
    private const ulong Offset = 48_000_000;
    private const ulong MagicOffset = Offset - 8;   // magic(4)+version(4)
    private const uint Magic = 0x4D4F4453;          // "MODS"
    private const uint Version = 1;
    private const uint MuteMagic = 0x4D555445;      // "MUTE" sentinel after the locked section
    private const ulong RegionEnd = 63_000_000;     // refuse writes past here

    private const int MaxPinned = 2000;
    private const int MaxLocked = 500;
    private const int MaxMutes = 2000;
    private const int MaxPrincipalLen = 29;         // IC principal is at most 29 bytes raw

    private HashSet<long>? _pinned;
    private HashSet<int>? _locked;
    private List<MuteEntry>? _mutes;

    // Per-server mute: (serverId, principal) → untilMs epoch ms (forever = long.MaxValue).
    private sealed class MuteEntry { public int ServerId; public byte[] Principal = Array.Empty<byte>(); public long UntilMs; }

    // ─── Public API ───────────────────────────────────────────────────
    public bool IsPinned(long msgId) { Load(); return _pinned!.Contains(msgId); }
    public bool IsLocked(int roomId) { Load(); return _locked!.Contains(roomId); }
    public IReadOnlyCollection<long> PinnedIds() { Load(); return _pinned!; }
    public IReadOnlyCollection<int> LockedIds() { Load(); return _locked!; }

    public bool SetPin(long msgId, bool pinned)
    {
        if (msgId <= 0) return false;
        Load();
        bool changed = pinned ? (_pinned!.Count < MaxPinned && _pinned!.Add(msgId)) : _pinned!.Remove(msgId);
        return changed && Persist();
    }

    public bool SetLock(int roomId, bool locked)
    {
        if (roomId <= 0) return false;
        Load();
        bool changed = locked ? (_locked!.Count < MaxLocked && _locked!.Add(roomId)) : _locked!.Remove(roomId);
        return changed && Persist();
    }

    /// <summary>Is (serverId, principal) currently muted at nowMs? Lazily drops
    /// expired mutes (and persists once if any were dropped).</summary>
    public bool IsMuted(int serverId, byte[]? principal, long nowMs)
    {
        if (principal is null || principal.Length == 0) return false;
        Load();
        bool dropped = false, hit = false;
        for (int i = _mutes!.Count - 1; i >= 0; i--)
        {
            var m = _mutes[i];
            if (m.UntilMs <= nowMs) { _mutes.RemoveAt(i); dropped = true; continue; }
            if (m.ServerId == serverId && Equal(m.Principal, principal)) hit = true;
        }
        if (dropped) Persist();
        return hit;
    }

    /// <summary>Set/clear a mute. untilMs &lt;= nowMs removes the entry (= unmute).
    /// forever = long.MaxValue. Returns false if full / invalid / nothing changed.</summary>
    public bool SetMute(int serverId, byte[] principal, long untilMs, long nowMs)
    {
        if (principal is null || principal.Length == 0 || principal.Length > MaxPrincipalLen) return false;
        Load();
        var idx = _mutes!.FindIndex(m => m.ServerId == serverId && Equal(m.Principal, principal));
        if (untilMs <= nowMs)   // expire-now == unmute
        {
            if (idx < 0) return false;
            _mutes.RemoveAt(idx);
            return Persist();
        }
        if (idx >= 0) { _mutes[idx].UntilMs = untilMs; return Persist(); }
        if (_mutes.Count >= MaxMutes) return false;
        _mutes.Add(new MuteEntry { ServerId = serverId, Principal = principal, UntilMs = untilMs });
        return Persist();
    }

    /// <summary>Currently-active mutes on a server (for a management UI).</summary>
    public IReadOnlyList<(byte[] Principal, long UntilMs)> MutedList(int serverId, long nowMs)
    {
        Load();
        var outp = new List<(byte[], long)>();
        foreach (var m in _mutes!)
            if (m.ServerId == serverId && m.UntilMs > nowMs) outp.Add((m.Principal, m.UntilMs));
        return outp;
    }

    private static bool Equal(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    // ─── storage ─────────────────────────────────────────────────────
    private void Load()
    {
        if (_pinned is not null) return;
        EnsureGrown(Offset);
        _pinned = new HashSet<long>();
        _locked = new HashSet<int>();
        _mutes = new List<MuteEntry>();
        if (ReadU32(MagicOffset) != Magic || ReadU32(MagicOffset + 4) != Version)
            return;   // fresh/garbage region → empty sets (no trap)
        var r = new MemoryReader(Offset);
        var pc = (int)r.ReadU32();
        if (pc < 0 || pc > MaxPinned) { _pinned = new(); _locked = new(); return; }
        for (int i = 0; i < pc; i++) _pinned.Add(r.ReadI64());
        var lc = (int)r.ReadU32();
        if (lc < 0 || lc > MaxLocked) { _locked = new(); return; }
        for (int i = 0; i < lc; i++) _locked.Add(r.ReadI32());
        // Mutes — appended after locked, guarded by a MUTE sentinel. A pre-mute
        // pins+locks blob has zeros here → mm != MUTE → no mutes, no desync.
        var mm = r.ReadU32();
        if (mm != MuteMagic) return;
        var mc = (int)r.ReadU32();
        if (mc < 0 || mc > MaxMutes) { _mutes = new(); return; }
        for (int i = 0; i < mc; i++)
        {
            var sid = r.ReadI32();
            var len = (int)r.ReadU32();
            if (len <= 0 || len > MaxPrincipalLen) { _mutes = new(); return; }
            var prin = r.ReadBlob(len);
            var until = r.ReadI64();
            _mutes.Add(new MuteEntry { ServerId = sid, Principal = prin, UntilMs = until });
        }
    }

    private bool Persist()
    {
        var buf = new MemoryWriter();
        buf.WriteU32((uint)_pinned!.Count);
        foreach (var id in _pinned) buf.WriteI64(id);
        buf.WriteU32((uint)_locked!.Count);
        foreach (var id in _locked) buf.WriteI32(id);
        // Mutes section — MUTE sentinel + table (additive after locked).
        buf.WriteU32(MuteMagic);
        buf.WriteU32((uint)_mutes!.Count);
        foreach (var m in _mutes)
        {
            buf.WriteI32(m.ServerId);
            buf.WriteU32((uint)m.Principal.Length);
            buf.WriteBlob(m.Principal);
            buf.WriteI64(m.UntilMs);
        }
        var bytes = buf.ToArray();
        if (Offset + (ulong)bytes.Length > RegionEnd) return false;   // never overrun the region
        WriteBytes(Offset, bytes);
        WriteU32(MagicOffset, Magic);
        WriteU32(MagicOffset + 4, Version);
        return true;
    }

    // ─── stable-memory primitives (ServerService/RoleService idiom) ────
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
        public void WriteBlob(byte[] b) => _buf.AddRange(b);
    }

    private sealed class MemoryReader
    {
        public ulong Cursor { get; private set; }
        public MemoryReader(ulong offset) { Cursor = offset; }
        public uint ReadU32() { var b = new byte[4]; ReadBytes(Cursor, b); Cursor += 4; return BitConverter.ToUInt32(b); }
        public int ReadI32() { var b = new byte[4]; ReadBytes(Cursor, b); Cursor += 4; return BitConverter.ToInt32(b); }
        public long ReadI64() { var b = new byte[8]; ReadBytes(Cursor, b); Cursor += 8; return BitConverter.ToInt64(b); }
        public byte[] ReadBlob(int len) { var b = new byte[len]; ReadBytes(Cursor, b); Cursor += (ulong)len; return b; }
    }
}
