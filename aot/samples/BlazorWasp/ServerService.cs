using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Wasp.IcCdk;

namespace WaspSample.BlazorWasp;

/// <summary>
/// Discord-style "servers" (guilds) layered ADDITIVELY on top of the
/// existing multi-room chat. A server is a named bucket of channels; each
/// channel is an existing ChatService room id. The server->channelIds
/// association lives ENTIRELY in this new store at offset 6_000_000.
/// ChatService's Room record is never given a serverId field, so its
/// persisted layout at offset 4096 (magic CHAT, version 3) stays
/// byte-identical and the live mainnet rooms survive --mode upgrade.
///
/// The DEFAULT server (id 0, "Wasp") is VIRTUAL: never persisted. Its
/// channel set is derived live = every ChatService room NOT claimed by a
/// user server. So pre-existing rooms 1 (general) & 2 (random) show up
/// under Wasp with zero change to chat's layout.
///
/// Servers are PUBLIC/open (same trust model as ChatService.CreateRoom):
/// anyone may create a server or add a channel. There is no privacy
/// claim here. (DMs are the private surface — see DmService.)
///
/// Stable layout at Offset (header is the IdentityService idiom:
/// magic(4)+version(4) eight bytes BELOW the data offset):
///   uint32 magic "SRVR"            (at MagicOffset)
///   uint32 version 1               (at MagicOffset+4)
///   uint32 serverCount             (at Offset)
///   foreach persisted server (id >= 1):
///     int32  id
///     int32  utf8len(name);  bytes name
///     int64  createdAtMs
///     uint32 channelCount
///     foreach channel: int32 roomId
/// </summary>
public sealed unsafe class ServerService
{
    private const ulong Offset = 6_000_000;
    private const ulong MagicOffset = Offset - 8;   // magic(4)+version(4)
    private const uint Magic = 0x53525652;          // "SRVR"
    private const uint Version = 1;

    public const int DefaultServerId = 0;
    public const string DefaultServerName = "Wasp";

    private const int MaxServers = 200;
    private const int MaxChannelsPerServer = 50;
    private const int MaxNameLen = 24;

    private readonly ChatService _chat;
    public ServerService(ChatService chat) { _chat = chat; }

    public sealed record Server(int Id, string Name, long CreatedAtMs, IReadOnlyList<int> ChannelIds);

    private List<StoredServer>? _servers;
    private int _nextServerId = 1;

    private sealed class StoredServer
    {
        public int Id;
        public string Name = "";
        public long CreatedAtMs;
        public List<int> Channels = new();
    }

    // ─── Public API ───────────────────────────────────────────────────

    /// <summary>All servers, virtual default (id 0 "Wasp") first, then
    /// user-created. The default's channels are every chat room not
    /// claimed by a user server, computed live.</summary>
    public IReadOnlyList<Server> ListServers()
    {
        var stored = LoadServers();
        var list = new List<Server>(1 + stored.Count);
        list.Add(new Server(DefaultServerId, DefaultServerName, 0, DefaultChannelIds()));
        foreach (var s in stored)
            list.Add(new Server(s.Id, s.Name, s.CreatedAtMs, s.Channels.ToArray()));
        return list;
    }

    /// <summary>Channel (room) ids of a server. Server 0 = chat rooms not
    /// claimed by any user server; user servers = their persisted list.</summary>
    public IReadOnlyList<int> ChannelsOf(int serverId)
    {
        if (serverId == DefaultServerId) return DefaultChannelIds();
        var s = LoadServers().FirstOrDefault(x => x.Id == serverId);
        return s is null ? Array.Empty<int>() : s.Channels.ToArray();
    }

    /// <summary>Which server owns a given channel/room? Scans persisted user
    /// servers' channel lists; returns <see cref="DefaultServerId"/> (0) for any
    /// room not claimed by a user server (the virtual "Wasp" server — incl. the
    /// pre-existing general/random rooms). M2-B moderation resolves a message's
    /// server via this to look up the caller's per-server role.</summary>
    public int ServerOfChannel(int roomId)
    {
        foreach (var s in LoadServers())
            if (s.Channels.Contains(roomId)) return s.Id;
        return DefaultServerId;
    }

