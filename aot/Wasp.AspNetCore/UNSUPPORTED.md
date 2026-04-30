# Wasp.AspNetCore — banned / shimmed APIs on canister

ICP canisters have no real filesystem, no threading, no sockets, and a hard
instruction budget per message. The ASP.NET Core framework assumes all four.
This document records every framework API that's either trapped, stubbed, or
substituted to make `Microsoft.AspNetCore.App` AOT-compile and run inside a
canister.

If a route or middleware in your canister hits one of the banned paths, it
will trap with a clear message. If a previously-working canister starts
trapping after a `dotnet` upgrade, this is the first place to look.

---

## Trim-time substitutions (`ILLink.Substitutions.xml`)

Applied via `--substitution=…` passed to `ilc`. See `Wasp.AspNetCore.targets`.

| Method | Action | Why |
|---|---|---|
| `System.IO.Directory.Exists(string)` | Returns `true` | `PhysicalFileProvider.ctor` calls this; canister has no filesystem so the real implementation falls into wasi `stat` → `proc_exit` → trap. |
| `Microsoft.Extensions.FileProviders.PhysicalFileProvider..ctor(string)` | No-op | Even with `Directory.Exists` substituted, the ctor calls `Path.GetFullPath` and registers a watcher — both fail on canister. The provider is constructed but never read in the M0 hello sample. **M2 (#52) replaces it with an embedded-resource provider for real Razor SSR work.** |
| `Microsoft.Extensions.FileProviders.PhysicalFileProvider..ctor(string, ExclusionFilters)` | No-op | Same as above (the no-arg overload delegates here). |

---

## WASI imports stubbed by `wasi-stub`

Applied to the canister `.wasm` after `dotnet publish`. See
`shared/tools/wasi-stub/src/main.rs`.

All imports from the `wasi_snapshot_preview1` (Preview 1) and `wasi:*` (Preview 2
component) modules are no-op'd by default. Two exceptions:

| Import | Treatment | Why |
|---|---|---|
| `wasi_snapshot_preview1::proc_exit` | **Trap** | C `[[noreturn]]`. Returning from a stub leaves callers (e.g. wasi-libc's `_Exit`) in undefined state and crashes deep in the call stack with an opaque `unreachable`. Trapping fails fast with a useful diagnostic. |
| `wasi_snapshot_preview1::fd_prestat_get` | Returns `EBADF` (errno 8) | Wasi-libc's preopen enumeration walks fds calling this until it gets `EBADF`. Returning `0` (success) makes it think every fd is a valid preopen and corrupts state. |

---

## Forbidden user-code APIs

These compile fine but trap at runtime when called inside a canister message,
because they require capabilities the canister doesn't have. The trap goes
through `IcSyncContext.RunUntilComplete` and surfaces as a 500 response.

| API | Treatment | Notes |
|---|---|---|
| `await Task.Delay(...)` | Trap | No real timer in mid-message. Canister has `ic0.global_timer_set` for inter-message timers (M4 work). |
| `Task.Run(...)` (with blocking work) | Trap | No thread pool. Continuations posted from outside our `IcSyncContext` never reach the drain queue. |
| `ThreadPool.QueueUserWorkItem(...)` | Trap | Same as above. |
| `Console.WriteLine`, `Console.Error.Write` | Silent no-op (writes to stubbed `fd_write`) | Use `Wasp.IcCdk.Reply.Print` or an `ILogger` wired to it. |
| `File.*`, `Directory.*` | Most trap or return false | The substituted `Directory.Exists` always returns `true`; everything else either traps or returns failure. |
| `HttpClient.*` from middleware | Trap (M3+) | Outcalls have a callback shape that doesn't fit a synchronous mid-pipeline await. Allowed only inside terminal endpoint handlers in M3 (#57). |
| `Environment.CurrentDirectory` | Returns `/` (wasi-libc default) | Don't rely on it. |
| `DateTime.UtcNow` | Works | `Wasp.IcCdk.Ic0.time()`-backed via the runtime's wasi `clock_time_get`, stubbed to return `0`. **Time is not advancing in queries; use `Ic0.time()` for real timestamps.** |

---

## ASP.NET Core defaults that don't work

`WebApplication.CreateBuilder()` and `WebApplication.CreateSlimBuilder()` both
load `appsettings.json` via `JsonConfigurationExtensions.AddJsonFile`, which
dereferences the (stubbed) `PhysicalFileProvider` and traps. **Use
`WebApplication.CreateEmptyBuilder` instead** and add only the services you
need:

```csharp
var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions {
    ApplicationName = "MyCanister",
    ContentRootPath = "/canister",
});
builder.Services.AddRoutingCore();
builder.WebHost.UseIcCanister();
var app = builder.Build();
app.MapGet("/", () => "hi");
app.StartAsync().GetAwaiter().GetResult();
```

`AddRoutingCore` is the slim variant of `AddRouting`; it pulls in endpoint
matching without the link-generation machinery.

---

## RDG body binding (typed parameter) — read manually instead

```csharp
// Don't:  AOT-trims JsonSerializer.DeserializeAsync<Note>(PipeReader, ...) →
//         EE_MissingMethod at runtime
app.MapPost("/note", (Note n) => $"got {n.Title}");

// Do:     manual read + deserialize via the typed JsonTypeInfo
app.MapPost("/note", async (HttpContext ctx) =>
{
    using var sr = new StreamReader(ctx.Request.Body);
    var json = await sr.ReadToEndAsync();
    var note = JsonSerializer.Deserialize(json, NoteJsonContext.Default.Note);
    return note is null ? Results.BadRequest() : Results.Text($"got {note.Title}");
});
```

The .NET 10 `RequestDelegateGenerator` emits body-binding code that calls
`JsonSerializer.DeserializeAsync<T>(PipeReader, JsonTypeInfo<T>, CancellationToken)`
through a `Func<>` indirection the AOT trimmer can't follow. The closed
generic instantiation gets trimmed and the canister returns 500 with
`EE_MissingMethod`. Reading the body and deserializing via the source-gen
typed `JsonTypeInfo` directly is AOT-clean and recommended.

Source-gen result writes (`Results.Json(value, JsonTypeInfo)`) and string
returns work as expected; only typed-parameter body binding is affected.

## JsonSerializerContext setup

`WebApplication.CreateEmptyBuilder` does not register `JsonOptions` with the
default reflection resolver, so source-gen contexts must be wired explicitly:

```csharp
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Note))]
[JsonSerializable(typeof(object))]  // RDG asks for typeof(object) at startup
internal partial class MyJsonContext : JsonSerializerContext { }

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolver = MyJsonContext.Default);
```

The `typeof(object)` entry is required: the RDG calls
`JsonSerializerOptions.GetTypeInfo(typeof(object))` once during
`MapPost(...)` setup, and a missing entry crashes the resolver.

## Verification

A canister that exercises a banned path returns:

```
HTTP/1.1 500 Internal Server Error
content-type: text/plain; charset=utf-8

Wasp.AspNetCore internal error: <exception message>
```

`IcServer.Dispatch` catches all managed exceptions inside the pipeline and
surfaces them as 500 responses (instead of trapping the canister). The full
exception trace is also printed to `dfx canister logs` via `Reply.Print`.
