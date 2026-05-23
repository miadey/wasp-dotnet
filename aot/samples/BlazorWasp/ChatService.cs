using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Wasp.IcCdk;

namespace WaspSample.BlazorWasp;

/// <summary>
/// Multi-room chat persisted in stable memory.
///
/// Layout at offset 4096:
///   uint32 magic ("CHAT")
///   uint32 version (3)         // v3 adds message.Id
///   uint32 roomCount
///   foreach room:
///     int32  id
///     int32  utf8len(name)
///     bytes  name
///     int64  createdAtMs
///   uint32 messageCount
///   foreach message (capped at MaxMessages, FIFO drop):
///     int64  id              // monotonic, used to key reactions
///     int32  roomId
///     int64  atMs
///     int32  utf8len(username|text)
///     bytes  "username|text"
///
/// After the messages section, a "REAC" sentinel + reactions table is
/// appended; older blobs that pre-date this section are detected by
/// the missing sentinel and produce an empty reactions dict, so the
/// format change is backwards-compatible (no schema-version bump).
///
/// Reactions table:
///   uint32 magic ("REAC")
///   uint32 entryCount
///   foreach entry:
///     int64  messageId
///     uint32 emojiCount
///     foreach emoji:
///       utf8len + bytes (emoji string)
///       int32 (count)
/// </summary>
public sealed unsafe class ChatService
{
    private const ulong Offset = 4096;
    private const ulong MagicOffset = 4092;
    private const uint Magic = 0x43484154;        // "CHAT"
    private const uint Version = 3;
    private const int MaxMessages = 500;          // 50 per room × 10 rooms ish

    /// <summary>The five emojis users can react with. Locked down to a
    /// small set so the heap dict has predictable shape + so we can
    /// validate input.</summary>
    public static readonly string[] ReactionEmojis = new[]
    {
        "\U0001F44D",   // 👍
        "❤️", // ❤️
        "\U0001F600",   // 😀
        "\U0001F62E",   // 😮
        "\U0001F389",   // 🎉
    };

    public sealed record Room(int Id, string Name, long CreatedAtMs);
    public sealed record Message(
        long Id, int RoomId, long AtMs,
        string Username, string Text,
        long ReplyToId,   // 0 = not a reply
        long ImageId);    // 0 = no image attached

    private List<Room>? _rooms;
    private List<Message>? _messages;
    private int _nextRoomId = 1;
    private long _nextMsgId = 1;
    private readonly Dictionary<long, Dictionary<string, int>> _reactions = new();

    public IReadOnlyList<Room> Rooms => Load().rooms;

    public IReadOnlyList<Message> MessagesIn(int roomId)
    {
        var (_, msgs) = Load();
        return msgs.Where(m => m.RoomId == roomId).TakeLast(50).ToArray();
    }

    public Room? FindRoom(int id) => Rooms.FirstOrDefault(r => r.Id == id);
    public Room? FindRoomByName(string name) => Rooms.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    public Message? FindMessage(long id)
    {
        var (_, msgs) = Load();
        return msgs.FirstOrDefault(m => m.Id == id);
    }

    public Room CreateRoom(string name)
    {
        name = SanitiseName(name);
        var (rooms, msgs) = Load();
        // Idempotent — return existing if name matches.
        var existing = rooms.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
        var room = new Room(_nextRoomId++, name, NowMs());
        rooms.Add(room);
        Persist(rooms, msgs);
        return room;
    }

    public void Post(int roomId, IReadOnlyDictionary<string, string> args)
    {
        long imageId = 0;
        if (args.TryGetValue("imageId", out var iidStr)) long.TryParse(iidStr, out imageId);
        // Message must have either text or an image (or both).
        args.TryGetValue("text", out var text);
        text ??= "";
        if (text.Length > 280) text = text.Substring(0, 280);
        if (string.IsNullOrWhiteSpace(text) && imageId == 0) return;
        args.TryGetValue("username", out var username);
        username = SanitiseUsername(username);
        long replyToId = 0;
        if (args.TryGetValue("replyTo", out var rtsStr)) long.TryParse(rtsStr, out replyToId);
        var (rooms, msgs) = Load();
        if (!rooms.Any(r => r.Id == roomId)) return;
        // Validate replyToId points to a real message (in any room).
        if (replyToId > 0 && !msgs.Any(m => m.Id == replyToId)) replyToId = 0;
        msgs.Add(new Message(_nextMsgId++, roomId, NowMs(), username, text, replyToId, imageId));
        if (msgs.Count > MaxMessages)
        {
            msgs = msgs.GetRange(msgs.Count - MaxMessages, MaxMessages);
        }
        Persist(rooms, msgs);
    }

