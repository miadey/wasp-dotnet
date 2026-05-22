using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Wasp.OrthogonalPersistence;

/// <summary>
/// EF-Core-style "DbContext" backed by canister stable memory. The
/// developer subclasses this, declares <see cref="WaspDbSet{T}"/>
/// properties, and gets persistence "for free" — every
/// <see cref="SaveChanges"/> snapshots every set to a single chunk of
/// stable memory.
///
/// Mental model: orthogonal persistence. State that lives in the
/// context's sets survives canister upgrades, redeploys, hours of
/// idle time, replica restarts. No SQL, no migrations, no DBAs.
///
/// Trade-off: every SaveChanges rewrites the whole snapshot. Fine for
/// dapps up to roughly a few MB of total state — past that, switch to
/// chunked / shard / dedicated stable-memory layouts.
/// </summary>
public abstract class WaspDbContext
{
    private readonly List<IWaspDbSetInternal> _sets = new();
    private readonly JsonSerializerOptions _json;
    private readonly ulong _stableOffset;

    /// <summary>
    /// Create the context. <paramref name="jsonOptions"/> MUST carry a
    /// source-gen <c>JsonSerializerContext</c> resolver covering every
    /// entity type in this context, otherwise serialisation will fail
    /// under AOT trimming.
    /// </summary>
    protected WaspDbContext(JsonSerializerOptions jsonOptions, ulong stableOffset = 65536)
    {
        _json = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
        _stableOffset = stableOffset;
    }

    /// <summary>
    /// Called by <see cref="WaspDbSet{T}"/>'s constructor. Don't call
    /// directly — declare sets as `public WaspDbSet&lt;T&gt; Xs { get; }`
    /// and instantiate them in the constructor.
    /// </summary>
    internal void Register(IWaspDbSetInternal set) => _sets.Add(set);

    public int SaveChanges()
    {
        var snapshot = new Dictionary<string, object>(_sets.Count);
        foreach (var set in _sets) snapshot[set.Name] = set.SnapshotForSerialize();
        var json = JsonSerializer.Serialize(snapshot, typeof(Dictionary<string, object>), _json);
        Storage.Write(_stableOffset, json);
        return _sets.Count;
    }

    public void Load()
    {
        var json = Storage.Read(_stableOffset);
        if (json is null) return;
        // Parse to a Dictionary<string, JsonElement>. The element-array
        // for each set is then deserialised by the set itself, which
        // knows its T.
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var set in _sets)
        {
            if (!root.TryGetProperty(set.Name, out var element)) continue;
            set.LoadFromJson(element, _json);
        }
    }
}

internal interface IWaspDbSetInternal
{
    string Name { get; }
    object SnapshotForSerialize();
    void LoadFromJson(JsonElement element, JsonSerializerOptions options);
}
