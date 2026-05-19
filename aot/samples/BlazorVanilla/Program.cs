using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Wasp.AspNetCore;
using Wasp.AspNetCore.Blazor.Server;
using Wasp.IcCdk;
using WaspSample.BlazorVanilla.Components;
using WaspSample.BlazorVanilla.Components.Pages;

// BlazorVanilla — stock-template Blazor Server canister exercising more
// of the dotnet new blazor surface (#90). Uses the M4.S9 one-line API.

namespace WaspSample.BlazorVanilla;

public static class Program
{
    // AOT trim-dependency pins for components reached only via reflection
    // (Router → RouteView → page component types). Same pattern as
    // CircuitOnIc.
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(App))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Routes))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Components.Layout.MainLayout))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Components.Layout.NavMenu))]
    // LayoutComponentBase exposes ChildContent via inheritance; All on
    // MainLayout only preserves declared members. Without this root the
    // framework's ParameterView setter throws "no property matching
    // ChildContent" at render time.
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Microsoft.AspNetCore.Components.LayoutComponentBase))]
    // #82: NavLink (and its base CascadingValue resolution path) is
    // used in NavMenu. Pin so the trimmer keeps the active-class
    // diffing logic + Match enum.
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Microsoft.AspNetCore.Components.Routing.NavLink))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Home))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Counter))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Weather))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Wasp.AspNetCore.Blazor.Server.WaspNavLink))]
    [DynamicDependency("FromMilliseconds(System.Int64)", typeof(System.TimeSpan))]
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
                ApplicationName = "BlazorVanilla",
            });

            // ─── M4.S9 one-line API ────────────────────────────────
            // Interactive Blazor Server: AddInteractiveServerComponents
            // + IcCircuitTransportRegistry + /_blazor Long Polling
            // endpoint chain wire up. Counter's @onclick handler runs
            // on the canister and ships a render diff back to the
            // browser via blazor.web.js.
            builder.UseInternetComputer<App>();

            var app = builder.Build();

            // Fast-click query endpoint — GET /api/click?c=N → {"count": N+1}.
            // Runs as an IC query call (no consensus, ~50 ms RTT) so it
            // gives near-instant feedback while the SignalR Long Polling
            // handshake for the slow-click path is still warming up.
            // No persistence — the JS client keeps the running count.
            // Writes the response directly to HttpResponse to avoid the
            // RequestDelegateGenerator's JsonTypeInfo source-gen path,
            // which fails under reflection-disabled JSON.
            app.MapGet("/api/click", async (Microsoft.AspNetCore.Http.HttpContext ctx) =>
            {
                int c = 0;
                if (ctx.Request.Query.TryGetValue("c", out var raw)
                    && int.TryParse(raw.ToString(), out var parsed))
                {
                    c = parsed;
                }
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsync("{\"count\":" + (c + 1) + "}");
            });

            // Weather "file in stable memory" — GET /api/weather dumps the
            // raw CSV; POST /api/weather/seed writes the default 5 rows.
            // GET is a query call (~50 ms RTT, reads stable memory).
            // POST is an update call (~2–4 s, the IC gateway upgrades all
            // POSTs to http_request_update), which is what lets the write
            // actually persist across messages and canister upgrades.
            app.MapGet("/api/weather", async (Microsoft.AspNetCore.Http.HttpContext ctx) =>
            {
                var csv = WeatherStableStore.ReadCsvOrEmpty();
                ctx.Response.ContentType = "text/csv; charset=utf-8";
                await ctx.Response.WriteAsync(csv);
            });

            app.MapPost("/api/weather/seed", async (Microsoft.AspNetCore.Http.HttpContext ctx) =>
            {
                var csv = WeatherStableStore.DefaultCsv();
                WeatherStableStore.WriteCsv(csv);
                // Refresh the pre-rendered /weather cache so the next GET
                // serves HTML that reflects the just-persisted bytes.
                IcServer.RegisterRenderedPath("/weather");
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsync(
                    "{\"seeded\":true,\"bytes\":" + System.Text.Encoding.UTF8.GetByteCount(csv) + "}");
            });

            // App-side mirror — auto-detects the BlazorOnIcMarker via DI
            // and wires Long-Polling / JS bridge / static assets.
            app.UseInternetComputer();

            // Canister-friendly app.Run() — StartAsync().GetAwaiter()
            // .GetResult() without disposing the host.
            app.RunOnIC();

            // Pre-render every page so each route serves from the
            // ~5-50 ms query-path static-asset cache instead of running
            // the full ASP.NET render pipeline through update consensus
            // on each request. Navigation between pages then feels
            // instant. The Counter page's WaspMarker stays embedded in
            // the cached HTML — blazor.web.js still attaches its circuit
            // on first Counter load.
            IcServer.RegisterRenderedPath("/");
            IcServer.RegisterRenderedPath("/counter");
            IcServer.RegisterRenderedPath("/weather");
        }
        catch (System.Exception ex)
        {
            var msg = ex.GetType().FullName + ": " + ex.Message
                + (ex.StackTrace is { } st ? "\n" + st : "");
            IcServer.InitFailureMessage = msg;
            Reply.Print("[init-fail] " + msg);
        }
    }
}