    /// <summary>Create a public server. Open to anyone. Idempotent on name
    /// (case-insensitive) so a double-submit doesn't duplicate.</summary>
    public Server CreateServer(string name)
    {
        name = SanitiseName(name);
        var servers = LoadServers();
        var existing = servers.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return new Server(existing.Id, existing.Name, existing.CreatedAtMs, existing.Channels.ToArray());
        if (servers.Count >= MaxServers)
            return new Server(DefaultServerId, DefaultServerName, 0, DefaultChannelIds());
        var srv = new StoredServer { Id = _nextServerId++, Name = name, CreatedAtMs = NowMs(), Channels = new() };
        servers.Add(srv);
        PersistServers();
        return new Server(srv.Id, srv.Name, srv.CreatedAtMs, srv.Channels.ToArray());
    }

    /// <summary>Associate an EXISTING ChatService room with a user server.
    /// Records the link in THIS store only — never touches ChatService.
    /// Rejected for the virtual default (id 0). Idempotent. Returns false
    /// if the room doesn't exist, the server doesn't exist, or it's full.</summary>
    public bool AddChannel(int serverId, int roomId)
    {
        if (serverId == DefaultServerId) return false;
        if (_chat.FindRoom(roomId) is null) return false;
        var servers = LoadServers();
        var s = servers.FirstOrDefault(x => x.Id == serverId);
        if (s is null) return false;
        if (s.Channels.Contains(roomId)) return true;
        if (s.Channels.Count >= MaxChannelsPerServer) return false;
        s.Channels.Add(roomId);
        PersistServers();
        return true;
    }

    /// <summary>Permanently remove a user server. Returns the server's channel
    /// room ids so the caller can clean up the underlying ChatService rooms
    /// (this store never touches ChatService). Rejected for the virtual default
    /// (id 0) and for an unknown id (returns an empty array — nothing removed).
    /// Additive: it only shrinks the persisted list, never changes its format.</summary>
    public IReadOnlyList<int> RemoveServer(int serverId)
    {
        if (serverId == DefaultServerId) return Array.Empty<int>();
        var servers = LoadServers();
        var s = servers.FirstOrDefault(x => x.Id == serverId);
        if (s is null) return Array.Empty<int>();
        var rooms = s.Channels.ToArray();
        servers.Remove(s);
        PersistServers();
        return rooms;
    }

    // ─── Virtual default ──────────────────────────────────────────────
    private int[] DefaultChannelIds()
    {
        var claimed = new HashSet<int>();
        foreach (var s in LoadServers())
            foreach (var c in s.Channels) claimed.Add(c);
        return _chat.Rooms.Select(r => r.Id).Where(id => !claimed.Contains(id)).ToArray();
    }

    // ─── storage ──────────────────────────────────────────────────────
    private List<StoredServer> LoadServers()
    {
        if (_servers is not null) return _servers;
        EnsureGrown(Offset);
        _servers = new List<StoredServer>();
        if (ReadU32(MagicOffset) != Magic || ReadU32(MagicOffset + 4) != Version)
            return _servers;
        var r = new MemoryReader(Offset);
        var count = (int)r.ReadU32();
        if (count < 0 || count > MaxServers) { _servers = new(); return _servers; }
        for (int i = 0; i < count; i++)
        {
            var s = new StoredServer { Id = r.ReadI32(), Name = r.ReadString(), CreatedAtMs = r.ReadI64() };
            var ch = (int)r.ReadU32();
            if (ch < 0 || ch > MaxChannelsPerServer) ch = 0;
            for (int j = 0; j < ch; j++) s.Channels.Add(r.ReadI32());
            _servers.Add(s);
        }
        _nextServerId = _servers.Count == 0 ? 1 : _servers.Max(s => s.Id) + 1;
        return _servers;
    }

    private void PersistServers()
    {
        var servers = _servers!;
        var buf = new MemoryWriter();
        buf.WriteU32((uint)servers.Count);
        foreach (var s in servers)
        {
            buf.WriteI32(s.Id);
            buf.WriteString(s.Name);
            buf.WriteI64(s.CreatedAtMs);
            buf.WriteU32((uint)s.Channels.Count);
            foreach (var c in s.Channels) buf.WriteI32(c);
        }
        WriteBytes(Offset, buf.ToArray());
        WriteU32(MagicOffset, Magic);
        WriteU32(MagicOffset + 4, Version);
    }

    // ─── helpers ──────────────────────────────────────────────────────
    private static long NowMs() => (long)(Ic0.time() / 1_000_000UL);

    private static string SanitiseName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "untitled";
        raw = raw.Trim();
        if (raw.Length > MaxNameLen) raw = raw.Substring(0, MaxNameLen);
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw) if (c != '|' && c >= 0x20) sb.Append(c);
        var cleaned = sb.ToString();
        return string.IsNullOrEmpty(cleaned) ? "untitled" : cleaned;
    }

    // ─── stable-memory primitives (IdentityService idiom) ─────────────
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

