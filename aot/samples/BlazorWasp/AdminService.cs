using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Wasp.IcCdk;

namespace WaspSample.BlazorWasp;

/// <summary>
/// Persistent allowlist of Internet Identity principals that may edit
/// stable-memory contents via the /stable explorer. Stored at a fixed
/// stable-memory offset (well clear of CounterService at 0, ChatService
/// at 4096, and ImageStore at 1_000_000).
///
/// Layout at <see cref="BaseOffset"/>:
///   uint32 magic "ADMN"
///   uint32 version (1)
///   uint32 count
///   foreach principal:
///     uint32 len  (1..29)
///     bytes  principal (raw blob form, not textual)
///
/// Bootstrap: the canister controller (the dfx identity that did the
/// install) calls /api/admin/add_admin via dfx to seed the first
/// principal. After that, admins can manage the list from /stable.
/// </summary>
public sealed unsafe class AdminService
{
    private const ulong BaseOffset = 900_000;
    private const uint Magic = 0x4E4D4441;   // "ADMN" little-endian
    private const uint Version = 1;
    private const int MaxPrincipals = 16;    // bounded — 16 admins is plenty
    private const int MaxPrincipalLen = 29;  // IC principal is at most 29 bytes raw

    private List<byte[]>? _cache;

    public IReadOnlyList<byte[]> List() => Load();

    public bool IsAdmin(byte[]? principal)
    {
        if (principal is null || principal.Length == 0) return false;
        foreach (var p in Load())
            if (Equal(p, principal)) return true;
        return false;
    }

    /// <summary>Returns true if added, false if it was already present
    /// or the list is full.</summary>
    public bool Add(byte[] principal)
    {
        if (principal is null || principal.Length == 0 || principal.Length > MaxPrincipalLen)
            return false;
        var list = Load();
        if (list.Any(p => Equal(p, principal))) return false;
        if (list.Count >= MaxPrincipals) return false;
        list.Add(principal);
        Persist(list);
        return true;
    }

    public bool Remove(byte[] principal)
    {
        if (principal is null || principal.Length == 0) return false;
        var list = Load();
        var idx = list.FindIndex(p => Equal(p, principal));
        if (idx < 0) return false;
        list.RemoveAt(idx);
        Persist(list);
        return true;
    }

    // ─── storage ─────────────────────────────────────────────────────
    private List<byte[]> Load()
    {
        if (_cache is not null) return _cache;
        EnsurePages(BaseOffset + 1024);
        var hdr = new byte[12];
        ReadBytes(BaseOffset, hdr);
        var magic = BitConverter.ToUInt32(hdr, 0);
        var ver = BitConverter.ToUInt32(hdr, 4);
        var count = BitConverter.ToInt32(hdr, 8);
        if (magic != Magic || ver != Version || count < 0 || count > MaxPrincipals)
        {
            _cache = new List<byte[]>();
            return _cache;
        }
        var list = new List<byte[]>(count);
        ulong cursor = BaseOffset + 12;
        for (int i = 0; i < count; i++)
        {
            var lenBuf = new byte[4];
            ReadBytes(cursor, lenBuf);
            var len = BitConverter.ToInt32(lenBuf, 0);
            cursor += 4;
            if (len <= 0 || len > MaxPrincipalLen) { _cache = new List<byte[]>(); return _cache; }
            var p = new byte[len];
            ReadBytes(cursor, p);
            cursor += (ulong)len;
            list.Add(p);
        }
        _cache = list;
        return _cache;
    }

    private void Persist(List<byte[]> list)
    {
        _cache = list;
        var totalLen = 12 + list.Sum(p => 4 + p.Length);
        var buf = new byte[totalLen];
        BitConverter.GetBytes(Magic).CopyTo(buf, 0);
        BitConverter.GetBytes(Version).CopyTo(buf, 4);
        BitConverter.GetBytes(list.Count).CopyTo(buf, 8);
        int o = 12;
        foreach (var p in list)
        {
            BitConverter.GetBytes(p.Length).CopyTo(buf, o); o += 4;
            p.CopyTo(buf, o); o += p.Length;
        }
        WriteBytes(BaseOffset, buf);
    }

