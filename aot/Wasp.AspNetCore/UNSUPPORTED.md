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

## Authentication / Authorization

The full `AddAuthentication()` stack pulls in `Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager`, which has a parameterless `KeyManagementOptions..ctor()` that the `wasm32-wasi` trimmer breaks (`TimeSpan.FromMilliseconds(Int64)` gets removed). The canister traps at `Host.StartAsync` with:

```
System.Reflection.TargetInvocationException
   at Microsoft.Extensions.Options.OptionsFactory.Create(...)
   at Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager..ctor(...)
```

**Workaround that works today:**

1. Skip `AddAuthentication()`. Add a custom middleware that reads the cookie/header/principal source and sets `ctx.User = new ClaimsPrincipal(...)` directly.
2. Use `AddAuthorizationBuilder().SetDefaultPolicy(...).RequireAuthenticatedUser()` for the authz side — `AddAuthorizationCore` doesn't pull DataProtection.
3. Replace the default `IAuthorizationMiddlewareResultHandler` with one that writes 401/403 directly. The default tries `ctx.ChallengeAsync()` which needs `IAuthenticationService` (not registered).

```csharp
internal sealed class IcAuthMiddlewareResultHandler : IAuthorizationMiddlewareResultHandler
{
    public async Task HandleAsync(RequestDelegate next, HttpContext ctx,
        AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (result.Forbidden) { ctx.Response.StatusCode = 403; return; }
        if (result.Challenged) { ctx.Response.StatusCode = 401; return; }
        await next(ctx);
    }
}

builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, IcAuthMiddlewareResultHandler>();
```

The result: `[Authorize]` / `.RequireAuthorization()` works, `UseAuthorization()` middleware runs, but no DataProtection in the graph. See `samples/AspNetCoreApi/Program.cs` (#51) for the end-to-end pattern.

## IResult return shape — write to ctx.Response directly under heavy trimming

Some configurations of the .NET 10 `RequestDelegateGenerator` interceptor and the trimmer produce a build where the `IResult.ExecuteAsync` path is reachable but its terminal write to `ctx.Response.Body` is no-op'd (the canister returns the correct status code but `content-length: 0`). The repro is fragile — the same source, rebuilt with a tiny addition (e.g. one `Reply.Print` call), produces a working binary. When this bites, switch the handler from the `Delegate`-typed `MapPost("/x", (...) => Results.Text(...))` shape to the `RequestDelegate`-typed shape and write the response stream directly:

```csharp
RequestDelegate saveHandler = async ctx =>
{
    /* read body, deserialize, etc */
    ctx.Response.StatusCode = 200;
    ctx.Response.ContentType = "text/plain; charset=utf-8";
    await ctx.Response.WriteAsync($"saved '{note.Title}'");
};
api.MapPost("/save", saveHandler);
```

This bypasses RDG entirely; the AOT compiler treats it as a plain delegate call. AOT-stable across trim configurations.

## Razor SSR (M2 work-in-progress)

The full `Microsoft.AspNetCore.Components` stack — including `AddRazorComponents()`, the route discovery, the `EndpointHtmlRenderer`, and `HtmlRenderer` (from `Microsoft.AspNetCore.Components.Web`) — **AOT-compiles to wasm32-wasi** and runs inside the canister.  All the moving parts wire up: the framework's service graph resolves, the route table builds, components instantiate, headers come back correctly (e.g. `blazor-enhanced-nav: allow`).

What does **not** work today:

1. **`StaticHtmlRenderer.RenderCore` NREs at the HTML write phase.** The renderer instantiates the component and walks the render tree, then dies in `RenderCore(int, TextWriter, ArrayRange<RenderTreeFrame>, int)` with a bare `NullReferenceException` (no specific field — appears to be a static cache the trimmer dropped). Confirmed with both `app.MapRazorComponents<App>()` AND `HtmlRenderer.RenderComponentAsync<App>().ToHtmlString()`. Survives even with `[DynamicDependency(All, typeof(App))]` on the entry point.

2. **`EndpointResponseBufferingFeature` doesn't flush back through our `IHttpResponseBodyFeature`.** When `MapRazorComponents` is used, the framework's enhanced-nav streaming feature wraps our body feature with its own buffering layer, then on completion does not write the buffered bytes back to the outer feature. So even if (1) were fixed, the body would still arrive empty.

**Workaround in `samples/RazorOnIc`:** the sample's `/` endpoint hand-writes the HTML via `ctx.Response.WriteAsync(...)`. The `<App />` Razor component is shipped alongside as documentation of the intended pattern, and `/razor` is wired to render it via `HtmlRenderer` (returns 500 today). Form POST + `StableCell<int>` work end-to-end through the hand-written path: bumping the counter does POST → 303 → GET, and the count survives canister upgrade.

**Fix paths under investigation (M2 follow-up):**
- Aggressive `TrimmerRootDescriptor` over `Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure` to preserve the field metadata that's getting dropped.
- A hand-rolled minimal Razor renderer (`Wasp.AspNetCore.Razor.SsrLite`) that bypasses `StaticHtmlRenderer` and walks `RenderTreeFrame[]` directly.
- Replace `EndpointResponseBufferingFeature` with our own simpler feature via a `RazorComponentsServiceOptions` config or a custom middleware that strips the buffering.

## DataProtection — registration short-circuit

`Microsoft.AspNetCore.DataProtection.XmlKeyManager` doesn't AOT-compile cleanly to wasm32-wasi: `KeyManagementOptions..ctor()`'s field initializer references `TimeSpan.FromMilliseconds(long)` which the trimmer drops. `KeyManagementOptionsSetup.Configure` then NREs on the empty-options instance. Anything that calls `AddDataProtection()` transitively (any of `AddAuthentication`, `AddRazorComponents`, `AddAntiforgery`) dies at `Host.StartAsync`.

The library ships a fix:

1. **`Wasp.AspNetCore.UseIcDataProtection()`** — call BEFORE the first framework call that triggers data protection. Pre-registers `IDataProtectionProvider` and `IKeyManager` with no-op implementations. `AddDataProtection` then uses `TryAddSingleton` and our registrations win.

2. **ILLink substitutions** baked into the package:
   - `KeyManagementOptions..ctor()` → stubbed (empty body).
   - `KeyManagementOptionsSetup.Configure` → stubbed (no-op).
   - `AntiforgeryOptionsSetup.Configure` → stubbed (calls `SHA256.HashData` to derive a cookie name, not supported on wasm). Consumer must `services.Configure<AntiforgeryOptions>(o => o.Cookie.Name = "...")` instead.
   - `PersistentServicesRegistry.RegisterForPersistence` / `RestoreStateAsync` → stubbed (hashes types via `SHA256.HashData` — not needed for SSR-only).

These are loaded automatically by every project that imports `Wasp.AspNetCore.targets`.

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
