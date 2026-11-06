using System;
using System.Collections.Generic;
using Wasp.IcCdk;

namespace WaspSample.BlazorWasp;

/// <summary>
/// Discourse-style FORUM state, layered ADDITIVELY on the existing substrate.
/// A forum CATEGORY is a forum-kind <see cref="ServerService"/> server; a
/// TOPIC is an ordinary <see cref="ChatService"/> room inside it (so the
/// category→topic association, locks, pins, roles and membership all reuse
/// ServerService/ModService/RoleService unchanged). The OP is the topic
/// room's first message; replies are the rest. Reactions, threads, edit/delete
/// and search all work on those messages with zero change.
///
/// This service stores ONLY what ChatService can't represent for a topic:
///   • the full TITLE (chat room names are short/slugged), and
///   • the accepted-answer (SOLVED) message pointer.
/// Everything else (reply count, last-activity, OP author, tags-via-#hashtags)
/// is DERIVED live from ChatService, so no duplicated/denormalised state.
///
/// Region at offset 81M, magic "FRUM" v1, header-below-data idiom, self
/// RegionEnd guard at 85M. Never touches CHAT (4096 v3) or SRVR (6M v1).
///
/// Layout at Offset:
///   uint32 magic "FRUM"            (at MagicOffset)
///   uint32 version 1               (at MagicOffset+4)
///   uint32 topicCount              (at Offset)
///   foreach topic:
///     int32  topicRoomId
///     int32  categoryServerId
///     int64  solvedMessageId       (0 = unsolved)
///     int32  utf8len(title); bytes title
/// </summary>
public sealed unsafe class ForumService
{
    private const ulong Offset = 81_000_000;
    private const ulong MagicOffset = Offset - 8;
    private const ulong RegionEnd = 85_000_000;     // VoteService (VOTE) would start here
    private const uint Magic = 0x4652554D;          // "FRUM"
    private const uint Version = 1;
    private const int MaxTopics = 20_000;
    private const int MaxTitleLen = 160;

    private Dictionary<int, Topic>? _topics;

    public sealed class Topic
    {
        public int RoomId;
        public int CategoryServerId;
        public long SolvedMessageId;
        public string Title = "";
    }

    // ─── Public API ───────────────────────────────────────────────────

    /// <summary>Record a new topic. The caller has already created the room
    /// (ChatService) + linked it to the category (ServerService.AddChannel)
    /// and posted the OP. Idempotent on roomId.</summary>
    public void RegisterTopic(int roomId, int categoryServerId, string title)
    {
        if (roomId <= 0) return;
        title = SanitiseTitle(title);
        var map = Load();
        if (map.TryGetValue(roomId, out var t)) { t.Title = title; t.CategoryServerId = categoryServerId; }
        else
        {
            if (map.Count >= MaxTopics) return;
            map[roomId] = new Topic { RoomId = roomId, CategoryServerId = categoryServerId, SolvedMessageId = 0, Title = title };
        }
        Persist();
    }

    public bool IsTopic(int roomId) => Load().ContainsKey(roomId);

    public string TitleOf(int roomId) => Load().TryGetValue(roomId, out var t) ? t.Title : "";

    public long SolvedOf(int roomId) => Load().TryGetValue(roomId, out var t) ? t.SolvedMessageId : 0;

    /// <summary>Mark (or clear, with msgId 0) the accepted answer for a topic.
    /// Authorization is the caller's concern (OP author or moderator).</summary>
    public bool SetSolved(int roomId, long messageId)
    {
        var map = Load();
        if (!map.TryGetValue(roomId, out var t)) return false;
        t.SolvedMessageId = messageId < 0 ? 0 : messageId;
        Persist();
        return true;
    }

    /// <summary>All topic roomIds in a category (the category's channels that
    /// are registered topics).</summary>
    public IReadOnlyList<Topic> TopicsIn(int categoryServerId)
    {
        var list = new List<Topic>();
        foreach (var t in Load().Values)
            if (t.CategoryServerId == categoryServerId) list.Add(t);
        return list;
    }

    // ─── storage ──────────────────────────────────────────────────────
    private Dictionary<int, Topic> Load()
    {
        if (_topics is not null) return _topics;
        EnsureGrown(Offset);
        _topics = new Dictionary<int, Topic>();
        if (ReadU32(MagicOffset) != Magic || ReadU32(MagicOffset + 4) != Version)
            return _topics;
        var r = new MemoryReader(Offset);
        var count = (int)r.ReadU32();
        if (count < 0 || count > MaxTopics) { _topics = new(); return _topics; }
        for (int i = 0; i < count; i++)
        {
            var t = new Topic
            {
                RoomId = r.ReadI32(),
                CategoryServerId = r.ReadI32(),
                SolvedMessageId = r.ReadI64(),
                Title = r.ReadString(),
            };
            if (t.RoomId > 0) _topics[t.RoomId] = t;
        }
        return _topics;
    }

    private void Persist()
    {
        var map = _topics!;
        var buf = new MemoryWriter();
        buf.WriteU32((uint)map.Count);
        foreach (var t in map.Values)
        {
            buf.WriteI32(t.RoomId);
            buf.WriteI32(t.CategoryServerId);
            buf.WriteI64(t.SolvedMessageId);
            buf.WriteString(t.Title);
        }
        var bytes = buf.ToArray();
        if (Offset + (ulong)bytes.Length > RegionEnd - 8) return;   // reserve top 8 bytes for the next region magic header
        WriteBytes(Offset, bytes);
        WriteU32(MagicOffset, Magic);
        WriteU32(MagicOffset + 4, Version);
    }

    private static string SanitiseTitle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Untitled topic";
        raw = raw.Trim();
        if (raw.Length > MaxTitleLen) raw = raw.Substring(0, MaxTitleLen);
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var c in raw) if (c >= 0x20) sb.Append(c);
        var cleaned = sb.ToString();
        return string.IsNullOrEmpty(cleaned) ? "Untitled topic" : cleaned;
    }

    // ─── stable-memory primitives (ServerService idiom) ───────────────
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
            var data = System.Text.Encoding.UTF8.GetBytes(s ?? "");
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
            return System.Text.Encoding.UTF8.GetString(b);
        }
    }
}
