using System;
using System.Collections.Generic;
using System.Linq;
using Wasp.IcCdk;

namespace WaspSample.BlazorWasp;

/// <summary>
/// "phpMyAdmin for stable memory." Every service that owns a region of
/// stable memory registers an <see cref="IStableCollection"/> here.
/// The /stable Blazor page walks the registry to render Server →
/// Collections → Keys → Value tree.
///
/// Why a registry instead of reflection: stable memory is opaque bytes.
/// The canister knows the schema (each service has its own bespoke
/// binary layout) — there is no way to discover types from outside.
/// So introspection is opt-in: a service exposes whatever public
/// view of its data makes sense, and the explorer just renders it.
/// </summary>
public static class StableExplorer
{
    private static readonly List<IStableCollection> _collections = new();
    private static readonly object _lock = new();

    public static void Register(IStableCollection collection)
    {
        if (collection is null) return;
        lock (_lock)
        {
            // Idempotent — Program.Init runs once but be defensive.
            if (_collections.Any(c => c.Name == collection.Name)) return;
            _collections.Add(collection);
        }
    }

    public static IReadOnlyList<IStableCollection> All
    {
        get { lock (_lock) return _collections.ToArray(); }
    }

    public static IStableCollection? Find(string name)
        => All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    /// <summary>Total stable-memory pages allocated by this canister.
    /// Each page is 64 KiB.</summary>
    public static ulong StablePages => Ic0.stable64_size();

    public static ulong StableBytes => StablePages * 65536UL;
}

/// <summary>
/// A logical collection of items stored in stable memory — e.g. all
/// chat rooms, all chat messages, all uploaded images. Implementations
/// are thin views over existing services; they MUST be cheap to call
/// (the explorer page calls Count + ListKeys on every render).
/// </summary>
public interface IStableCollection
{
    /// <summary>Unique stable id used in URLs. Conventionally
    /// <c>service.collection</c>, e.g. "chat.messages".</summary>
    string Name { get; }

    /// <summary>Human-readable display name, e.g. "Chat messages".</summary>
    string DisplayName { get; }

    /// <summary>One-line description shown under the tree node.</summary>
    string Description { get; }

    /// <summary>Approximate stable-memory offset for the start of this
    /// collection's region. Display-only; not used for reads.</summary>
    ulong StableOffset { get; }

    int Count { get; }

    /// <summary>Return at most <paramref name="take"/> keys starting
    /// from <paramref name="skip"/>. Order is up to the implementation
    /// but should be deterministic so pagination is stable.</summary>
    IReadOnlyList<StableKey> ListKeys(int skip, int take);

    /// <summary>Resolve a key (as returned by ListKeys) to its fields.
    /// Return null if the key no longer exists.</summary>
    StableValue? GetValue(string keyId);

    /// <summary>Fields the explorer UI should render as editable
    /// inputs. Empty list = read-only collection. Names must match
    /// what GetValue returns so the UI can pre-fill values.</summary>
    IReadOnlyList<string> EditableFields { get; }

    /// <summary>Apply a patch. Patch keys are field names, values are
    /// the new (string) form to parse. Implementations validate and
    /// either persist + return null, or return an error message.</summary>
    string? TryEdit(string keyId, IReadOnlyDictionary<string, string> patch);
}

/// <summary>Display row in the tree under a collection.</summary>
/// <param name="Id">Opaque id, used in URL + passed to GetValue.</param>
/// <param name="Display">Short label, e.g. "#1 general" or "msg 247".</param>
/// <param name="Summary">Optional second line (timestamp, length, etc).</param>
public sealed record StableKey(string Id, string Display, string? Summary);

/// <summary>Rendered detail pane: an ordered list of name/value pairs.</summary>
public sealed record StableValue(string KeyId, IReadOnlyList<StableField> Fields);

public sealed record StableField(string Name, string Value);

// ─── Adapters over existing services ─────────────────────────────────
//
// One file rather than per-service classes — keeps the adapters next to
// the interface so the contract is obvious, and lets the domain
// services stay focused on their own concern.

internal sealed class CounterCollection : IStableCollection
{
    private readonly CounterService _svc;
    public CounterCollection(CounterService svc) { _svc = svc; }

    public string Name => "counter.value";
    public string DisplayName => "Counter";
    public string Description => "Single 4-byte int32 counter cell";
    public ulong StableOffset => 0;
    public int Count => 1;

    public IReadOnlyList<StableKey> ListKeys(int skip, int take)
        => skip > 0
            ? Array.Empty<StableKey>()
            : new[] { new StableKey("value", "value", $"{_svc.Count}") };

    public StableValue? GetValue(string keyId)
        => keyId == "value"
            ? new StableValue(keyId, new[]
              {
                  new StableField("type", "int32"),
                  new StableField("offset", "0"),
                  new StableField("size", "4 bytes"),
                  new StableField("value", _svc.Count.ToString()),
              })
            : null;

    public IReadOnlyList<string> EditableFields => new[] { "value" };

    public string? TryEdit(string keyId, IReadOnlyDictionary<string, string> patch)
    {
        if (keyId != "value") return "unknown key";
        if (!patch.TryGetValue("value", out var raw)) return "missing 'value' field";
        if (!int.TryParse(raw, out var n)) return "value must be a 32-bit signed integer";
        _svc.SetExact(n);
        return null;
    }
}

internal sealed class ChatRoomsCollection : IStableCollection
{
    private readonly ChatService _svc;
    public ChatRoomsCollection(ChatService svc) { _svc = svc; }

    public string Name => "chat.rooms";
    public string DisplayName => "Chat rooms";
    public string Description => "Channels at offset 4096, after CHAT magic+version+roomCount";
    public ulong StableOffset => 4096 + 12;
    public int Count => _svc.Rooms.Count;

