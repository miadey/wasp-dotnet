using System;
using System.Collections.Generic;
using System.Text;
using Wasp.IcCdk;

namespace WaspSample.BlazorWasp;

/// <summary>
/// Phase-1 crypto foundation: a PUBLIC key directory for end-to-end
/// encrypted DMs (X3DH-style). Each principal publishes one or more
/// DEVICES; each device carries an identity key (IK), a signed pre-key
/// (SPK) + its signature, and a pool of one-time pre-keys (OTPKs). A
/// peer who wants to start a session FETCHES a bundle: the device's IK,
/// SPK (+ sig), and AT MOST ONE OTPK which is atomically POPPED so no
/// two senders ever reuse the same one-time key.
///
/// This store holds PUBLIC bytes ONLY — never a private key. The canister
/// does NOT verify spkSig (no Ed25519 on the NativeAOT->wasm target); it
/// stores the signature verbatim and the RECEIVING CLIENT verifies it
/// against the device IK. PublishDevice/ReplenishOtpk trust the
/// AUTHENTICATED caller PRINCIPAL (AdminService.ToText(caller)) as the
/// owner and never a body-supplied principal — the same posture as
/// DmService. FetchBundle assumes the endpoint already enforced auth.
///
/// New ADDITIVE region; never touches any existing region. VoteService
/// (VOTE) ends at 130M; this region sits at 140M with a 10M gap below
/// and RegionEnd 160M above.
///
/// Stable layout (lengths u32 LE; lp-bytes = u32 length-prefix + raw bytes
/// so 0x00 key bytes survive; principalText/deviceId via the IdentityService
/// string idiom — int32 utf8 length + bytes):
///   at MagicOffset:  uint32 magic "KEYS"   uint32 version 1
///   at Offset:       uint32 principalCount
///   foreach principal:
///     str  principalText
///     uint32 deviceCount  (cap MaxDevicesPerPrincipal)
///     foreach device:
///       str  deviceId  (max 64 chars)
///       u8   ikAlg;   lp ikPub
///       u8   spkAlg;  lp spkPub;  lp spkSig
///       i64  spkRegisteredAtMs
///       i64  registeredAtMs
///       uint32 otpkCount  (cap MaxOtpkPerDevice)
///       foreach otpk:  u8 otpkAlg;  lp otpkPub
/// </summary>
public sealed unsafe class KeyDirectoryService
{
    private const ulong Offset = 140_000_000;
    private const ulong MagicOffset = Offset - 8;     // magic(4) + version(4) = 139_999_992
    private const ulong RegionEnd = 160_000_000;      // refuse writes past RegionEnd-8
    private const uint Magic = 0x4B455953;            // "KEYS"
    private const uint Version = 1;

    private const int MaxPrincipals = 50_000;
    private const int MaxDevicesPerPrincipal = 8;
    private const int MaxOtpkPerDevice = 100;
    private const int MaxDeviceIdLen = 64;
    private const int MaxPrincipalTextLen = 96;
    private const int MaxPubLen = 256;               // bound on any single public-key blob
    private const byte AlgX25519 = 1;                // alg==1 requires a 32-byte pub
    private const int X25519PubLen = 32;

    // ─── in-memory model ──────────────────────────────────────────────
    private sealed class Otpk { public byte Alg; public byte[] Pub = Array.Empty<byte>(); }
    private sealed class Device
    {
        public string DeviceId = "";
        public byte IkAlg;
        public byte[] IkPub = Array.Empty<byte>();
        public byte SpkAlg;
        public byte[] SpkPub = Array.Empty<byte>();
        public byte[] SpkSig = Array.Empty<byte>();
        public long SpkRegisteredAtMs;
        public long RegisteredAtMs;
        public List<Otpk> Otpks = new();
    }

    // principalText -> ordered device list
    private Dictionary<string, List<Device>>? _byPrincipal;
    // Highest stable offset the serialized store has ever reached, so a
    // later Persist() that shrinks the blob (popped OTPKs / removed devices)
    // can zero the orphaned tail — DmService idiom.
    private ulong _highWaterEnd;

    // ─── small public classes ─────────────────────────────────────────

