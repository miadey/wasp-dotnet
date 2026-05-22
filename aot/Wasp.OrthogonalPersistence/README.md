# Wasp.OrthogonalPersistence

EF-Core-style `DbContext` + `DbSet<T>` API for IC canisters. State
lives in stable memory; the developer writes plain C# without a SQL
backend.

```csharp
public sealed class TodoContext : WaspDbContext
{
    public WaspDbSet<Todo> Todos { get; }

    public TodoContext(JsonSerializerOptions json) : base(json)
    {
        Todos = new WaspDbSet<Todo>(this, "todos");
    }
}

// At canister init
var ctx = new TodoContext(jsonOptions);
ctx.Load();           // hydrate from previous canister runs

// In a request handler
ctx.Todos.Add(new Todo { Id = 1, Title = "Hello" });
ctx.SaveChanges();    // commits + snapshots to stable memory

// LINQ works on DbSet<T> via IEnumerable<T>
var pending = ctx.Todos.Where(t => !t.Done).ToList();
```

## Why not real EF Core

We tried. `Microsoft.EntityFrameworkCore` + `EntityFrameworkCore.InMemory`
pulled the AOT-trimmed wasm to ~13.8 MiB of code section — past the
IC's runtime ceiling on how big a Wasm module's code section can be.

The relevant limit is **the code section**, not the heap. Three
separate limits, often confused:

| Resource | Limit | What it is |
|---|---|---|
| Code section | **12 MiB** | the compiled function bytecodes |
| Linear memory (heap) | 4 GiB (wasm32), 6 GiB (wasm64) | runtime allocations |
| Stable memory | 500 GiB | persisted across calls |

The 4 GiB / 6 GiB you might see in IC docs is the **heap** — bytes the
canister allocates while executing — not the size of the compiled
code. The code section has its own much smaller cap.

The **12 MiB** number above is from the current `dfinity/ic`
HEAD constant
[`MAX_CODE_SECTION_SIZE_IN_BYTES`](https://github.com/dfinity/ic/blob/master/rs/embedders/src/wasm_utils/validation.rs)
in `rs/embedders/src/wasm_utils/validation.rs`:

```rust
pub const MAX_CODE_SECTION_SIZE_IN_BYTES: u32 = 12 * 1024 * 1024;
```

Older replicas enforce smaller values — my dfx 0.28 reported `11534336`
(11 MiB), and `dfinity/portal` docs still reference the previous
10 MiB. The number creeps up over time; the architectural reason is
the IC's replicated execution model — every replica has to recompile
the module on install, so its size compounds linearly with subnet
replication factor.

In all three cases (10, 11, or 12 MiB), the EF Core wasm at 13.8 MiB
is over the ceiling. Reaching it would need either substantial
custom trimmer descriptors against EF Core's reflection paths, or
EF Core's own published AOT-trimmed package (not yet shipped). We
shipped the lightweight `WaspDbContext` instead so something works
today.

## Storage layout

`Storage.cs` writes a single chunk at a developer-chosen offset
(default `65536`):

```
offset + 0      : "WAOP" magic (4 bytes)
offset + 4      : version (u32, 1)
offset + 8      : payload byte length (u32)
offset + 12     : JSON-encoded { "setName": [...entities...], ... }
```

Pages are added in 64 KB increments as the snapshot grows. The
serialiser uses System.Text.Json with a source-gen
`JsonSerializerContext` resolver passed by the developer (required
for AOT trimming).

Re-installing the canister with `--mode upgrade` preserves stable
memory, so the snapshot survives. `--mode reinstall` clears it.

## Limits

- **Snapshot size**: 64 MiB cap (sanity check; bump in `Storage.cs`
  if you genuinely need more).
- **Schema migrations**: not handled. If you change a field's name or
  type, decode of older payloads will silently drop the field.
  Strategy is the same as any other on-chain canister upgrade: stage
  the change.
- **Entity equality**: by reference. Use `Find(predicate)` or
  `Where(predicate)` for id-based lookups.

## See also

- `samples/TodoEf/` — minimal-API todo backend exercising the full
  CRUD round trip and surviving a `dfx canister install --mode upgrade`.
