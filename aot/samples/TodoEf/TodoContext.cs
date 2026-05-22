using System.Text.Json;
using Wasp.OrthogonalPersistence;

namespace WaspSample.TodoEf;

public class Todo
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public bool Done { get; set; }
    public long CreatedAtMs { get; set; }
}

/// <summary>
/// EF-Core-style DbContext on the IC. Same shape developers know
/// from Entity Framework, but the sets persist to stable memory
/// instead of SQL — no connection string, no migrations.
/// </summary>
public sealed class TodoContext : WaspDbContext
{
    public WaspDbSet<Todo> Todos { get; }

    private int _nextId = 1;

    public TodoContext(JsonSerializerOptions json) : base(json)
    {
        Todos = new WaspDbSet<Todo>(this, "todos");
    }

    public Todo Create(string title)
    {
        var todo = new Todo
        {
            Id = NextId(),
            Title = title,
            Done = false,
            CreatedAtMs = (long)(Wasp.IcCdk.Ic0.time() / 1_000_000UL),
        };
        Todos.Add(todo);
        SaveChanges();
        return todo;
    }

    private int NextId()
    {
        // First-write seed: walk existing rows so id space stays
        // monotonic across canister upgrades.
        if (_nextId == 1)
        {
            foreach (var t in Todos)
                if (t.Id >= _nextId) _nextId = t.Id + 1;
        }
        return _nextId++;
    }
}
