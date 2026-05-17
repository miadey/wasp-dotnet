using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Wasp.AspNetCore;
using Wasp.AspNetCore.Blazor.Server;
using Wasp.IcCdk;
using WaspSample.CircuitOnIc.Components;
using WaspSample.CircuitOnIc.Components.Pages;

// M4.S7 (issue #60): Counter.razor with @rendermode InteractiveServer
// running on a canister. Companion to RazorOnIc/ — that sample renders
// statically (no live circuit); this one renders interactively.
//
// Transport: SignalR LONG POLLING over the IC HTTP gateway. No external
// ic-websocket-gateway, no Docker at runtime — every interaction is a
// plain HTTP request that the IC boundary node (pocket-ic locally,
// boundary nodes on mainnet) dispatches to http_request_update on the
// canister, which routes via ASP.NET Core middleware to the
// /_blazor/{negotiate,send,poll} endpoints registered by
// app.MapWaspBlazorLongPolling(...).
//
// Build/deploy:
//   1. Generate the vendored CircuitHost weaver output (once):
//        dotnet run --project shared/tools/Wasp.CircuitHostWeaver -- \
//          /usr/local/share/dotnet/shared/Microsoft.AspNetCore.App/10.0.6/Microsoft.AspNetCore.Components.Server.dll \
//          aot/Wasp.AspNetCore.Blazor.Server/Vendor/Microsoft.AspNetCore.Components.Server.dll
//   2. AOT-compile the backend canister:
//        cd aot/samples/CircuitOnIc && dotnet build -c Release /p:IlcLlvmTarget=wasm32-wasi
//   3. Deploy via dfx (see aot/dfx.json — needs `circuitonic` (custom wasm)
//      and `circuitonic_assets` (asset canister) entries).
//   4. Open the asset-canister URL in a browser. blazor.web.js is
//      configured (in App.razor) to use HttpTransportType.LongPolling so
//      it never even tries to open a WebSocket.

namespace WaspSample.CircuitOnIc;

public static class Program
{
    private static IcCircuitTransportRegistry? _registry;
    private static CircuitHubFacade? _facade;

