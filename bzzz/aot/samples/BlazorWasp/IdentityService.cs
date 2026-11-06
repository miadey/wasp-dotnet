using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Wasp.IcCdk;

namespace WaspSample.BlazorWasp;

/// <summary>
/// Cosmetic Internet-Identity layer. Maps an II principal (text form,
/// e.g. <c>2vxsx-fae...</c>) to a chosen display name so users don't
/// have to retype their name in chat / CRM / etc on every page.
///
/// Security note: this is NOT authenticated. The principal arrives via
/// the same HTTP form-args channel as everything else and the canister
/// trusts it — anyone who knows the text form of a principal can claim
/// it. Real auth would require switching update calls to agent-js with
/// signed delegations; that's a much bigger lift. The win here is UX,
/// not security: one click in II → name follows you across pages, and
/// you can't accidentally collide with someone else's name (we suffix
/// duplicates with -2, -3, …).
///
/// Stable layout (at <see cref="Offset"/>):
///   uint32 magic ("IDNT")
///   uint32 version (1)
///   uint32 entryCount
///   foreach entry:
///     int32  utf8len(principal); bytes principal
///     int32  utf8len(name);      bytes name
///     int64  boundAtMs
/// </summary>
public sealed unsafe class IdentityService
{
    private const ulong Offset = 5_500_000;
    private const ulong MagicOffset = Offset - 8;   // magic(4) + version(4)
    private const uint Magic = 0x49444E54;          // "IDNT"
    private const uint Version = 1;

    public sealed record Binding(string Principal, string DisplayName, long BoundAtMs);

    private Dictionary<string, Binding>? _byPrincipal;
    private Dictionary<string, string>? _nameToPrincipal;   // lower(name) → principal

    public IReadOnlyList<Binding> All() => Load().Values.ToList();

    public Binding? Lookup(string? principal)
    {
        if (string.IsNullOrEmpty(principal)) return null;
        return Load().TryGetValue(principal, out var b) ? b : null;
    }

    public Binding? LookupByName(string? displayName)
    {
        if (string.IsNullOrEmpty(displayName)) return null;
        var idx = LoadNameIndex();
        if (!idx.TryGetValue(displayName.ToLowerInvariant(), out var p)) return null;
        return Lookup(p);
    }

    /// <summary>Bind a display name to a principal. If the name is
    /// already taken by a different principal, a numeric suffix is
    /// appended (e.g. "User99" → "User99-2"). If the principal already
    /// has a binding and the requested name matches it (case-insens),
    /// returns the existing binding. Otherwise updates the binding to
    /// the new name and removes the old name from the index.</summary>
    public Binding Bind(string principal, string displayName)
    {
        principal = SanitisePrincipal(principal);
        displayName = SanitiseName(displayName);
        var map = Load();
        var idx = LoadNameIndex();

        // Same principal asking for the same name? No-op.
        if (map.TryGetValue(principal, out var existing) &&
            existing.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase))
            return existing;

        // Resolve collision: if another principal owns this name, suffix.
        var finalName = displayName;
        if (idx.TryGetValue(finalName.ToLowerInvariant(), out var owner) && owner != principal)
        {
            for (var n = 2; n < 1000; n++)
            {
                var candidate = displayName + "-" + n;
                if (!idx.ContainsKey(candidate.ToLowerInvariant()))
                {
                    finalName = candidate;
                    break;
                }
            }
        }

        // Drop stale name → principal mapping if this principal is renaming.
        if (existing is not null)
            idx.Remove(existing.DisplayName.ToLowerInvariant());

        var binding = new Binding(principal, finalName, NowMs());
        map[principal] = binding;
        idx[finalName.ToLowerInvariant()] = principal;
        Persist();
        return binding;
    }

    public bool Unbind(string principal)
    {
        var map = Load();
        if (!map.TryGetValue(principal, out var b)) return false;
        map.Remove(principal);
        LoadNameIndex().Remove(b.DisplayName.ToLowerInvariant());
        Persist();
        return true;
    }

    // ─── storage ─────────────────────────────────────────────────────
    private Dictionary<string, Binding> Load()
    {
        if (_byPrincipal is not null) return _byPrincipal;
        EnsureGrown();
        _byPrincipal = new Dictionary<string, Binding>(StringComparer.Ordinal);
        _nameToPrincipal = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ReadMagic() != Magic || ReadVersion() != Version) return _byPrincipal;
        var r = new MemoryReader(Offset);
        var count = (int)r.ReadU32();
        for (int i = 0; i < count; i++)
        {
            var princ = r.ReadString();
            var name = r.ReadString();
            var at = r.ReadI64();
            if (string.IsNullOrEmpty(princ) || string.IsNullOrEmpty(name)) continue;
            _byPrincipal[princ] = new Binding(princ, name, at);
            _nameToPrincipal[name.ToLowerInvariant()] = princ;
        }
        return _byPrincipal;
    }

    private Dictionary<string, string> LoadNameIndex()
    {
        Load();
        return _nameToPrincipal!;
    }

    private void Persist()
    {
        var map = _byPrincipal!;
        var buf = new MemoryWriter();
        buf.WriteU32((uint)map.Count);
        foreach (var b in map.Values)
        {
            buf.WriteString(b.Principal);
            buf.WriteString(b.DisplayName);
            buf.WriteI64(b.BoundAtMs);
        }
        var bytes = buf.ToArray();
        WriteBytes(Offset, bytes);
        WriteMagicAndVersion();
    }

    // ─── helpers ─────────────────────────────────────────────────────
    private static long NowMs() => (long)(Ic0.time() / 1_000_000UL);

    private static string SanitisePrincipal(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        raw = raw.Trim();
        // II principals are base32-with-dashes, up to ~63 chars. Be
        // permissive but bounded so we don't store unbounded garbage.
        if (raw.Length > 96) raw = raw.Substring(0, 96);
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-') sb.Append(c);
        return sb.ToString();
    }

    private static string SanitiseName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "User";
        raw = raw.Trim();
        if (raw.Length > 24) raw = raw.Substring(0, 24);
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw) if (c != '|' && c >= 0x20 && c != '') sb.Append(c);
        var cleaned = sb.ToString();
        return string.IsNullOrEmpty(cleaned) ? "User" : cleaned;
    }

    // ─── stable-memory primitives ────────────────────────────────────
    private uint ReadMagic() { var b = new byte[4]; ReadBytes(MagicOffset, b); return BitConverter.ToUInt32(b); }
    private uint ReadVersion() { var b = new byte[4]; ReadBytes(MagicOffset + 4, b); return BitConverter.ToUInt32(b); }
    private void WriteMagicAndVersion()
    {
        WriteBytes(MagicOffset, BitConverter.GetBytes(Magic));
        WriteBytes(MagicOffset + 4, BitConverter.GetBytes(Version));
    }
    private static void EnsureGrown()
    {
        ulong needed = Offset + 65536UL;   // one page past Offset to start
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
