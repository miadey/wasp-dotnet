using System;
using System.Collections.Generic;
using System.Linq;
using Wasp.IcCdk;

namespace WaspSample.BlazorWasp;

/// <summary>B4 per-server visibility + explicit membership. A brand-new additive
/// stable region at offset 64_000_000 (first free above ModService's 63M ceiling)
/// — touches NO existing region, so live data survives --mode upgrade and a fresh
/// canister reads it as "all servers public, no members". Keyed by serverId; a
/// channel inherits its owning server's flag (resolved via ServerService.ServerOfChannel).
///
/// PRIVACY MODEL: a PRIVATE server's channel content must NEVER render on the
/// anonymous SSR / query path (it would leak + get cached). Reads for private
/// channels go through a SIGNED update endpoint gated by membership — the same
/// posture as DmService. The virtual default server 0 ("Wasp") is ALWAYS public.
///
/// Stable layout (header 8 bytes below data; ServerService/RoleService/ModService idiom):
///   uint32 magic "MEMB"   (at MagicOffset)
///   uint32 version 1      (at MagicOffset+4)
///   uint32 privateCount;  foreach: int32 serverId         (only PRIVATE servers stored; absence = public)
///   uint32 memberCount;   foreach: int32 serverId; uint32 len(principal 1..29); bytes principal (raw blob)
/// </summary>
public sealed unsafe class MembershipService
{
    private const ulong Offset = 64_000_000;
    private const ulong MagicOffset = Offset - 8;   // magic(4)+version(4)
    private const uint Magic = 0x4D454D42;          // "MEMB"
    private const uint Version = 1;
    private const ulong RegionEnd = 79_000_000;     // refuse writes past here

    private const int MaxPrivateServers = 500;
    private const int MaxMembersPerServer = 2000;
    private const int MaxMemberEntries = 20000;     // global ceiling vs region size
    private const int MaxPrincipalLen = 29;

    private HashSet<int>? _private;
    private List<MemberEntry>? _members;

    private sealed class MemberEntry { public int ServerId; public byte[] Principal = Array.Empty<byte>(); }

    // ─── Public API ───────────────────────────────────────────────────
    /// <summary>Server 0 (virtual "Wasp") is always public; otherwise check the set.</summary>
    public bool IsPrivate(int serverId) { if (serverId <= 0) return false; Load(); return _private!.Contains(serverId); }

    public bool SetPrivate(int serverId, bool priv)
    {
        if (serverId <= 0) return false;   // the virtual default server can't be private
        Load();
        bool changed = priv ? (_private!.Count < MaxPrivateServers && _private!.Add(serverId)) : _private!.Remove(serverId);
        return changed && Persist();
    }

    public bool IsMember(int serverId, byte[]? principal)
    {
        if (serverId <= 0 || principal is null || principal.Length == 0
            || (principal.Length == 1 && principal[0] == 0x04)) return false;   // anonymous never a member
        Load();
        foreach (var m in _members!)
            if (m.ServerId == serverId && Equal(m.Principal, principal)) return true;
        return false;
    }

    public bool AddMember(int serverId, byte[] principal)
    {
        if (serverId <= 0 || principal is null || principal.Length == 0 || principal.Length > MaxPrincipalLen) return false;
        Load();
        if (_members!.Any(m => m.ServerId == serverId && Equal(m.Principal, principal))) return true;   // idempotent
        if (_members.Count(m => m.ServerId == serverId) >= MaxMembersPerServer) return false;
        if (_members.Count >= MaxMemberEntries) return false;
        _members.Add(new MemberEntry { ServerId = serverId, Principal = principal });
        return Persist();
    }

    public bool RemoveMember(int serverId, byte[] principal)
    {
        if (serverId <= 0 || principal is null || principal.Length == 0) return false;
        Load();
        var idx = _members!.FindIndex(m => m.ServerId == serverId && Equal(m.Principal, principal));
        if (idx < 0) return true;   // idempotent
        _members.RemoveAt(idx);
        return Persist();
    }

    public IReadOnlyList<byte[]> ListMembers(int serverId)
    {
        Load();
        return _members!.Where(m => m.ServerId == serverId).Select(m => m.Principal).ToList();
    }

    // ─── storage ─────────────────────────────────────────────────────
    private void Load()
    {
        if (_private is not null) return;
        EnsureGrown(Offset);
        _private = new HashSet<int>();
        _members = new List<MemberEntry>();
        if (ReadU32(MagicOffset) != Magic || ReadU32(MagicOffset + 4) != Version)
            return;   // fresh/garbage region → all public, no members (no trap)
        var r = new MemoryReader(Offset);
        var pc = (int)r.ReadU32();
        if (pc < 0 || pc > MaxPrivateServers) { _private = new(); _members = new(); return; }
        for (int i = 0; i < pc; i++) _private.Add(r.ReadI32());
        var mc = (int)r.ReadU32();
        if (mc < 0 || mc > MaxMemberEntries) { _members = new(); return; }
        for (int i = 0; i < mc; i++)
        {
            var sid = r.ReadI32();
            var len = (int)r.ReadU32();
            if (len <= 0 || len > MaxPrincipalLen) { _members = new(); return; }
            var prin = r.ReadBlob(len);
            _members.Add(new MemberEntry { ServerId = sid, Principal = prin });
        }
    }

    private bool Persist()
    {
        var buf = new MemoryWriter();
        buf.WriteU32((uint)_private!.Count);
        foreach (var sid in _private) buf.WriteI32(sid);
        buf.WriteU32((uint)_members!.Count);
        foreach (var m in _members)
        {
            buf.WriteI32(m.ServerId);
            buf.WriteU32((uint)m.Principal.Length);
            buf.WriteBlob(m.Principal);
        }
        var bytes = buf.ToArray();
        if (Offset + (ulong)bytes.Length > RegionEnd) return false;   // never overrun the region
        WriteBytes(Offset, bytes);
        WriteU32(MagicOffset, Magic);
        WriteU32(MagicOffset + 4, Version);
        return true;
    }

    private static bool Equal(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
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
        public void WriteBlob(byte[] b) => _buf.AddRange(b);
    }

    private sealed class MemoryReader
    {
        public ulong Cursor { get; private set; }
        public MemoryReader(ulong offset) { Cursor = offset; }
        public uint ReadU32() { var b = new byte[4]; ReadBytes(Cursor, b); Cursor += 4; return BitConverter.ToUInt32(b); }
        public int ReadI32() { var b = new byte[4]; ReadBytes(Cursor, b); Cursor += 4; return BitConverter.ToInt32(b); }
        public byte[] ReadBlob(int len) { var b = new byte[len]; ReadBytes(Cursor, b); Cursor += (ulong)len; return b; }
    }
}