    /// <summary>Increment the reaction counter for (messageId, emoji).
    /// Silently no-ops if the emoji isn't in the allowed set or the
    /// message doesn't exist.</summary>
    public void React(long messageId, string emoji)
    {
        if (string.IsNullOrEmpty(emoji)) return;
        if (Array.IndexOf(ReactionEmojis, emoji) < 0) return;
        var (rooms, msgs) = Load();
        if (!msgs.Any(m => m.Id == messageId)) return;
        if (!_reactions.TryGetValue(messageId, out var dict))
        {
            dict = new Dictionary<string, int>();
            _reactions[messageId] = dict;
        }
        dict.TryGetValue(emoji, out var n);
        dict[emoji] = n + 1;
        Persist(rooms, msgs);   // include the updated reactions table
    }

    public IReadOnlyDictionary<string, int> ReactionsOf(long messageId)
    {
        return _reactions.TryGetValue(messageId, out var d)
            ? d
            : (IReadOnlyDictionary<string, int>)EmptyReactions;
    }
    private static readonly Dictionary<string, int> EmptyReactions = new();

    // ─── storage ─────────────────────────────────────────────────────
    private (List<Room> rooms, List<Message> msgs) Load()
    {
        if (_rooms is not null && _messages is not null) return (_rooms, _messages);
        EnsureGrown();
        if (ReadMagic() != Magic || ReadVersion() != Version)
        {
            _rooms = new List<Room>
            {
                new(1, "general", NowMs()),
                new(2, "random",  NowMs()),
            };
            _messages = new List<Message>();
            _nextRoomId = 3;
            Persist(_rooms, _messages);
            return (_rooms, _messages);
        }
        _rooms = ReadRooms();
        _messages = ReadMessages();
        ReadReactions();
        _nextRoomId = _rooms.Count == 0 ? 1 : _rooms.Max(r => r.Id) + 1;
        _nextMsgId  = _messages.Count == 0 ? 1 : _messages.Max(m => m.Id) + 1;
        return (_rooms, _messages);
    }

    private void Persist(List<Room> rooms, List<Message> messages)
    {
        _rooms = rooms; _messages = messages;
        var buf = new MemoryWriter();
        buf.WriteU32((uint)rooms.Count);
        foreach (var r in rooms)
        {
            buf.WriteI32(r.Id);
            buf.WriteString(r.Name);
            buf.WriteI64(r.CreatedAtMs);
        }
        buf.WriteU32((uint)messages.Count);
        foreach (var m in messages)
        {
            buf.WriteI64(m.Id);
            buf.WriteI32(m.RoomId);
            buf.WriteI64(m.AtMs);
            // New 4-field format separated by U+001E (RECORD SEPARATOR);
            // can't appear in user input, so unlike `|` it's safe to
            // wedge into the username|text bag without escape handling.
            // ReadMessages still parses the legacy `|` split so existing
            // blobs round-trip.
            buf.WriteString(
                m.Username + "" + m.Text + "" +
                m.ReplyToId + "" + m.ImageId);
        }
        // Reactions section — REAC sentinel + table. Sentinel lets
        // older blobs (written before reactions were persisted) be
        // detected on load: if the bytes after messages don't start
        // with REAC, treat as no reactions.
        buf.WriteU32(ReactionsMagic);
        buf.WriteU32((uint)_reactions.Count);
        foreach (var kv in _reactions)
        {
            buf.WriteI64(kv.Key);
            buf.WriteU32((uint)kv.Value.Count);
            foreach (var ek in kv.Value)
            {
                buf.WriteString(ek.Key);
                buf.WriteI32(ek.Value);
            }
        }
        var bytes = buf.ToArray();
        WriteBytes(Offset, bytes);
        WriteMagic();
    }

    private const uint ReactionsMagic = 0x52454143;   // "REAC"

    private List<Room> ReadRooms()
    {
        var r = new MemoryReader(Offset);
        var count = (int)r.ReadU32();
        var rooms = new List<Room>(count);
        for (int i = 0; i < count; i++)
        {
            var id = r.ReadI32();
            var name = r.ReadString();
            var atMs = r.ReadI64();
            rooms.Add(new Room(id, name, atMs));
        }
        _afterRoomsOffset = r.Cursor;
        return rooms;
    }

    private ulong _afterRoomsOffset;

