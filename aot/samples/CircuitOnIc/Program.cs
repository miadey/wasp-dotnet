using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Wasp.AspNetCore;
using Wasp.AspNetCore.Blazor.Server;
using Wasp.IcCdk;
using WaspSample.CircuitOnIc.Components;
using WaspSample.CircuitOnIc.Components.Pages;

// Blazor Server on IC — sample canister.
//
// The Wasp setup surface is now exactly three lines (see Init below):
//   builder.AddBlazorOnIC();
//   app.MapBlazorOnIC<App>(typeof(Program).Assembly);
//   app.RegisterPreRenderedShell("/");
//
// Everything else (the per-app fast-click endpoints, stable-memory
// persistence) is application code, not framework glue.

namespace WaspSample.CircuitOnIc;

public static class Program
{
    // AOT trim-dependencies. The first four pin Blazor types reachable
    // only via reflection from the framework. The fifth keeps the
    // private `_context` field on RendererSynchronizationContextDispatcher
    // — CircuitHubFacade yanks it via reflection at event-dispatch
    // time so DispatchEventAsync runs synchronously on the renderer
    // dispatcher (no ThreadPool, which wasm32-wasi doesn't have).
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(App))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Routes))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Components.Layout.MainLayout))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Counter))]
    [DynamicDependency("FromMilliseconds(System.Int64)", typeof(TimeSpan))]
    [DynamicDependency(
        DynamicallyAccessedMemberTypes.NonPublicFields,
        "Microsoft.AspNetCore.Components.Rendering.RendererSynchronizationContextDispatcher",
        "Microsoft.AspNetCore.Components")]
    [ModuleInitializer]
    internal static void Init()
    {
        try
        {
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
            {
                ContentRootPath = "/canister",
                ApplicationName = "CircuitOnIc",
            });

            // ─── 1. Wasp setup (one line) ───────────────────────────
            builder.AddBlazorOnIC();

            var app = builder.Build();

            // ─── 2. Wasp endpoints + interactive root (one line) ────
            app.MapBlazorOnIC<App>(typeof(Program).Assembly);

            // ─── 3. App-specific endpoints — query-RPC + persistence ─
            //
            // GET /api/click?c=<N> → {"count": N+1}    (query, ~12 ms)
            // GET /api/state       → {"count": <persisted>}  (query)
            // POST /api/sync?c=<N> → persists N to stable memory (update)
            //
            // The query-call paths return without consensus, so each
            // click renders the new number in tens of milliseconds.
            // Persistence is batched: the client fires /api/sync every
            // few seconds in the background instead of on each click.

            // Update-call route (write to stable memory) must be
            // registered BEFORE StartAsync so routing picks it up.
            app.MapPost("/api/sync", async (HttpContext ctx) =>
            {
                int next = 0;
                if (ctx.Request.Query.TryGetValue("c", out var raw)
                    && int.TryParse(raw.ToString(), out var parsed))
                {
                    next = parsed;
                }
                CounterStableStore.Write(next);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(
                    "{\"persisted\":" + next.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");
            }).DisableAntiforgery();

            app.StartAsync().GetAwaiter().GetResult();

            // After StartAsync: pre-render the SSR shell into the
            // canister's static-asset cache so subsequent page loads
            // serve in ~5 ms instead of ~1.5 s.
            app.RegisterPreRenderedShell("/");

            // Query-only endpoints (read stable memory or compute
            // statelessly) — registered post-StartAsync because they
            // bypass the ASP.NET pipeline entirely.
            IcServer.RegisterQueryHandler("/api/click", url =>
            {
                int prev = 0;
                int q = url.IndexOf('?');
                if (q >= 0)
                {
                    foreach (var part in url.Substring(q + 1).Split('&'))
                    {
                        if (part.StartsWith("c=", StringComparison.Ordinal)
                            && int.TryParse(part.Substring(2), out var parsed))
                        {
                            prev = parsed;
                        }
                    }
                }
                var bytes = Encoding.UTF8.GetBytes(
                    "{\"count\":" + (prev + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");
                return (bytes, "application/json; charset=utf-8");
            });

            IcServer.RegisterQueryHandler("/api/state", url =>
            {
                int count = CounterStableStore.Read();
                var bytes = Encoding.UTF8.GetBytes(
                    "{\"count\":" + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");
                return (bytes, "application/json; charset=utf-8");
            });
        }
        catch (Exception ex)
        {
            var msg = ex.GetType().FullName + ": " + ex.Message
                + (ex.StackTrace is { } st ? "\n" + st : "");
            IcServer.InitFailureMessage = msg;
            Reply.Print("[init-fail] " + msg);
        }
    }
}