    public IReadOnlyList<StableKey> ListKeys(int skip, int take)
        => _svc.Rooms.Skip(skip).Take(take)
              .Select(r => new StableKey(
                  r.Id.ToString(),
                  $"#{r.Id} {r.Name}",
                  FormatMs(r.CreatedAtMs)))
              .ToArray();

    public StableValue? GetValue(string keyId)
    {
        if (!int.TryParse(keyId, out var id)) return null;
        var room = _svc.FindRoom(id);
        if (room is null) return null;
        return new StableValue(keyId, new[]
        {
            new StableField("Id", room.Id.ToString()),
            new StableField("Name", room.Name),
            new StableField("CreatedAtMs", room.CreatedAtMs.ToString()),
            new StableField("CreatedAt", FormatMs(room.CreatedAtMs)),
            new StableField("MessageCount", _svc.MessagesIn(room.Id).Count.ToString()),
        });
    }

    public IReadOnlyList<string> EditableFields => Array.Empty<string>();
    public string? TryEdit(string keyId, IReadOnlyDictionary<string, string> patch) => "rooms are read-only";

    internal static string FormatMs(long ms)
        => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
}

internal sealed class ChatMessagesCollection : IStableCollection
{
    private readonly ChatService _svc;
    private readonly ServerService _servers;
    private readonly MembershipService _membership;
    public ChatMessagesCollection(ChatService svc, ServerService servers, MembershipService membership)
    { _svc = svc; _servers = servers; _membership = membership; }

    // B4: the /stable page renders on the ANONYMOUS query channel, so a private
    // server's messages must NOT be exposed here (no caller to authorize against).
    private bool IsPrivateRoom(int roomId) => _membership.IsPrivate(_servers.ServerOfChannel(roomId));

    public string Name => "chat.messages";
    public string DisplayName => "Chat messages";
    public string Description => "FIFO-capped at 500; written after the rooms section (private-server messages excluded)";
    public ulong StableOffset => 0;   // dynamic — depends on room count

    public int Count => _svc.AllMessages().Count(m => !IsPrivateRoom(m.RoomId));

    public IReadOnlyList<StableKey> ListKeys(int skip, int take)
    {
        var all = _svc.AllMessages();
        return all.Where(m => !IsPrivateRoom(m.RoomId)).Skip(skip).Take(take)
                  .Select(m => new StableKey(
                      m.Id.ToString(),
                      $"msg #{m.Id}",
                      $"r{m.RoomId} · {m.Username} · {Truncate(m.Text, 32)}"))
                  .ToArray();
    }

    public StableValue? GetValue(string keyId)
    {
        if (!long.TryParse(keyId, out var id)) return null;
        var m = _svc.FindMessage(id);
        if (m is null) return null;
        if (IsPrivateRoom(m.RoomId)) return null;   // B4: never expose private-server content
        var reacts = _svc.ReactionsOf(m.Id);
        return new StableValue(keyId, new[]
        {
            new StableField("Id", m.Id.ToString()),
            new StableField("RoomId", m.RoomId.ToString()),
            new StableField("AtMs", m.AtMs.ToString()),
            new StableField("At", ChatRoomsCollection.FormatMs(m.AtMs)),
            new StableField("Username", m.Username),
            new StableField("Text", m.Text),
            new StableField("ReplyToId", m.ReplyToId == 0 ? "(none)" : m.ReplyToId.ToString()),
            new StableField("ImageId", m.ImageId == 0 ? "(none)" : m.ImageId.ToString()),
            new StableField("Reactions",
                reacts.Count == 0 ? "(none)"
                : string.Join("  ", reacts.Select(kv => $"{kv.Key}×{kv.Value}"))),
        });
    }

    private static string Truncate(string s, int n)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n - 1) + "…");

    public IReadOnlyList<string> EditableFields => Array.Empty<string>();
    public string? TryEdit(string keyId, IReadOnlyDictionary<string, string> patch) => "messages are read-only";
}

internal sealed class ImageStoreCollection : IStableCollection
{
    private readonly ImageStore _svc;
    public ImageStoreCollection(ImageStore svc) { _svc = svc; }

    public string Name => "images.blobs";
    public string DisplayName => "Image blobs";
    public string Description => "Append-only at offset 1_000_000 (IMGS magic)";
    public ulong StableOffset => 1_000_000;
    public int Count => _svc.Count;

    public IReadOnlyList<StableKey> ListKeys(int skip, int take)
        => _svc.Ids.Skip(skip).Take(take)
              .Select(id =>
              {
                  var meta = _svc.MetaOf(id);
                  return new StableKey(
                      id.ToString(),
                      $"img #{id}",
                      meta is null ? null : $"{meta.Value.contentType} · {meta.Value.dataLen} B");
              })
              .ToArray();

    public StableValue? GetValue(string keyId)
    {
        if (!long.TryParse(keyId, out var id)) return null;
        var meta = _svc.MetaOf(id);
        if (meta is null) return null;
        return new StableValue(keyId, new[]
        {
            new StableField("Id", id.ToString()),
            new StableField("ContentType", meta.Value.contentType),
            new StableField("DataLength", meta.Value.dataLen + " bytes"),
            new StableField("StableOffset", meta.Value.offset.ToString()),
            new StableField("Preview", $"<img src=\"/_chat/img?id={id}\" />"),
        });
    }

    public IReadOnlyList<string> EditableFields => Array.Empty<string>();
    public string? TryEdit(string keyId, IReadOnlyDictionary<string, string> patch) => "image blobs are read-only";
}
