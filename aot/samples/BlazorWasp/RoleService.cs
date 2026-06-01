using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Wasp.IcCdk;

namespace WaspSample.BlazorWasp;

/// <summary>Per-server role grants — the M2 authorization store. Maps a
/// <c>(serverId, principal)</c> pair to a <see cref="ServerRole"/>. Layered
/// ADDITIVELY in a brand-new stable region at offset 32_000_000 (the first
/// free address above ThreadService's 31M ceiling), so NO existing region is
/// touched and live mainnet data survives --mode upgrade untouched.
///
/// Trust model: this store only records grants. WHO may mutate it is decided
/// by the caller's signed <c>msg_caller</c> at the endpoint boundary (see
/// <see cref="AdminService.CurrentCaller"/>), never by a client-supplied
/// string. The three tiers:
///   • Super-admin  — global, lives in <see cref="AdminService"/>; may create
///                    servers and acts as Admin on every server.
///   • Owner (3)    — the principal that created the server; may grant/revoke
///                    Admins and Moderators; cannot be revoked.
///   • Admin (2)    — may create channels in that server + grant/revoke Mods.
///   • Moderator(1) — RESERVED. The tier is stored/grantable now, but message
///                    moderation is NOT yet enforced: delete/edit still run on
///                    the anonymous bridge keyed by a (spoofable) display name.
///                    Real enforcement (signed delete/edit gated on this role)
///                    lands in M2 phase B — until then a Moderator grant confers
///                    no actual power.
///   • None (0)     — a normal user (post/react/reply/edit-own/delete-own).
///
/// Stable layout (IdentityService/ServerService idiom — magic+version eight
/// bytes BELOW the data offset):
///   uint32 magic "ROLE"            (at MagicOffset)
///   uint32 version 1               (at MagicOffset+4)
///   uint32 grantCount              (at Offset)
///   foreach grant:
///     int32  serverId
///     uint32 len(principal 1..29);  bytes principal (raw blob form)
///     uint32 role  (ServerRole)
/// </summary>
public sealed unsafe class RoleService
{
    private const ulong Offset = 32_000_000;
    private const ulong MagicOffset = Offset - 8;   // magic(4)+version(4)
    private const uint Magic = 0x524F4C45;          // "ROLE"
    private const uint Version = 1;
    private const ulong RegionEnd = 47_000_000;     // refuse writes past here (next region is free far above)

    private const int MaxGrants = 4000;
    private const int MaxPrincipalLen = 29;         // IC principal is at most 29 bytes raw

    private sealed class RoleGrant
    {
        public int ServerId;
        public byte[] Principal = Array.Empty<byte>();
        public byte Role;
    }

    private List<RoleGrant>? _grants;

    // ─── Public API ───────────────────────────────────────────────────

    /// <summary>The role <paramref name="principal"/> holds on
    /// <paramref name="serverId"/>, or <see cref="ServerRole.None"/>. Owner &gt;
    /// Admin &gt; Moderator &gt; None.</summary>
    public ServerRole RoleOf(int serverId, byte[]? principal)
    {
        if (principal is null || principal.Length == 0) return ServerRole.None;
        foreach (var g in Load())
            if (g.ServerId == serverId && Equal(g.Principal, principal))
                return (ServerRole)g.Role;
        return ServerRole.None;
    }

    /// <summary>Upsert a grant. role==None removes the grant (same as
    /// <see cref="Revoke"/>). Returns false only if the store is full or the
    /// principal is invalid.</summary>
    public bool Grant(int serverId, byte[] principal, ServerRole role)
    {
        if (principal is null || principal.Length == 0 || principal.Length > MaxPrincipalLen)
            return false;
        if (role == ServerRole.None) return Revoke(serverId, principal);
        var grants = Load();
        var existing = grants.FirstOrDefault(g => g.ServerId == serverId && Equal(g.Principal, principal));
        if (existing is not null) { existing.Role = (byte)role; }
        else
        {
            if (grants.Count >= MaxGrants) return false;
            grants.Add(new RoleGrant { ServerId = serverId, Principal = principal, Role = (byte)role });
        }
        return Persist(grants);
    }

    /// <summary>Remove any grant for (serverId, principal). Returns true if one
    /// was removed.</summary>
    public bool Revoke(int serverId, byte[] principal)
    {
        if (principal is null || principal.Length == 0) return false;
        var grants = Load();
        var idx = grants.FindIndex(g => g.ServerId == serverId && Equal(g.Principal, principal));
        if (idx < 0) return false;
        grants.RemoveAt(idx);
        return Persist(grants);
    }

    /// <summary>Record the server's owner on creation. No-op if the server
    /// already has an Owner (the first creator wins; ownership is sticky).</summary>
    public void SetOwner(int serverId, byte[] principal)
    {
        if (principal is null || principal.Length == 0 || principal.Length > MaxPrincipalLen) return;
        var grants = Load();
        if (grants.Any(g => g.ServerId == serverId && g.Role == (byte)ServerRole.Owner)) return;
        Grant(serverId, principal, ServerRole.Owner);
    }

    /// <summary>All grants on a server (for the roles-management UI). Owner
    /// first, then Admins, then Moderators.</summary>
    public IReadOnlyList<(byte[] Principal, ServerRole Role)> ListGrants(int serverId)
        => Load()
            .Where(g => g.ServerId == serverId)
            .OrderByDescending(g => g.Role)
            .Select(g => (g.Principal, (ServerRole)g.Role))
            .ToList();

    // ─── storage ─────────────────────────────────────────────────────
    private List<RoleGrant> Load()
    {
        if (_grants is not null) return _grants;
        EnsureGrown(Offset);
        _grants = new List<RoleGrant>();
        if (ReadU32(MagicOffset) != Magic || ReadU32(MagicOffset + 4) != Version)
            return _grants;
        var r = new MemoryReader(Offset);
        var count = (int)r.ReadU32();
        if (count < 0 || count > MaxGrants) { _grants = new(); return _grants; }
        for (int i = 0; i < count; i++)
        {
            var serverId = r.ReadI32();
            var len = (int)r.ReadU32();
            if (len <= 0 || len > MaxPrincipalLen) { _grants = new(); return _grants; }
            var principal = r.ReadBlob(len);
            var role = (byte)r.ReadU32();
            _grants.Add(new RoleGrant { ServerId = serverId, Principal = principal, Role = role });
        }
        return _grants;
    }

    private bool Persist(List<RoleGrant> grants)
    {
        _grants = grants;
        var buf = new MemoryWriter();
        buf.WriteU32((uint)grants.Count);
        foreach (var g in grants)
        {
            buf.WriteI32(g.ServerId);
            buf.WriteU32((uint)g.Principal.Length);
            buf.WriteBlob(g.Principal);
            buf.WriteU32(g.Role);
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

    // ─── stable-memory primitives (IdentityService/ServerService idiom) ─
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

/// <summary>Per-server authorization tier. Numeric order is meaningful:
/// higher = more powerful, so gates can use <c>role &gt;= ServerRole.Admin</c>.</summary>
public enum ServerRole : byte
{
    None = 0,
    Moderator = 1,
    Admin = 2,
    Owner = 3,
}