    /// <summary>One peer device's public X3DH bundle. <see cref="otpkPub"/>
    /// is null when no one-time pre-keys remain (session degrades to SPK-only).</summary>
    public sealed class KeyBundle
    {
        public string peerPrincipalText { get; set; } = "";
        public string deviceId { get; set; } = "";
        public byte ikAlg { get; set; }
        public byte[] ikPub { get; set; } = Array.Empty<byte>();
        public byte spkAlg { get; set; }
        public byte[] spkPub { get; set; } = Array.Empty<byte>();
        public byte[] spkSig { get; set; } = Array.Empty<byte>();
        public long spkAtMs { get; set; }
        public byte otpkAlg { get; set; }
        public byte[]? otpkPub { get; set; }     // null when none left
        public uint otpkId { get; set; }
    }

    /// <summary>Read-only public IK info for a principal's device, plus how
    /// many one-time pre-keys remain (so a client can prompt replenishment).</summary>
    public sealed class DevicePub
    {
        public string principalText { get; set; } = "";
        public string deviceId { get; set; } = "";
        public byte ikAlg { get; set; }
        public byte[] ikPub { get; set; } = Array.Empty<byte>();
        public byte spkAlg { get; set; }
        public byte[] spkPub { get; set; } = Array.Empty<byte>();
        public byte[] spkSig { get; set; } = Array.Empty<byte>();
        public long spkAtMs { get; set; }
        public int otpkCount { get; set; }
    }

    // ─── Public API (authenticated caller bytes only) ─────────────────

    /// <summary>Publish or update the calling principal's device. The owner is
    /// derived from the AUTHENTICATED caller (AdminService.ToText) — never a
    /// body-supplied principal. Upserts by deviceId (rotating the SPK on an
    /// existing device, preserving its OTPK pool) and enforces
    /// MaxDevicesPerPrincipal. Returns false on bad auth/validation or if the
    /// write would overrun the region.</summary>
    public bool PublishDevice(byte[] caller, string deviceId, byte ikAlg, byte[] ikPub,
                              byte spkAlg, byte[] spkPub, byte[] spkSig, long spkAtMs)
    {
        var owner = RequireAuthed(caller);
        if (owner is null) return false;

        deviceId = SanitiseDeviceId(deviceId);
        if (string.IsNullOrEmpty(deviceId)) return false;
        if (!ValidPub(ikAlg, ikPub)) return false;
        if (!ValidPub(spkAlg, spkPub)) return false;
        if (spkSig is null || spkSig.Length == 0 || spkSig.Length > MaxPubLen) return false;

        var map = Load();
        if (!map.TryGetValue(owner, out var devices)) { devices = new(); map[owner] = devices; }

        var existing = FindDevice(devices, deviceId);
        if (existing is null)
        {
            if (devices.Count >= MaxDevicesPerPrincipal) return false;   // enforce cap
            existing = new Device { DeviceId = deviceId, RegisteredAtMs = NowMs() };
            devices.Add(existing);
        }
        existing.IkAlg = ikAlg;
        existing.IkPub = (byte[])ikPub.Clone();
        existing.SpkAlg = spkAlg;                                        // rotate SPK
        existing.SpkPub = (byte[])spkPub.Clone();
        existing.SpkSig = (byte[])spkSig.Clone();
        existing.SpkRegisteredAtMs = spkAtMs;

        if (!Persist()) { _byPrincipal = null; return false; }          // region-end guard
        return true;
    }

    /// <summary>Append one-time pre-keys to the caller's OWN device, clamped to
    /// MaxOtpkPerDevice. Returns the number actually accepted (0 on bad auth,
    /// unknown device, invalid keys, full pool, or a region-overrun rollback).</summary>
    public int ReplenishOtpk(byte[] caller, string deviceId, List<byte[]> otpkPubs)
    {
        var owner = RequireAuthed(caller);
        if (owner is null) return 0;
        deviceId = SanitiseDeviceId(deviceId);
        if (string.IsNullOrEmpty(deviceId)) return 0;
        if (otpkPubs is null || otpkPubs.Count == 0) return 0;

        var map = Load();
        if (!map.TryGetValue(owner, out var devices)) return 0;
        var dev = FindDevice(devices, deviceId);
        if (dev is null) return 0;

        int accepted = 0;
        foreach (var pub in otpkPubs)
        {
            if (dev.Otpks.Count >= MaxOtpkPerDevice) break;             // clamp to cap
            // OTPKs use the device's IK alg; reject blobs that don't fit it.
            if (!ValidPub(dev.IkAlg, pub)) continue;
            dev.Otpks.Add(new Otpk { Alg = dev.IkAlg, Pub = (byte[])pub.Clone() });
            accepted++;
        }
        if (accepted == 0) return 0;
        if (!Persist()) { _byPrincipal = null; return 0; }             // region-end guard
        return accepted;
    }