    private List<Message> ReadMessages()
    {
        var r = new MemoryReader(_afterRoomsOffset);
        var count = (int)r.ReadU32();
        var msgs = new List<Message>(count);
        for (int i = 0; i < count; i++)
        {
            var id = r.ReadI64();
            var roomId = r.ReadI32();
            var atMs = r.ReadI64();
            var s = r.ReadString();
            string username; string text; long replyToId = 0; long imageId = 0;
            // New format: U+001E-separated 4 fields.
            if (s.IndexOf('\u001E') >= 0)
            {
                var parts = s.Split('\u001E', 4);
                username = parts[0];
                text     = parts.Length > 1 ? parts[1] : "";
                if (parts.Length > 2) long.TryParse(parts[2], out replyToId);
                if (parts.Length > 3) long.TryParse(parts[3], out imageId);
            }
            else
            {
                // Legacy format: "username|text" (text may contain |).
                var sep = s.IndexOf('|');
                username = sep > 0 ? s.Substring(0, sep) : "Anonymous";
                text = sep > 0 ? s.Substring(sep + 1) : s;
            }
            msgs.Add(new Message(id, roomId, atMs, username, text, replyToId, imageId));
        }
        _afterMessagesOffset = r.Cursor;
        return msgs;
    }

    private ulong _afterMessagesOffset;

    private void ReadReactions()
    {
        _reactions.Clear();
        var r = new MemoryReader(_afterMessagesOffset);
        uint magic;
        try { magic = r.ReadU32(); }
        catch { return; }
        if (magic != ReactionsMagic) return;   // pre-reactions blob
        var entries = (int)r.ReadU32();
        for (int i = 0; i < entries; i++)
        {
            var msgId = r.ReadI64();
            var kCount = (int)r.ReadU32();
            var inner = new Dictionary<string, int>(kCount);
            for (int j = 0; j < kCount; j++)
            {
                var emoji = r.ReadString();
                var n = r.ReadI32();
                inner[emoji] = n;
            }
            _reactions[msgId] = inner;
        }
    }

    // ─── helpers ─────────────────────────────────────────────────────
    private static long NowMs() => (long)(Ic0.time() / 1_000_000UL);

    private static string SanitiseName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "untitled";
        raw = new string(raw.Trim().Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray()).ToLowerInvariant();
        if (raw.Length == 0) return "untitled";
        if (raw.Length > 20) raw = raw.Substring(0, 20);
        return raw;
    }

    private static string SanitiseUsername(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Anonymous";
        raw = raw.Trim();
        if (raw.Length > 24) raw = raw.Substring(0, 24);
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw) if (c != '|' && c >= 0x20) sb.Append(c);
        var cleaned = sb.ToString();
        return string.IsNullOrEmpty(cleaned) ? "Anonymous" : cleaned;
    }

    // ─── stable-memory primitives ─────────────────────────────────────
    private uint ReadMagic() { var b = new byte[4]; ReadBytes(MagicOffset, b); return BitConverter.ToUInt32(b); }
    private void WriteMagic() { WriteBytes(MagicOffset, BitConverter.GetBytes(Magic)); WriteBytes(MagicOffset - 4, BitConverter.GetBytes(Version)); }
    private uint ReadVersion() { var b = new byte[4]; ReadBytes(MagicOffset - 4, b); return BitConverter.ToUInt32(b); }
    private static void EnsureGrown() { if (Ic0.stable64_size() < 1) Ic0.stable64_grow(1); }

    internal static void ReadBytes(ulong offset, byte[] buf)
    { fixed (byte* p = buf) Ic0.stable64_read((ulong)(nint)p, offset, (ulong)buf.Length); }
    internal static void WriteBytes(ulong offset, byte[] buf)
    {
        ulong needed = offset + (ulong)buf.Length;
        ulong haveBytes = Ic0.stable64_size() * 65536UL;
        if (needed > haveBytes) Ic0.stable64_grow(((needed - haveBytes) + 65535UL) / 65536UL);
        fixed (byte* p = buf) Ic0.stable64_write(offset, (ulong)(nint)p, (ulong)buf.Length);
    }

    // ─── binary read/write helpers ───────────────────────────────────
    private sealed class MemoryWriter
    {
        private readonly List<byte> _buf = new();
        public byte[] ToArray() => _buf.ToArray();
        public void WriteU32(uint v) => _buf.AddRange(BitConverter.GetBytes(v));
        public void WriteI32(int v) => _buf.AddRange(BitConverter.GetBytes(v));
        public void WriteI64(long v) => _buf.AddRange(BitConverter.GetBytes(v));
        public void WriteString(string s)
        {
            var data = Encoding.UTF8.GetBytes(s);
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
