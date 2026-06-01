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
///     int32  utf8len(payload)
///     bytes  payload         // U+001E-separated fields, currently 6:
///                            //   username  text  replyToId  imageId
///                            //   editedAtMs  deletedAtMs
///
/// The payload is widened by APPENDING trailing U+001E fields only — old
/// blobs with fewer fields read back with the new fields defaulting to 0
/// (see ReadMessages). This keeps the format backward-compatible WITHOUT a
/// version bump, so existing mainnet messages survive an upgrade. NEVER
/// bump Version or reorder the fixed id/roomId/atMs prefix — Load() resets
/// to default rooms (wiping all messages) on any magic/version mismatch.
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

    /// <summary>The six "quick" reaction emojis shown inline in the
    /// composer bar and as the first row of the reaction popover.</summary>
    public static readonly string[] ReactionEmojis = new[]
    {
        "\U0001F44D",   // 👍
        "❤️", // ❤️
        "\U0001F600",   // 😀
        "\U0001F62E",   // 😮
        "\U0001F389",   // 🎉
        "\U0001F32F",   // 🌯  fajitas / burrito
    };

    /// <summary>Fuller emoji set surfaced in the grid picker (both the
    /// per-message reaction popover and the composer's full picker). The
    /// REAC table stores the emoji as a variable-length utf8 string, so
    /// widening this set is format-compatible — no schema bump.</summary>
    public static readonly string[] PickerEmojis = new[]
    {
        "\U0001F44D", "\U0001F44E", "❤️", "\U0001F525", "\U0001F389", "\U0001F600",
        "\U0001F602", "\U0001F923", "\U0001F60D", "\U0001F60E", "\U0001F914", "\U0001F644",
        "\U0001F62E", "\U0001F633", "\U0001F62D", "\U0001F621", "\U0001F44F", "\U0001F64C",
        "\U0001F64F", "\U0001F4AF", "\U0001F680", "\U0001F44C", "\U0001F91D", "\U0001F9E0",
        "\U0001F440", "\U0001F4A9", "\U0001F480", "\U0001F47B", "\U0001F916", "\U0001F32F",
        "\U0001F37B", "\U0001F44B", "✅",
    };

    /// <summary>Validation set for <see cref="React"/> — the union of the
    /// quick + picker emoji. Reactions outside this set are rejected so
    /// the persisted REAC table can't be stuffed with arbitrary blobs.</summary>
    private static readonly HashSet<string> AllowedReactions =
        new(System.Linq.Enumerable.Concat(ReactionEmojis, PickerEmojis));

    public sealed record Room(int Id, string Name, long CreatedAtMs);
    public sealed record Message(
        long Id, int RoomId, long AtMs,
        string Username, string Text,
        long ReplyToId,    // 0 = not a reply
        long ImageId,      // 0 = no image attached
        long EditedAtMs = 0,    // 0 = never edited; ms timestamp of last edit
        long DeletedAtMs = 0,   // 0 = live; ms timestamp when tombstoned
        string AuthorPrincipal = "")  // M2-B: textual II principal of the signed
                                      // author; "" = legacy/unverified (the 50
                                      // pre-M2-B anonymous messages). Appended as
                                      // a 7th U+001E field — NO Version bump.
    {
        public bool IsDeleted => DeletedAtMs > 0;
        public bool IsEdited => EditedAtMs > 0;
        public bool IsOwned => AuthorPrincipal.Length > 0;   // has a verified author
    }

    private List<Room>? _rooms;
    private List<Message>? _messages;
    private int _nextRoomId = 1;
    private long _nextMsgId = 1;
    private readonly Dictionary<long, Dictionary<string, int>> _reactions = new();

    public IReadOnlyList<Room> Rooms => Load().rooms;

    public IReadOnlyList<Message> MessagesIn(int roomId, int count = 50)
    {
        if (count < 1) count = 50; if (count > 500) count = 500;   // "load older" window, bounded
        var (_, msgs) = Load();
        return msgs.Where(m => m.RoomId == roomId).TakeLast(count).ToArray();
    }

    /// <summary>Total live+tombstoned message count in a room (to know if older
    /// messages exist beyond the current window).</summary>
    public int MessageCountIn(int roomId)
    {
        var (_, msgs) = Load();
        int n = 0; foreach (var m in msgs) if (m.RoomId == roomId) n++;
        return n;
    }

    /// <summary>All messages across rooms, oldest first. Used by the
    /// /stable explorer; chat UI uses <see cref="MessagesIn"/>.</summary>
    public IReadOnlyList<Message> AllMessages() => Load().msgs;

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

    /// <summary>Create a room WITHOUT the name-idempotency dedup — always a new
    /// room id. Forum topics need this: two topics may share a title (e.g.
    /// "Help"), and the forum addresses topics by roomId, not name. Layout
    /// unchanged (no Version bump); just skips the FirstOrDefault dedup.</summary>
    public Room CreateRoomForced(string name)
    {
        name = SanitiseName(name);
        var (rooms, msgs) = Load();
        var room = new Room(_nextRoomId++, name, NowMs());
        rooms.Add(room);
        Persist(rooms, msgs);
        return room;
    }

    /// <summary>Permanently remove a room and every message in it. Used by the
    /// admin "delete server" path to fully erase a deleted server's channels
    /// (otherwise an orphaned room would resurface under the virtual default).
    /// Additive: shrinks the persisted lists only — the on-disk format and the
    /// other rooms/messages are untouched. Returns true if the room existed.</summary>
    public bool RemoveRoom(int roomId)
    {
        var (rooms, msgs) = Load();
        var removed = rooms.RemoveAll(r => r.Id == roomId);
        if (removed == 0) return false;
        msgs.RemoveAll(m => m.RoomId == roomId);
        Persist(rooms, msgs);
        return true;
    }

    public void Post(int roomId, IReadOnlyDictionary<string, string> args, string authorPrincipal = "")
    {
        long imageId = 0;
        if (args.TryGetValue("imageId", out var iidStr)) long.TryParse(iidStr, out imageId);
        // Message must have either text or an image (or both).
        args.TryGetValue("text", out var text);
        text = SanitiseText(text);
        if (string.IsNullOrWhiteSpace(text) && imageId == 0) return;
        args.TryGetValue("username", out var username);
        username = SanitiseUsername(username);
        long replyToId = 0;
        if (args.TryGetValue("replyTo", out var rtsStr)) long.TryParse(rtsStr, out replyToId);
        var (rooms, msgs) = Load();
        if (!rooms.Any(r => r.Id == roomId)) return;
        // Validate replyToId points to a real message (in any room).
        if (replyToId > 0 && !msgs.Any(m => m.Id == replyToId)) replyToId = 0;
        // M2-B: authorPrincipal is the signed caller's textual principal ("" only
        // on the legacy anonymous path, which is being retired). It is what lets
        // own-message edit/delete be authorized by identity rather than a
        // spoofable display name.
        msgs.Add(new Message(_nextMsgId++, roomId, NowMs(), username, text, replyToId, imageId, AuthorPrincipal: authorPrincipal));
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
        if (!AllowedReactions.Contains(emoji)) return;
        var (rooms, msgs) = Load();
        var target = msgs.FirstOrDefault(m => m.Id == messageId);
        if (target is null || target.IsDeleted) return;   // can't react to a tombstone
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

    /// <summary>Edit a message's text in place (sets <see cref="Message.EditedAtMs"/>).
    /// M2-B: authorized by the SIGNED caller principal — the editor must be the
    /// verified author (<c>m.AuthorPrincipal == callerPrincipal</c>) OR a moderator
    /// (<paramref name="canModerate"/>). Legacy/unowned messages (AuthorPrincipal=="")
    /// have no verifiable owner, so only a moderator can touch them.
    /// <paramref name="callerPrincipal"/> is AdminService.ToText(CurrentCaller())
    /// from a signed update call — NEVER a client-supplied string, so the old
    /// display-name spoof is gone. No-ops on unknown/deleted messages.</summary>
    public bool Edit(long messageId, string newText, string callerPrincipal, bool canModerate = false)
    {
        newText = SanitiseText(newText);
        if (string.IsNullOrWhiteSpace(newText)) return false;   // empty edit = use delete instead
        var (rooms, msgs) = Load();
        var idx = msgs.FindIndex(m => m.Id == messageId);
        if (idx < 0) return false;
        var m = msgs[idx];
        if (m.IsDeleted) return false;
        if (!canModerate && !(m.IsOwned && !string.IsNullOrEmpty(callerPrincipal)
                              && m.AuthorPrincipal.Equals(callerPrincipal, StringComparison.Ordinal)))
            return false;
        msgs[idx] = m with { Text = newText, EditedAtMs = NowMs() };
        Persist(rooms, msgs);
        return true;
    }

    /// <summary>Tombstone a message: keep the row (so the reply graph and
    /// id sequence stay intact) but blank its text/image and drop its
    /// reactions. M2-B: same signed-principal authorization as <see cref="Edit"/>
    /// — verified author OR moderator; legacy/unowned messages are mod-only.</summary>
    public bool Delete(long messageId, string callerPrincipal, bool canModerate = false)
    {
        var (rooms, msgs) = Load();
        var idx = msgs.FindIndex(m => m.Id == messageId);
        if (idx < 0) return false;
        var m = msgs[idx];
        if (m.IsDeleted) return false;
        if (!canModerate && !(m.IsOwned && !string.IsNullOrEmpty(callerPrincipal)
                              && m.AuthorPrincipal.Equals(callerPrincipal, StringComparison.Ordinal)))
            return false;
        msgs[idx] = m with { Text = "", ImageId = 0, DeletedAtMs = NowMs() };
        _reactions.Remove(messageId);
        Persist(rooms, msgs);
        return true;
    }

    /// <summary>Highest message id per room across all loaded messages.
    /// Backs the client-side unread/badge logic: the client compares
    /// these against its localStorage last-read cursors. Pure query.</summary>
    public IReadOnlyDictionary<int, long> LatestMsgIdPerRoom()
    {
        var (_, msgs) = Load();
        var d = new Dictionary<int, long>();
        foreach (var m in msgs)
            if (!d.TryGetValue(m.RoomId, out var cur) || m.Id > cur) d[m.RoomId] = m.Id;
        return d;
    }

    /// <summary>Online threshold for the member rail: anyone whose
    /// last message landed within this window is shown as "online".</summary>
    public const long OnlineWindowMs = 5 * 60 * 1000;

    public sealed record Member(string Username, long LastSeenMs, bool Online);

    /// <summary>Derive the member list. Authorship history supplies the
    /// "ever been here" set; <paramref name="activeNames"/> (live
    /// presence-heartbeat names from <see cref="PresenceService"/>) gets
    /// merged in so just *viewing* the site counts as online — without
    /// it, a logged-in user who hasn't posted in the last 5 min would
    /// silently fall off the rail. Generic placeholder names ("Visitor",
    /// "Guest") are skipped so anon viewers don't crowd the list. Cap
    /// at 80 to bound render cost.</summary>
    public IReadOnlyList<Member> ListMembers(IReadOnlyCollection<string>? activeNames = null)
    {
        var (_, msgs) = Load();
        var latest = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var m in msgs)
        {
            if (string.IsNullOrEmpty(m.Username)) continue;
            if (!latest.TryGetValue(m.Username, out var prev) || m.AtMs > prev)
                latest[m.Username] = m.AtMs;
        }
        var now = NowMs();
        if (activeNames is not null)
        {
            foreach (var name in activeNames)
            {
                if (string.IsNullOrEmpty(name)) continue;
                // Skip the wasp.js / presence defaults — they'd collapse
                // every anon viewer into a single bogus "Visitor" row.
                if (name == "Visitor" || name == "Guest" || name == "Anonymous") continue;
                latest[name] = now;   // force online + bump last-seen
            }
        }
        var cutoff = now - OnlineWindowMs;
        var members = latest
            .Select(kv => new Member(kv.Key, kv.Value, kv.Value >= cutoff))
            .OrderByDescending(m => m.Online)
            .ThenByDescending(m => m.LastSeenMs)
            .Take(80)
            .ToList();
        return members;
    }

    /// <summary>Kind of an inline <see cref="Segment"/>: plain text, an
    /// @mention, an http(s) link, or an inline code span.</summary>
    public enum SegKind : byte { Text, Mention, Link, Code, Spoiler, Hashtag }

    /// <summary>One inline run produced by <see cref="ParseSegments"/>.
    /// <see cref="Kind"/> selects how it renders; Bold/Italic/Strike/
    /// Underline are style flags that stack on Text/Mention/Link runs
    /// (Code is always literal + unstyled). For a mention, <see cref="Value"/>
    /// is the bare username (no leading @); for a link, Value is the href
    /// (also used as display text).</summary>
    public readonly record struct Segment(
        SegKind Kind, string Value,
        bool Bold = false, bool Italic = false,
        bool Strike = false, bool Underline = false)
    {
        public bool IsMention => Kind == SegKind.Mention;
        public bool IsLink    => Kind == SegKind.Link;
        public bool IsCode    => Kind == SegKind.Code;
        public bool IsText    => Kind == SegKind.Text;
        public bool IsSpoiler => Kind == SegKind.Spoiler;   // Value = inner text (self-contained, no nesting)
        public bool IsHashtag => Kind == SegKind.Hashtag;   // Value = bare tag (no leading '#')
        public bool HasStyle  => Bold || Italic || Strike || Underline;
    }

    /// <summary>Tokenize a message body into styled inline runs: a tiny
    /// hand-rolled Markdown subset (no library — NativeAOT size budget).
    /// Supports <c>`code`</c>, <c>**bold**</c>, <c>*italic*</c>,
    /// <c>~~strike~~</c>, <c>__underline__</c> as matched pairs (an
    /// unmatched delimiter is left as literal text), plus http(s) URLs and
    /// <c>@username</c> mentions. A mention is <c>@</c> followed by 1-24
    /// username chars and must not follow a word character (so an email
    /// like <c>foo@bar</c> doesn't pull a mention out of <c>bar</c>).</summary>
    public static IReadOnlyList<Segment> ParseSegments(string text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<Segment>();
        var segs = new List<Segment>();
        ParseInline(text, false, false, false, false, segs);
        return segs;
    }

    /// <summary>True if <paramref name="text"/> contains a #hashtag whose bare
    /// value equals <paramref name="tag"/> (case-insensitive, no leading '#').
    /// Reuses the render tokenizer via <see cref="ParseBlocks"/>, so a '#word'
    /// inside a code fence never matches (Code blocks carry no segments). This
    /// keeps the ?tag= filtered channel view exactly in sync with what renders
    /// as a clickable #pill.</summary>
    public static bool MessageHasTag(string text, string tag)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(tag)) return false;
        foreach (var b in ParseBlocks(text))
            foreach (var s in b.Segments)
                if (s.IsHashtag && string.Equals(s.Value, tag, StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
    }

    /// <summary>Block kind for the line-level pre-scan: a paragraph (inline-
    /// tokenized), a fenced code block (verbatim, monospace), or a blockquote
    /// (inline-tokenized). Block constructs must be split out BEFORE the inline
    /// tokenizer runs — the inline loop works on one run and would tear a
    /// multi-line fence apart on '*'/'@'.</summary>
    public enum BlockKind : byte { Para, Code, Quote }

    /// <summary>One block. Para/Quote carry inline <see cref="Segments"/>;
    /// Code carries verbatim <see cref="Text"/> + a sanitized <see cref="Lang"/>.</summary>
    public sealed record Block(BlockKind Kind, IReadOnlyList<Segment> Segments, string Text, string Lang);

    /// <summary>Pre-scan a message into blocks: triple-backtick fences become
    /// Code blocks (verbatim), runs of leading '&gt;' lines become one Quote,
    /// everything else is a Para inline-tokenized by ParseSegments. No regex
    /// (NativeAOT size). All text is Blazor-auto-escaped at render → no XSS.</summary>
    public static IReadOnlyList<Block> ParseBlocks(string text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<Block>();
        var blocks = new List<Block>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int n = lines.Length, idx = 0;
        var para = new StringBuilder();

        void FlushPara()
        {
            if (para.Length == 0) return;
            blocks.Add(new Block(BlockKind.Para, ParseSegments(para.ToString()), "", ""));
            para.Clear();
        }

        while (idx < n)
        {
            var line = lines[idx];
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushPara();
                var lang = SanitiseLang(trimmed.Substring(3));
                idx++;
                var body = new StringBuilder();
                while (idx < n)
                {
                    if (lines[idx].TrimStart().StartsWith("```", StringComparison.Ordinal)) { idx++; break; }
                    if (body.Length > 0) body.Append('\n');
                    body.Append(lines[idx]);   // verbatim — Blazor escapes on render
                    idx++;
                }
                // Unclosed fence still emits a code block to end-of-message.
                blocks.Add(new Block(BlockKind.Code, Array.Empty<Segment>(), body.ToString(), lang));
                continue;
            }

            if (trimmed.StartsWith(">", StringComparison.Ordinal))
            {
                FlushPara();
                var q = new StringBuilder();
                while (idx < n)
                {
                    var t = lines[idx].TrimStart();
                    if (!t.StartsWith(">", StringComparison.Ordinal)) break;
                    var rest = t.Substring(1);
                    if (rest.StartsWith(" ", StringComparison.Ordinal)) rest = rest.Substring(1);
                    if (q.Length > 0) q.Append('\n');
                    q.Append(rest);
                    idx++;
                }
                blocks.Add(new Block(BlockKind.Quote, ParseSegments(q.ToString()), "", ""));
                continue;
            }

            if (para.Length > 0) para.Append('\n');
            para.Append(line);
            idx++;
        }
        FlushPara();
        return blocks;
    }

    // Sanitize a fenced-code language label for DISPLAY ONLY (never a CSS selector).
    private static string SanitiseLang(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        raw = raw.Trim();
        if (raw.Length == 0) return "";
        if (raw.Length > 16) raw = raw.Substring(0, 16);
        foreach (var c in raw)
            if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '+' || c == '#' || c == '-' || c == '.'))
                return "";
        return raw.ToLowerInvariant();
    }

    private static void ParseInline(string s, bool bold, bool italic, bool strike, bool underline, List<Segment> segs)
    {
        int i = 0, runStart = 0;
        while (i < s.Length)
        {
            char c = s[i];

            // Spoiler — ||hidden||. Literal-delimited like code; self-contained
            // (inner kept as plain text, no nested formatting) so the renderer
            // emits one flat <span>. '|' is printable ASCII so it can't collide
            // with the U+001E field separator. Unmatched '||' falls through.
            if (c == '|' && i + 1 < s.Length && s[i + 1] == '|')
            {
                int sclose = s.IndexOf("||", i + 2, StringComparison.Ordinal);
                if (sclose >= 0)
                {
                    LeafRun(s, runStart, i, bold, italic, strike, underline, segs);
                    segs.Add(new Segment(SegKind.Spoiler, s.Substring(i + 2, sclose - (i + 2)), bold, italic, strike, underline));
                    i = sclose + 2; runStart = i; continue;
                }
            }

            // Inline code — literal up to the next backtick, no nesting.
            if (c == '`')
            {
                int close = s.IndexOf('`', i + 1);
                if (close > i)
                {
                    LeafRun(s, runStart, i, bold, italic, strike, underline, segs);
                    segs.Add(new Segment(SegKind.Code, s.Substring(i + 1, close - i - 1)));
                    i = close + 1; runStart = i; continue;
                }
            }

            // URLs handled at this level (before delimiter splitting) so a
            // link containing * or __ isn't torn apart by Markdown markers.
            if ((c == 'h' || c == 'H') && IsUrlStart(s, i))
            {
                int j = i;
                while (j < s.Length && !IsUrlStop(s[j])) j++;
                while (j > i && IsTrailingPunct(s[j - 1])) j--;
                // Require at least one host char past the scheme, symmetric
                // for http:// (7) and https:// (8).
                int schemeLen = MatchCI(s, i, "https://") ? 8 : 7;
                if (j - i > schemeLen)
                {
                    LeafRun(s, runStart, i, bold, italic, strike, underline, segs);
                    segs.Add(new Segment(SegKind.Link, s.Substring(i, j - i), bold, italic, strike, underline));
                    i = j; runStart = i; continue;
                }
            }

            // Style delimiters — only when a matching close exists.
            var delim = DelimAt(s, i);
            if (delim is not null)
            {
                int close = s.IndexOf(delim, i + delim.Length, StringComparison.Ordinal);
                if (close >= 0)
                {
                    LeafRun(s, runStart, i, bold, italic, strike, underline, segs);
                    var inner = s.Substring(i + delim.Length, close - (i + delim.Length));
                    ParseInline(inner,
                        bold      || delim == "**" || delim == "***",
                        italic    || delim == "*"  || delim == "***",
                        strike    || delim == "~~",
                        underline || delim == "__",
                        segs);
                    i = close + delim.Length; runStart = i; continue;
                }
            }
            i++;
        }
        LeafRun(s, runStart, s.Length, bold, italic, strike, underline, segs);
    }

    private static string? DelimAt(string s, int i)
    {
        char c = s[i];
        char n = i + 1 < s.Length ? s[i + 1] : '\0';
        char n2 = i + 2 < s.Length ? s[i + 2] : '\0';
        if (c == '*' && n == '*' && n2 == '*') return "***";
        if (c == '*' && n == '*') return "**";
        if (c == '~' && n == '~') return "~~";
        if (c == '_' && n == '_') return "__";
        if (c == '*') return "*";
        return null;
    }

    // Emit s[from..end) as Text + Mention runs, carrying the style flags.
    private static void LeafRun(string s, int from, int end, bool bold, bool italic, bool strike, bool underline, List<Segment> segs)
    {
        if (end <= from) return;
        int i = from, runStart = from;
        while (i < end)
        {
            if (s[i] == '@' && (i == 0 || !IsMentionWordChar(s[i - 1])))
            {
                int j = i + 1;
                while (j < end && IsMentionWordChar(s[j]) && (j - i - 1) < 24) j++;
                if (j > i + 1)
                {
                    if (i > runStart) segs.Add(new Segment(SegKind.Text, s.Substring(runStart, i - runStart), bold, italic, strike, underline));
                    segs.Add(new Segment(SegKind.Mention, s.Substring(i + 1, j - i - 1), bold, italic, strike, underline));
                    runStart = j; i = j; continue;
                }
            }
            // #hashtag — parallel to @mention. Tag chars are [A-Za-z0-9_] (NO
            // dash, so "road-map" -> tag "road" + literal "-map"); "C#" stays
            // literal because '#' is preceded by a word char (boundary guard).
            if (s[i] == '#' && (i == 0 || !IsMentionWordChar(s[i - 1])))
            {
                int j = i + 1;
                while (j < end && IsTagChar(s[j]) && (j - i - 1) < 24) j++;
                if (j > i + 1)
                {
                    if (i > runStart) segs.Add(new Segment(SegKind.Text, s.Substring(runStart, i - runStart), bold, italic, strike, underline));
                    segs.Add(new Segment(SegKind.Hashtag, s.Substring(i + 1, j - i - 1), bold, italic, strike, underline));
                    runStart = j; i = j; continue;
                }
            }
            i++;
        }
        if (runStart < end) segs.Add(new Segment(SegKind.Text, s.Substring(runStart, end - runStart), bold, italic, strike, underline));
    }

    private static bool IsMentionWordChar(char c)
        => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_';

    // Hashtag chars: letters/digits/underscore only — NO dash (mentions allow dash).
    private static bool IsTagChar(char c)
        => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';

    /// <summary>A pasted video URL detected for inline embedding. Src is the
    /// player URL (iframe) or the direct file URL; Kind is "iframe"|"file".
    /// The browser only loads it when the user clicks (lightbox), so no
    /// third-party request fires from the feed.</summary>
    public sealed record VideoEmbed(string Src, string Kind, string Provider);

    /// <summary>Detect YouTube / Vimeo / direct video URLs and map to an
    /// embeddable player URL. Hand-rolled (no Regex) to match the tokenizer
    /// style and keep NativeAOT small. Returns null for non-video links.</summary>
    public static VideoEmbed? TryVideoEmbed(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var u = url.Trim();
        var lower = u.ToLowerInvariant();

        // Direct video file (ignore query string when checking the extension).
        var path = lower; int qq = path.IndexOf('?'); if (qq >= 0) path = path.Substring(0, qq);
        if (path.EndsWith(".mp4") || path.EndsWith(".webm") || path.EndsWith(".ogv") || path.EndsWith(".mov"))
            return new VideoEmbed(u, "file", "Video");

        // YouTube: youtu.be/ID, youtube.com/watch?v=ID, /shorts/ID, /embed/ID
        var yt = ExtractAfter(lower, u, "youtu.be/");
        if (yt is null) yt = ExtractQueryParam(lower, u, "v=", "youtube.com/watch");
        if (yt is null) yt = ExtractAfter(lower, u, "youtube.com/shorts/");
        if (yt is null) yt = ExtractAfter(lower, u, "youtube.com/embed/");
        var ytId = SanitizeId(yt);
        if (ytId is not null) return new VideoEmbed("https://www.youtube-nocookie.com/embed/" + ytId, "iframe", "YouTube");

        // Vimeo: vimeo.com/NUMERICID
        var vm = ExtractAfter(lower, u, "vimeo.com/");
        var vmId = SanitizeId(vm);
        if (vmId is not null && IsAllDigits(vmId)) return new VideoEmbed("https://player.vimeo.com/video/" + vmId, "iframe", "Vimeo");

        return null;
    }

    // Returns the original-case substring of `orig` that follows `marker`
    // (matched case-insensitively in `lower`), up to the next /?&# or end.
    private static string? ExtractAfter(string lower, string orig, string marker)
    {
        int m = lower.IndexOf(marker, StringComparison.Ordinal);
        if (m < 0) return null;
        int start = m + marker.Length, j = start;
        while (j < orig.Length && orig[j] != '/' && orig[j] != '?' && orig[j] != '&' && orig[j] != '#') j++;
        return j > start ? orig.Substring(start, j - start) : null;
    }

    // Returns the value of `param` (e.g. "v=") but only if `gate` is in the URL.
    private static string? ExtractQueryParam(string lower, string orig, string param, string gate)
    {
        if (lower.IndexOf(gate, StringComparison.Ordinal) < 0) return null;
        int m = lower.IndexOf(param, StringComparison.Ordinal);
        if (m < 0) return null;
        int start = m + param.Length, j = start;
        while (j < orig.Length && orig[j] != '&' && orig[j] != '#') j++;
        return j > start ? orig.Substring(start, j - start) : null;
    }

    private static string? SanitizeId(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length > 20) return null;
        foreach (var c in raw)
            if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_'))
                return null;
        return raw;
    }
    private static bool IsAllDigits(string s) { foreach (var c in s) if (c < '0' || c > '9') return false; return s.Length > 0; }

    private static bool IsUrlStart(string s, int i)
        => MatchCI(s, i, "http://") || MatchCI(s, i, "https://");

    private static bool MatchCI(string s, int i, string lit)
    {
        if (i + lit.Length > s.Length) return false;
        for (int k = 0; k < lit.Length; k++)
        {
            char a = s[i + k];
            if (a >= 'A' && a <= 'Z') a = (char)(a + 32);
            if (a != lit[k]) return false;
        }
        return true;
    }

    private static bool IsUrlStop(char c)
        => c == ' ' || c == '\n' || c == '\t' || c == '\r' || c == '<' || c == '>' || c == '"' || c == '`' || c == '|' || c == '*' || c == '~';

    private static bool IsTrailingPunct(char c)
        => c == '.' || c == ',' || c == '!' || c == '?' || c == ')' || c == ']' || c == ';' || c == ':';

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
            // 6-field format separated by U+001E (RECORD SEPARATOR):
            // username, text, replyToId, imageId, editedAtMs, deletedAtMs.
            // U+001E can't appear in user input (SanitiseText/SanitiseUsername
            // strip it), so unlike `|` it needs no escape handling. New
            // fields are appended only; ReadMessages defaults missing
            // trailing fields to 0 and still parses the legacy `|` split,
            // so older blobs round-trip without a version bump.
            buf.WriteString(
                m.Username + "" + m.Text + "" +
                m.ReplyToId + "" + m.ImageId + "" +
                m.EditedAtMs + "" + m.DeletedAtMs
                + ((char)0x1E) + m.AuthorPrincipal);   // 7th appended field (M2-B author)
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
            string username; string text; long replyToId = 0; long imageId = 0; long editedAtMs = 0; long deletedAtMs = 0; string authorPrincipal = "";
            // New format: U+001E-separated, up to 6 fields (trailing
            // editedAtMs/deletedAtMs default to 0 for older 4-field blobs).
            if (s.IndexOf('\u001E') >= 0)
            {
                var parts = s.Split('\u001E', 7);
                username = parts[0];
                text     = parts.Length > 1 ? parts[1] : "";
                if (parts.Length > 2) long.TryParse(parts[2], out replyToId);
                if (parts.Length > 3) long.TryParse(parts[3], out imageId);
                if (parts.Length > 4) long.TryParse(parts[4], out editedAtMs);
                if (parts.Length > 5) long.TryParse(parts[5], out deletedAtMs);
                if (parts.Length > 6) authorPrincipal = parts[6];   // "" for legacy 6-field blobs
            }
            else
            {
                // Legacy format: "username|text" (text may contain |).
                var sep = s.IndexOf('|');
                username = sep > 0 ? s.Substring(0, sep) : "Anonymous";
                text = sep > 0 ? s.Substring(sep + 1) : s;
            }
            msgs.Add(new Message(id, roomId, atMs, username, text, replyToId, imageId, editedAtMs, deletedAtMs, authorPrincipal));
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

    /// <summary>Normalise message body before persisting: cap at 280
    /// chars and strip the U+001E record separator (and the legacy U+001F)
    /// so a hostile client can't inject field delimiters that would
    /// corrupt the widened message record on read-back. Newlines/tabs are
    /// kept (Shift+Enter multiline is intentional).</summary>
    private static string SanitiseText(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        if (raw.IndexOf('') >= 0 || raw.IndexOf('') >= 0)
            raw = raw.Replace('', ' ').Replace('', ' ');
        if (raw.Length > 280)
        {
            // Don't slice through a surrogate pair (e.g. an emoji as the
            // 280th char) — that would leave a lone surrogate that UTF-8
            // mangles to U+FFFD on persist.
            int cut = char.IsHighSurrogate(raw[279]) ? 279 : 280;
            raw = raw.Substring(0, cut);
        }
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