    private static bool Equal(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static void EnsurePages(ulong needed)
    {
        ulong haveBytes = Ic0.stable64_size() * 65536UL;
        if (needed > haveBytes)
            Ic0.stable64_grow(((needed - haveBytes) + 65535UL) / 65536UL);
    }

    private static void ReadBytes(ulong offset, byte[] buf)
    { fixed (byte* p = buf) Ic0.stable64_read((ulong)(nint)p, offset, (ulong)buf.Length); }

    private static void WriteBytes(ulong offset, byte[] buf)
    {
        EnsurePages(offset + (ulong)buf.Length);
        fixed (byte* p = buf) Ic0.stable64_write(offset, (ulong)(nint)p, (ulong)buf.Length);
    }

    // ─── Helpers for textual principal ↔ raw blob ────────────────────

    /// <summary>This canister's own principal, in textual form.</summary>
    public static string CanisterIdText()
    {
        uint len = Ic0.canister_self_size();
        var buf = new byte[len];
        if (len > 0) fixed (byte* p = buf) Ic0.canister_self_copy((nint)p, 0, len);
        return ToText(buf);
    }

    /// <summary>True if <paramref name="principal"/> is NOT a signed-in identity:
    /// either no principal at all, or the IC anonymous principal — whose raw blob
    /// is the single byte 0x04 (textual "2vxsx-fae"), NOT length 0. A plain
    /// <c>caller.Length == 0</c> guard therefore never catches anonymous; gates
    /// that mean "must be signed in" must use this.</summary>
    public static bool IsAnonymous(byte[]? principal)
        => principal is null || principal.Length == 0 || (principal.Length == 1 && principal[0] == 0x04);

    /// <summary>Read the current call's caller principal as raw bytes.</summary>
    public static byte[] CurrentCaller()
    {
        uint len = Ic0.msg_caller_size();
        var buf = new byte[len];
        if (len > 0) fixed (byte* p = buf) Ic0.msg_caller_copy((nint)p, 0, len);
        return buf;
    }

    /// <summary>Whether the current caller is one of the canister's
    /// controllers (the dfx identity that installed the canister, plus
    /// anything explicitly added with <c>dfx canister update-settings</c>).
    /// </summary>
    public static bool IsCurrentCallerController()
    {
        var p = CurrentCaller();
        if (p.Length == 0) return false;
        fixed (byte* ptr = p) return Ic0.is_controller((nint)ptr, (uint)p.Length) != 0;
    }

    /// <summary>Render a principal as the standard textual form
    /// (group-of-5, CRC32 prefix, dashes). Used to display admin list
    /// + caller in the UI.</summary>
    public static string ToText(byte[] principal)
    {
        if (principal is null || principal.Length == 0) return "2vxsx-fae"; // anonymous
        // Prepend big-endian CRC32 of the raw bytes, then base32-encode
        // the whole thing and group into 5-char dash-separated chunks.
        var crc = Crc32(principal);
        var withCrc = new byte[4 + principal.Length];
        withCrc[0] = (byte)(crc >> 24);
        withCrc[1] = (byte)(crc >> 16);
        withCrc[2] = (byte)(crc >> 8);
        withCrc[3] = (byte)crc;
        Array.Copy(principal, 0, withCrc, 4, principal.Length);
        var b32 = Base32Encode(withCrc);
        var sb = new StringBuilder(b32.Length + b32.Length / 5);
        for (int i = 0; i < b32.Length; i++)
        {
            if (i > 0 && i % 5 == 0) sb.Append('-');
            sb.Append(b32[i]);
        }
        return sb.ToString();
    }

    /// <summary>Parse a textual principal back into its raw bytes form.
    /// Throws on malformed input.</summary>
    public static byte[] FromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("empty principal");
        var stripped = text.Replace("-", "").ToLowerInvariant();
        var decoded = Base32Decode(stripped);
        if (decoded.Length < 4) throw new ArgumentException("principal too short");
        var body = new byte[decoded.Length - 4];
        Array.Copy(decoded, 4, body, 0, body.Length);
        // Verify CRC32 matches.
        var expected = (uint)(decoded[0] << 24 | decoded[1] << 16 | decoded[2] << 8 | decoded[3]);
        if (Crc32(body) != expected) throw new ArgumentException("principal CRC mismatch");
        return body;
    }

    // ─── base32 (RFC 4648, no padding, lowercase) ────────────────────
    private const string B32 = "abcdefghijklmnopqrstuvwxyz234567";

    private static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(B32[(buffer >> bits) & 0x1F]);
            }
        }
        if (bits > 0) sb.Append(B32[(buffer << (5 - bits)) & 0x1F]);
        return sb.ToString();
    }

    private static byte[] Base32Decode(string s)
    {
        var bytes = new List<byte>(s.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in s)
        {
            var v = B32.IndexOf(c);
            if (v < 0) throw new ArgumentException("bad base32 char: " + c);
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)((buffer >> bits) & 0xFF));
            }
        }
        return bytes.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
}