    // Pin AOT trim-dependencies (same pattern as RazorOnIc).
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(App))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Routes))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Components.Layout.MainLayout))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Counter))]
    [DynamicDependency("FromMilliseconds(System.Int64)", typeof(TimeSpan))]
    // Keep reflection metadata on the private `_context` field of the
    // renderer's dispatcher. We yank it at runtime to install the
    // RendererSynchronizationContext as Current — Dispatcher.CheckAccess
    // then returns true and DispatchEventAsync runs the @onclick handler
    // inline (no ThreadPool pump, which wasm32-wasi doesn't have).
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

            // Same DataProtection pre-seed as RazorOnIc (M2 #52 workaround).
            builder.Services.UseIcDataProtection();

            builder.Services.AddLogging();
            builder.Services.AddSingleton<System.Text.Encodings.Web.HtmlEncoder>(
                System.Text.Encodings.Web.HtmlEncoder.Default);
            builder.Services.AddSingleton<System.Text.Encodings.Web.JavaScriptEncoder>(
                System.Text.Encodings.Web.JavaScriptEncoder.Default);
            builder.Services.AddSingleton<System.Text.Encodings.Web.UrlEncoder>(
                System.Text.Encodings.Web.UrlEncoder.Default);

            // The full Razor Components stack. AddInteractiveServerRenderMode
            // is what brings in CircuitFactory + the friends CircuitHubFacade
            // needs to resolve.
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents(options =>
                {
                    options.DetailedErrors = true;
                });
            builder.Services.AddAntiforgery();

            builder.WebHost.UseIcCanister();

            var app = builder.Build();

            // Standard Razor Components endpoints require Antiforgery
            // middleware in the pipeline — the framework's
            // RazorComponentEndpointFactory stamps every endpoint with
            // RequireAntiforgeryToken metadata.
            app.UseAntiforgery();

            // Wasp marker middleware — appends <!--Blazor:server,...-->
            // comments to the SSR HTML so blazor.web.js knows there's a
            // server-interactive component to hydrate. See
            // Wasp.AspNetCore.Blazor.Server/src/BlazorMarkerMiddleware.cs
            // and gh issue #73 for why we hand-roll this instead of using
            // the framework's ServerComponentSerializer.
            app.UseWaspBlazorMarker();

            // Build a transport registry first so the negotiate endpoint
            // (registered below) can mint connection-id-keyed transports.
            _registry = new IcCircuitTransportRegistry();
            _registry.TransportConnected += transport =>
            {
                // ICircuitFactory is what AddInteractiveServerComponents
                // registers in DI; the concrete CircuitFactory is the
                // implementation. Both are public via our Cecil weaver.
                var factory = app.Services.GetRequiredService<ICircuitFactory>();
                _facade = CircuitHubFacade.Bind(transport, factory, app.Services);
            };
            _registry.TransportDisconnected += async _ =>
            {
                if (_facade is not null)
                {
                    await _facade.DisposeAsync();
                    _facade = null;
                }
            };

            // Hand-rolled SignalR Long Polling endpoints — replaces the
            // ic-websocket-gateway round-trip with three pure-HTTP routes
            // that ride the standard IC HTTP gateway:
            //   POST /_blazor/negotiate    advertise LongPolling
            //   POST /_blazor?id=...       inbound BlazorPack frames
            //   GET  /_blazor?id=...       drain queued outbound frames
            //   DELETE /_blazor?id=...     close
            app.MapWaspBlazorLongPolling(_registry);

            // Serve blazor.web.js (and any other wwwroot/* embedded
            // resources) so the page is self-contained — no cross-origin
            // script loading from a separate asset canister.
            app.MapWaspEmbeddedStaticFiles(typeof(Program).Assembly);

            // Register the SAME assets with the in-canister static-asset
            // map so they're served directly from a query call (~50 ms)
            // instead of upgrading to a ~3 s update call. Reach the
            // canister via the `.raw.localhost` subdomain so the IC
            // gateway skips response verification.
            try { EmbeddedStaticFiles.RegisterWaspStaticAssets(typeof(Program).Assembly); }
            catch (Exception sex)
            {
                Reply.Print($"[init] RegisterWaspStaticAssets failed: {sex.GetType().Name}: {sex.Message}");
            }

            // POST /api/sync — writes the supplied count to stable
            // memory. Update call, ~1.3 s. Client fires this every
            // few seconds in the background OR on page unload via
            // navigator.sendBeacon. Hot-path clicks stay on the query
            // path — only persistence ever hits an update call.
            // Must be registered BEFORE StartAsync so routing builds
            // the endpoint table with it included.
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

            // Serve the SSR shell. NOT calling .AddInteractiveServerRenderMode()
            // because it wires ServerComponentSerializer which needs JSON
            // reflection (PNS on wasm32-wasi). We emit our own marker via
            // BlazorMarkerMiddleware above; the descriptor format is decoded
            // by WaspComponentRecordParser in CircuitHubFacade.StartCircuit.
            app.MapRazorComponents<App>();

            app.StartAsync().GetAwaiter().GetResult();

            // Pre-render the SSR shell and register it as a static
            // asset so GET / serves from the query path (~100 ms on
            // .raw.localhost) instead of upgrading to an update call
            // (~1.5 s). The shell is deterministic (Counter type +
            // marker descriptors don't vary per request) so a single
            // capture at init suffices until the next canister upgrade.
            try { IcServer.RegisterRenderedPath("/"); }
            catch (Exception rex)
            {
                Reply.Print($"[init] RegisterRenderedPath(/) failed (continuing): {rex.GetType().Name}: {rex.Message}");
            }

            // SignalR's blazor.web.js fires GET /_blazor/initializers
            // on startup; in our setup it just returns an empty `[]`
            // JSON. Serving that from the query path saves another
            // ~1 s update-call.
            IcServer.RegisterStaticAsset(
                "/_blazor/initializers",
                System.Text.Encoding.UTF8.GetBytes("[]"),
                "application/json; charset=utf-8");

            // QUERY-RPC PROTOTYPE — GET /api/click?c=<N> returns
            // {"count": N+1}. Stateless: the canister doesn't persist
            // the count from the query; the client carries it in the
            // query string. Returns in ~12 ms (query call) vs ~1.3 s
            // for the SignalR update-call path.
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
                var bytes = System.Text.Encoding.UTF8.GetBytes(
                    "{\"count\":" + (prev + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");
                return (bytes, "application/json; charset=utf-8");
            });

            // GET /api/state — reads the persisted counter from stable
            // memory. Single 4-byte read at offset 0. Query call, no
            // consensus. ~10 ms round trip on raw.localhost.
            IcServer.RegisterQueryHandler("/api/state", url =>
            {
                int count = CounterStableStore.Read();
                var bytes = System.Text.Encoding.UTF8.GetBytes(
                    "{\"count\":" + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");
                return (bytes, "application/json; charset=utf-8");
            });

            // /api/sync moved above to be registered before StartAsync.
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