    /// <summary>Fetch a peer device's public bundle, atomically POPPING at most
    /// one OTPK (single-threaded canister: Load-mutate-Persist, popped bytes
    /// tail-zeroed). Returns null if the peer is unknown / has no device. The
    /// caller is assumed already authenticated by the endpoint. If no OTPK
    /// remains the bundle is returned with <c>otpkPub == null</c> (X3DH degrades
    /// to SPK-only).</summary>
    public KeyBundle? FetchBundle(byte[] caller, string peerPrincipalText, string? preferDeviceId)
    {
        // The endpoint enforces auth; still refuse an obviously anonymous caller
        // so an unsigned path can never pop a one-time key.
        if (RequireAuthed(caller) is null) return null;

        var peer = ParsePrincipalText(peerPrincipalText);
        if (peer is null) return null;

        var map = Load();
        if (!map.TryGetValue(peer, out var devices) || devices.Count == 0) return null;

        Device? dev = null;
        if (!string.IsNullOrEmpty(preferDeviceId))
        {
            var wanted = SanitiseDeviceId(preferDeviceId);
            if (!string.IsNullOrEmpty(wanted)) dev = FindDevice(devices, wanted);
        }
        dev ??= devices[0];                                             // else first device

        var bundle = new KeyBundle
        {
            peerPrincipalText = peer,
            deviceId = dev.DeviceId,
            ikAlg = dev.IkAlg,
            ikPub = (byte[])dev.IkPub.Clone(),
            spkAlg = dev.SpkAlg,
            spkPub = (byte[])dev.SpkPub.Clone(),
            spkSig = (byte[])dev.SpkSig.Clone(),
            spkAtMs = dev.SpkRegisteredAtMs,
            otpkPub = null,
            otpkId = 0,
        };

        if (dev.Otpks.Count > 0)
        {
            var popped = dev.Otpks[0];                                  // FIFO pop
            dev.Otpks.RemoveAt(0);
            bundle.otpkAlg = popped.Alg;
            bundle.otpkPub = (byte[])popped.Pub.Clone();
            bundle.otpkId = (uint)dev.RegisteredAtMs ^ (uint)NowMs();   // opaque, non-authoritative id
            // Persist the consumption + tail-zero the now-orphaned bytes. On a
            // region-overrun (cannot happen on shrink, but stay safe) roll back
            // the in-memory pop and return the bundle WITHOUT the OTPK rather
            // than hand out a key we failed to durably consume.
            if (!Persist())
            {
                _byPrincipal = null;
                bundle.otpkPub = null;
                bundle.otpkId = 0;
                bundle.otpkAlg = 0;
            }
        }
        return bundle;
    }

    /// <summary>Read-only list of a principal's public device info + remaining
    /// OTPK counts. Never pops. Empty for an unknown principal.</summary>
    public IReadOnlyList<DevicePub> DevicesOf(string principalText)
    {
        var peer = ParsePrincipalText(principalText);
        if (peer is null) return Array.Empty<DevicePub>();
        var map = Load();
        if (!map.TryGetValue(peer, out var devices) || devices.Count == 0)
            return Array.Empty<DevicePub>();
        var result = new List<DevicePub>(devices.Count);
        foreach (var d in devices)
            result.Add(new DevicePub
            {
                principalText = peer,
                deviceId = d.DeviceId,
                ikAlg = d.IkAlg,
                ikPub = (byte[])d.IkPub.Clone(),
                spkAlg = d.SpkAlg,
                spkPub = (byte[])d.SpkPub.Clone(),
                spkSig = (byte[])d.SpkSig.Clone(),
                spkAtMs = d.SpkRegisteredAtMs,
                otpkCount = d.Otpks.Count,
            });
        return result;
    }

    // ─── auth + validation ────────────────────────────────────────────
    private static string? RequireAuthed(byte[]? caller)
    {
        if (caller is null || caller.Length == 0) return null;          // anonymous: empty bytes
        if (caller.Length == 1 && caller[0] == 0x04) return null;       // 2vxsx-fae raw blob
        var text = AdminService.ToText(caller);
        if (string.IsNullOrEmpty(text) || text == "2vxsx-fae") return null;
        if (text.Length > MaxPrincipalTextLen) return null;
        return text;
    }

