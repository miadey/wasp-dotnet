using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Wasp.OrthogonalPersistence;

/// <summary>
/// EF-Core-style collection of entities. Mirrors the
/// <c>DbSet&lt;T&gt;</c> surface developers already know:
/// <c>Add</c>, <c>Remove</c>, <c>Find</c>, plus
/// <c>IEnumerable&lt;T&gt;</c> for LINQ.
///
/// Entity equality is by reference; deletion uses the same instance
/// you added. To support id-based lookup, expose an integer
/// <c>Id</c> property on your entity and use <c>FirstOrDefault</c>.
/// </summary>
public sealed class WaspDbSet<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T> : IEnumerable<T>, IWaspDbSetInternal
    where T : class
{
    private readonly List<T> _items = new();
    private readonly string _name;

    public WaspDbSet(WaspDbContext parent, string name)
    {
        _name = name;
        parent.Register(this);
    }

    public int Count => _items.Count;

    public void Add(T item) => _items.Add(item ?? throw new ArgumentNullException(nameof(item)));

    public bool Remove(T item) => _items.Remove(item ?? throw new ArgumentNullException(nameof(item)));

    public T? Find(Predicate<T> match) => _items.Find(match);

    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // ─── Internal: snapshot + load ────────────────────────────────────
    string IWaspDbSetInternal.Name => _name;

    object IWaspDbSetInternal.SnapshotForSerialize() => _items.ToArray();

    void IWaspDbSetInternal.LoadFromJson(JsonElement element, JsonSerializerOptions options)
    {
        if (element.ValueKind != JsonValueKind.Array) return;
        _items.Clear();
        foreach (var child in element.EnumerateArray())
        {
            var raw = child.GetRawText();
            var entity = JsonSerializer.Deserialize<T>(raw, options);
            if (entity is not null) _items.Add(entity);
        }
    }
}