    // Round-trip the peer text through AdminService (CRC-checked) so only a real
    // principal can be looked up and the key is canonical — DmService idiom.
    private static string? ParsePrincipalText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (raw.Length > MaxPrincipalTextLen) return null;
        try
        {
            var blob = AdminService.FromText(raw);
            if (blob.Length == 0) return null;
            if (blob.Length == 1 && blob[0] == 0x04) return null;       // anonymous
            return AdminService.ToText(blob);
        }
        catch { return null; }
    }

    // alg==1 (X25519) REQUIRES exactly 32 bytes; any alg requires a non-empty,
    // bounded blob. The canister never interprets other algs, only stores them.
    private static bool ValidPub(byte alg, byte[]? pub)
    {
        if (pub is null || pub.Length == 0 || pub.Length > MaxPubLen) return false;
        if (alg == AlgX25519 && pub.Length != X25519PubLen) return false;
        return true;
    }

    private static string SanitiseDeviceId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        raw = raw.Trim();
        if (raw.Length > MaxDeviceIdLen) raw = raw.Substring(0, MaxDeviceIdLen);
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.') sb.Append(c);
        return sb.ToString();
    }

    private static Device? FindDevice(List<Device> devices, string deviceId)
    {
        foreach (var d in devices)
            if (string.Equals(d.DeviceId, deviceId, StringComparison.Ordinal)) return d;
        return null;
    }

    // ─── storage ──────────────────────────────────────────────────────
    private Dictionary<string, List<Device>> Load()
    {
        if (_byPrincipal is not null) return _byPrincipal;
        EnsureGrown(Offset);
        _byPrincipal = new Dictionary<string, List<Device>>(StringComparer.Ordinal);
        // Wrong magic/version (incl. the never-written all-zero first-boot case):
        // start EMPTY in-memory, NEVER wipe, NEVER read another region; seed the
        // high-water mark at Offset so the first Persist() has nothing to scrub.
        if (ReadU32(MagicOffset) != Magic || ReadU32(MagicOffset + 4) != Version)
        { _highWaterEnd = Offset; return _byPrincipal; }

        var r = new MemoryReader(Offset);
        var principalCount = (int)r.ReadU32();
        if (principalCount < 0 || principalCount > MaxPrincipals)
        { _byPrincipal = new(StringComparer.Ordinal); _highWaterEnd = Offset; return _byPrincipal; }

        for (int i = 0; i < principalCount; i++)
        {
            var princ = r.ReadString();
            var deviceCount = (int)r.ReadU32();
            if (deviceCount < 0 || deviceCount > MaxDevicesPerPrincipal) deviceCount = 0;
            var devices = new List<Device>(deviceCount);
            for (int d = 0; d < deviceCount; d++)
            {
                var dev = new Device { DeviceId = r.ReadString() };
                dev.IkAlg = r.ReadU8();
                dev.IkPub = r.ReadBytesLp();
                dev.SpkAlg = r.ReadU8();
                dev.SpkPub = r.ReadBytesLp();
                dev.SpkSig = r.ReadBytesLp();
                dev.SpkRegisteredAtMs = r.ReadI64();
                dev.RegisteredAtMs = r.ReadI64();
                var otpkCount = (int)r.ReadU32();
                if (otpkCount < 0 || otpkCount > MaxOtpkPerDevice) otpkCount = 0;
                dev.Otpks = new List<Otpk>(otpkCount);
                for (int o = 0; o < otpkCount; o++)
                {
                    var alg = r.ReadU8();
                    var pub = r.ReadBytesLp();
                    dev.Otpks.Add(new Otpk { Alg = alg, Pub = pub });
                }
                if (!string.IsNullOrEmpty(dev.DeviceId)) devices.Add(dev);
            }
            if (!string.IsNullOrEmpty(princ) && devices.Count > 0)
                _byPrincipal[princ] = devices;
        }
        _highWaterEnd = r.Cursor;   // furthest valid byte of the persisted blob
        return _byPrincipal;
    }

    /// <summary>Serialize and write the whole store. Returns false (without
    /// writing) if the blob would overrun RegionEnd-8 (top 8 bytes reserved for
    /// the next region's magic header). On a shrink (popped OTPKs / removed
    /// devices) the orphaned tail [newEnd, _highWaterEnd) is zeroed so consumed
    /// one-time keys stop lingering in stable memory — DmService idiom.</summary>
    private bool Persist()
    {
        var map = _byPrincipal!;
        var buf = new MemoryWriter();
        buf.WriteU32((uint)map.Count);
        foreach (var kv in map)
        {
            buf.WriteString(kv.Key);
            var devices = kv.Value;
            int devCount = devices.Count > MaxDevicesPerPrincipal ? MaxDevicesPerPrincipal : devices.Count;
            buf.WriteU32((uint)devCount);
            for (int i = 0; i < devCount; i++)
            {
                var d = devices[i];
                buf.WriteString(d.DeviceId);
                buf.WriteU8(d.IkAlg);
                buf.WriteBytesLp(d.IkPub);
                buf.WriteU8(d.SpkAlg);
                buf.WriteBytesLp(d.SpkPub);
                buf.WriteBytesLp(d.SpkSig);
                buf.WriteI64(d.SpkRegisteredAtMs);
                buf.WriteI64(d.RegisteredAtMs);
                int otpkCount = d.Otpks.Count > MaxOtpkPerDevice ? MaxOtpkPerDevice : d.Otpks.Count;
                buf.WriteU32((uint)otpkCount);
                for (int o = 0; o < otpkCount; o++)
                {
                    buf.WriteU8(d.Otpks[o].Alg);
                    buf.WriteBytesLp(d.Otpks[o].Pub);
                }
            }
        }
        var bytes = buf.ToArray();
        if (Offset + (ulong)bytes.Length > RegionEnd - 8) return false;   // region-end guard
        WriteBytes(Offset, bytes);
        // Shrink tail-zeroing: scrub the now-orphaned tail so consumed OTPK /
        // removed-device public bytes stop lingering. Clamped by RegionEnd so the
        // zero range can never cross into the neighbouring region.
        ulong newEnd = Offset + (ulong)bytes.Length;
        if (_highWaterEnd > newEnd)
        {
            ulong zEnd = Math.Min(_highWaterEnd, RegionEnd);
            if (zEnd > newEnd) ZeroRange(newEnd, zEnd - newEnd);
        }
        _highWaterEnd = Math.Max(newEnd, _highWaterEnd);
        WriteU32(MagicOffset, Magic);
        WriteU32(MagicOffset + 4, Version);
        return true;
    }

    // ─── helpers ──────────────────────────────────────────────────────
    private static long NowMs() => (long)(Ic0.time() / 1_000_000UL);

    // ─── stable-memory primitives (DmService / VoteService idiom) ─────
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

    // Overwrite [offset, offset+len) with zeros in 4096-byte chunks. Used by
    // Persist() to scrub the orphaned tail on shrink; the caller clamps len to
    // RegionEnd so this never crosses regions — DmService idiom.
    private static void ZeroRange(ulong offset, ulong len)
    {
        if (len == 0) return;
        var zeros = new byte[4096];
        ulong remaining = len;
        ulong at = offset;
        while (remaining > 0)
        {
            int chunk = remaining > 4096UL ? 4096 : (int)remaining;
            fixed (byte* p = zeros) Ic0.stable64_write(at, (ulong)(nint)p, (ulong)chunk);
            at += (ulong)chunk;
            remaining -= (ulong)chunk;
        }
    }

    private sealed class MemoryWriter
    {
        private readonly List<byte> _buf = new();
        public byte[] ToArray() => _buf.ToArray();
        public void WriteU8(byte v) => _buf.Add(v);
        public void WriteU32(uint v) => _buf.AddRange(BitConverter.GetBytes(v));
        public void WriteI64(long v) => _buf.AddRange(BitConverter.GetBytes(v));
        public void WriteString(string s)
        {
            var data = Encoding.UTF8.GetBytes(s ?? "");
            _buf.AddRange(BitConverter.GetBytes(data.Length));
            _buf.AddRange(data);
        }
        // Length-prefixed raw bytes: u32 length + bytes, so 0x00 key bytes survive.
        public void WriteBytesLp(byte[]? b)
        {
            var data = b ?? Array.Empty<byte>();
            _buf.AddRange(BitConverter.GetBytes((uint)data.Length));
            _buf.AddRange(data);
        }
    }

    private sealed class MemoryReader
    {
        public ulong Cursor { get; private set; }
        public MemoryReader(ulong offset) { Cursor = offset; }
        public byte ReadU8() { var b = new byte[1]; ReadBytes(Cursor, b); Cursor += 1; return b[0]; }
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
        // Length-prefixed raw bytes mirror of WriteBytesLp.
        public byte[] ReadBytesLp()
        {
            var len = (int)ReadU32();
            if (len <= 0) return Array.Empty<byte>();
            var b = new byte[len];
            ReadBytes(Cursor, b);
            Cursor += (ulong)len;
            return b;
        }
    }
}
