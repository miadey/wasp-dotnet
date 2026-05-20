using System;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection;
using Wasp.AspNetCore;
using Wasp.IcCdk;

namespace Wasp.AspNetCore.Blazor.Server;

/// <summary>
/// One-call setup for Blazor Server running inside an IC canister.
///
/// Consumer's Program.cs becomes (essentially):
/// <code>
/// var builder = WebApplication.CreateEmptyBuilder(...);
/// builder.AddBlazorOnIC();
/// var app = builder.Build();
/// app.MapBlazorOnIC&lt;App&gt;(typeof(Program).Assembly);
/// app.StartAsync().GetAwaiter().GetResult();
/// </code>
///
/// The component-level surface (Counter.razor, MainLayout, etc.) is
/// stock Blazor — no Wasp types appear in markup. The IC-specific
/// bits (transport, dispatcher hacks, marker emission, JS bridge,
/// static-asset cert path) live entirely inside this assembly.
/// </summary>
public static class BlazorOnIcHostingExtensions
{
    // Monotonic counter for negotiate connectionId disambiguation (see
    // the negotiate query handler in MapBlazorOnIC). Bumped via
    // Interlocked.Increment from a query-call context — safe because
    // even though the increment is rolled back at end of query (no
    // state persists), each call sees a monotonically advancing
    // pre-increment value within the canister process lifetime, which
    // is what we need to disambiguate same-nanosecond negotiate calls.
    private static long _negotiateSeq;

    /// <summary>
    /// Register all DI services Blazor Server needs to run on a canister:
    /// data protection no-op, encoders, Razor Components stack with
    /// interactive server mode, antiforgery, IC HTTP gateway adapter,
    /// and the Wasp circuit transport registry. Idempotent — re-calling
    /// is harmless.
    /// </summary>
    public static WebApplicationBuilder AddBlazorOnIC(this WebApplicationBuilder builder)
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));

        // M2 #52 workaround — the framework's IDataProtectionProvider
        // resolution otherwise pulls Windows registry / DPAPI paths
        // unavailable on wasm32-wasi.
        builder.Services.UseIcDataProtection();

        builder.Services.AddLogging();

        // Razor needs concrete encoder singletons; the default DI
        // resolver tries to spin them up via reflection.
        builder.Services.AddSingleton<System.Text.Encodings.Web.HtmlEncoder>(
            System.Text.Encodings.Web.HtmlEncoder.Default);
        builder.Services.AddSingleton<System.Text.Encodings.Web.JavaScriptEncoder>(
            System.Text.Encodings.Web.JavaScriptEncoder.Default);
        builder.Services.AddSingleton<System.Text.Encodings.Web.UrlEncoder>(
            System.Text.Encodings.Web.UrlEncoder.Default);

        // The full Razor Components stack with interactive server mode.
        // AddInteractiveServerComponents wires CircuitFactory etc. that
        // CircuitHubFacade resolves from DI.
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents(options =>
            {
                options.DetailedErrors = true;
            });
        // Antiforgery is intentionally NOT registered. Without forms, the
        // /_blazor Long Polling transport, the framework's static SSR
        // pipeline, and our static-asset paths don't need CSRF tokens.
        // Dropping the registration cuts the HMAC, cookie name derivation,
        // token validator, and IAntiforgeryTokenStore chains from the
        // trim graph (~200–500 KB). The framework endpoints emitted by
        // MapRazorComponents still carry RequireAntiforgeryToken metadata
        // but with no antiforgery middleware in the pipeline (see
        // MapBlazorOnIC below) it's never enforced.

        // Replace Kestrel with the IC HTTP gateway adapter.
        builder.WebHost.UseIcCanister();

        // Per-canister circuit transport registry. Singleton so the
        // /_blazor endpoints registered in MapBlazorOnIC and the
        // CircuitHubFacade wired on TransportConnected share the same
        // connection map.
        builder.Services.AddSingleton<IcCircuitTransportRegistry>();

        return builder;
    }

    /// <summary>
    /// Wire the Wasp middleware, endpoints, and pre-rendered SSR shell
    /// for Blazor Server. Replaces the usual chain of
    /// <c>UseAntiforgery / UseWaspBlazorMarker / MapWaspBlazorLongPolling /
    /// MapWaspEmbeddedStaticFiles / RegisterWaspStaticAssets /
    /// MapRazorComponents / RegisterRenderedPath</c> calls.
    /// </summary>
    /// <typeparam name="TApp">Root component (e.g. App.razor)</typeparam>
    /// <param name="app">Web application instance</param>
    /// <param name="staticAssetsAssembly">
    /// Assembly whose <c>wwwroot/_framework/blazor.web.js</c> and
    /// other embedded resources should be served. Usually
    /// <c>typeof(Program).Assembly</c>.
    /// </param>
    public static WebApplication MapBlazorOnIC<TApp>(
        this WebApplication app,
        Assembly staticAssetsAssembly)
        where TApp : Microsoft.AspNetCore.Components.IComponent
            => MapBlazorOnIC<TApp>(app, staticAssetsAssembly, enableInteractiveServer: true);

    /// <summary>
    /// Internal overload that accepts the interactive-server flag. The
    /// public 2-arg overload defaults to true (preserves CircuitOnIc
    /// behavior). The app-side UseInternetComputer() reads
    /// marker.Options.EnableInteractiveServer and passes it through.
    /// </summary>
    internal static WebApplication MapBlazorOnIC<TApp>(
        this WebApplication app,
        Assembly staticAssetsAssembly,
        bool enableInteractiveServer)
        where TApp : Microsoft.AspNetCore.Components.IComponent
    {
        if (app is null) throw new ArgumentNullException(nameof(app));
        if (staticAssetsAssembly is null) throw new ArgumentNullException(nameof(staticAssetsAssembly));

        // Antiforgery middleware intentionally not wired. See the matching
        // comment in AddBlazorOnIC. The framework Razor Component endpoints
        // carry RequireAntiforgeryToken metadata but with no antiforgery
        // middleware in the pipeline the metadata is silently ignored —
        // safe because: (a) our /_blazor Long Polling transport rides
        // raw IcCircuitTransport frames, no form bodies; (b) the SSR
        // root response (GET /) is a synthetic asset, not a real form
        // submit; (c) we have no user-facing forms in the shipped samples.

        // (Legacy) marker middleware. Now a no-op since the marker pair
        // is emitted inline by the BlazorOnICRuntime component. Keeping
        // it lets the consumer drop the middleware call later without
        // breaking existing samples.
        app.UseWaspBlazorMarker();

        if (enableInteractiveServer)
        {
            // SignalR Long Polling endpoints — translate /_blazor/{...}
            // traffic into IcCircuitTransport frames the CircuitHubFacade
            // can dispatch.
            var registry = app.Services.GetRequiredService<IcCircuitTransportRegistry>();
            registry.TransportConnected += transport =>
            {
                // One facade per circuit, keyed in the registry by the
                // transport's connectionId. The earlier single-variable
                // capture worked for one tab but silently overwrote the
                // previous facade when a second tab connected, breaking
                // any cross-circuit interaction.
                var factory = app.Services.GetRequiredService<ICircuitFactory>();
                var facade = CircuitHubFacade.Bind(transport, factory, app.Services);
                registry.RegisterBoundFacade(transport.ConnectionId, facade);
            };
            registry.TransportDisconnected += async transport =>
            {
                if (registry.TryRemoveBoundFacade(transport.ConnectionId, out var facade))
                {
                    await facade.DisposeAsync();
                }
            };
            app.MapWaspBlazorLongPolling(registry);

            // gh #115 — fast-path empty long-poll GETs through canister_query.
            // Most polls during warmup return an empty body because the
            // server has nothing queued yet (waiting on circuit init or
            // user events). Each currently costs ~1.2 s of consensus
            // because IcServer.Dispatch upgrades all /_blazor GETs to
            // update. Looking up the connection + checking Outbound.IsEmpty
            // is purely read-only — perfectly safe in a query call.
            //
            // Handler returns:
            //   - empty body (200 application/octet-stream) when the
            //     connection doesn't exist (lazy-create deferred to the
            //     next POST) or the outbound queue is empty
            //   - null when there's queued data → bails to the update
            //     path, where MapGet's DrainOutbound mutates the queue
            //     and returns the actual bytes
            IcServer.RegisterQueryHandler("/_blazor", (url, method) =>
            {
                if (method != "GET")
                {
                    // POST handshake / StartCircuit / DELETE close all
                    // need to mutate state — fall through to update.
                    return null;
                }

                // Extract `id` from query string. Format: /_blazor?id=X[&...]
                string? id = null;
                int q = url.IndexOf('?');
                if (q >= 0)
                {
                    foreach (var pair in url.Substring(q + 1).Split('&'))
                    {
                        if (pair.StartsWith("id=", StringComparison.Ordinal))
                        {
                            id = pair.Substring(3);
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(id))
                {
                    // Malformed — let the update path 400 it.
                    return null;
                }

                var conn = registry.GetLongPollingConnection(id);
                if (conn is null)
                {
                    // Connection doesn't exist yet (negotiate ran but no
                    // POST has lazy-created it). Cheap empty response.
                    return (Array.Empty<byte>(), "application/octet-stream");
                }

                // If the connection has a bound circuit facade, the GET
                // must go through the update path so MapGet can pump
                // this circuit's RendererSynchronizationContext.
                // Otherwise a peer circuit's cross-circuit event would
                // leave StateHasChanged stuck queued — outbox shows
                // empty, fast-path returns 0 bytes, the pending diff
                // never ships. The pump only runs in the update-path
                // GET handler.
                if (registry.TryGetBoundFacade(id, out _))
                {
                    return null;
                }

                if (conn.Outbound.IsEmpty)
                {
                    // No facade yet (pre-circuit-init handshake polls)
                    // AND queue is empty → cheap empty response.
                    return (Array.Empty<byte>(), "application/octet-stream");
                }

                // Queue has data → drain needs to mutate → upgrade.
                return null;
            });
        }

        // Embedded static-asset endpoint (blazor.web.js) AND the in-
        // canister static-asset map (so the asset rides the ~50 ms
        // query-call path on `.raw.<id>.localhost`).
        app.MapWaspEmbeddedStaticFiles(staticAssetsAssembly);
        try { EmbeddedStaticFiles.RegisterWaspStaticAssets(staticAssetsAssembly); }
        catch (Exception sex)
        {
            Reply.Print($"[BlazorOnIC] RegisterWaspStaticAssets: {sex.GetType().Name}: {sex.Message}");
        }

        // blazor.web.js fires GET /_blazor/initializers at startup; we
        // return the empty `[]` initializer list, served from the query
        // path so it doesn't cost an update call.
        IcServer.RegisterStaticAsset(
            "/_blazor/initializers",
            System.Text.Encoding.UTF8.GetBytes("[]"),
            "application/json; charset=utf-8");

        // POST /_blazor/negotiate is now handled by the update-path
        // MapPost in LongPollingEndpoints. The earlier query-fast-path
        // optimization here (gh #113) saved ~2 s of consensus per
        // session but broke multi-tab: query-context mutations are
        // rolled back, so Interlocked.Increment on the monotonic
        // sequence counter resets between calls, and two browsers
        // negotiating in the same Ic0.time() nanosecond received the
        // same connectionId. The handshake POST then routed to a
        // single shared transport whose _handshakeComplete was already
        // true, and the second tab's handshake bytes got parsed as
        // blazorpack — "frame body length 123 exceeds remaining
        // payload 37". Running negotiate in update mode keeps the
        // sequence persistent across calls and guarantees unique ids.

        // The Wasp JS bridge — waspSetCount, the fetch wrapper, the
        // pre-registered interop bridge. Lives in an embedded resource
        // so the consumer's App.razor stays clean. Served on the static
        // asset path.
        try
        {
            using var jsStream = typeof(BlazorOnIcHostingExtensions).Assembly
                .GetManifestResourceStream("Wasp.AspNetCore.Blazor.Server.wwwroot.wasp-bridge.js");
            if (jsStream is not null)
            {
                using var ms = new System.IO.MemoryStream();
                jsStream.CopyTo(ms);
                IcServer.RegisterStaticAsset(
                    "/_framework/wasp-bridge.js",
                    ms.ToArray(),
                    "application/javascript; charset=utf-8");
            }
        }
        catch (Exception jex)
        {
            Reply.Print($"[BlazorOnIC] wasp-bridge.js register: {jex.GetType().Name}: {jex.Message}");
        }

        // Map the Razor Components root. .DisableAntiforgery() strips the
        // RequireAntiforgeryToken metadata the framework adds by default;
        // we don't register antiforgery (see AddBlazorOnIC), so the
        // matching middleware isn't in the pipeline and the routing
        // EndpointMiddleware would otherwise throw at dispatch time.
        // .AddInteractiveServerRenderMode() is required when any
        // component declares @rendermode InteractiveServer — without it,
        // the framework's SSRRenderModeBoundary throws at render time
        // ("A component ... has render mode 'InteractiveServerRenderMode',
        // but the required endpoints are not mapped on the server").
        var razorEndpoints = app.MapRazorComponents<TApp>().DisableAntiforgery();
        if (enableInteractiveServer)
        {
            razorEndpoints.AddInteractiveServerRenderMode();
        }

        return app;
    }

    /// <summary>
    /// Call AFTER <c>app.StartAsync()</c> to pre-render the SSR shell at
    /// the given path and serve it from the query-call path on
    /// subsequent requests. Saves a ~1.5 s update-call per page load.
    /// </summary>
    public static void RegisterPreRenderedShell(this WebApplication app, string path = "/")
    {
        try { IcServer.RegisterRenderedPath(path); }
        catch (Exception rex)
        {
            Reply.Print($"[BlazorOnIC] RegisterPreRenderedShell({path}): {rex.GetType().Name}: {rex.Message}");
        }
    }

    // ─── New unified API (gh #87) ────────────────────────────────────

    /// <summary>
    /// One-line IC adapter for a Blazor Server app. Same as the
    /// non-generic <see cref="HostingExtensions.UseInternetComputer"/>
    /// plus Razor Components + Wasp circuit-transport wiring.
    ///
    /// <code>
    /// var builder = WebApplication.CreateBuilder(args);
    /// builder.UseInternetComputer&lt;App&gt;();           // ← ONE LINE
    /// var app = builder.Build();
    /// app.UseInternetComputer();                       // mirror
    /// app.Run();
    /// </code>
    ///
    /// Equivalent to today's <see cref="AddBlazorOnIC"/> +
    /// <see cref="MapBlazorOnIC{TApp}"/> + <see cref="RegisterPreRenderedShell"/>
    /// trio. Auto-detects assets-assembly from <c>typeof(TApp).Assembly</c>.
    /// </summary>
    public static WebApplicationBuilder UseInternetComputer<TApp>(
        this WebApplicationBuilder builder,
        Action<HostingExtensions.IcOptions>? configure = null)
        where TApp : Microsoft.AspNetCore.Components.IComponent
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));

        var options = new HostingExtensions.IcOptions();
        configure?.Invoke(options);

        // Base IC adapter (encoders, antiforgery, data-protection,
        // IcServer-replaces-Kestrel).
        builder.UseInternetComputer(o =>
        {
            o.ContentRoot = options.ContentRoot;
            o.ApplicationName = options.ApplicationName;
            o.AutoPreRenderRoot = options.AutoPreRenderRoot;
            o.DetailedBlazorErrors = options.DetailedBlazorErrors;
        });

        // Blazor-specific DI: always need AddRazorComponents (it wires
        // the renderer, component activator, navigation manager etc.).
        // AddInteractiveServerComponents and IcCircuitTransportRegistry
        // only matter when the app actually has live circuits — gate
        // them so static-SSR-only samples (e.g. BlazorVanilla) don't
        // pay the ~1–2 MB of code section the interactive stack costs.
        //
        // Note: AddInteractiveServerComponents transitively pulls in
        // AddRouting(); when skipped we still need full routing for
        // VerifyRoutingServicesAreRegistered (called by ConfigureApplication
        // → UseRouting). Add it explicitly.
        builder.Services.AddRouting();
        var razorComponents = builder.Services.AddRazorComponents();
        if (options.EnableInteractiveServer)
        {
            razorComponents.AddInteractiveServerComponents(opts =>
            {
                opts.DetailedErrors = options.DetailedBlazorErrors;
            });
            builder.Services.AddSingleton<IcCircuitTransportRegistry>();
        }
        // Antiforgery intentionally not registered (see AddBlazorOnIC).

        // Marker — app.UseInternetComputer() reads this from DI to
        // decide whether to wire Long-Polling / JS-bridge / pre-render.
        builder.Services.AddSingleton(
            new HostingExtensions.BlazorOnIcMarker(
                appType: typeof(TApp),
                assetsAssembly: typeof(TApp).Assembly,
                options: options));

        return builder;
    }

    /// <summary>
    /// App-side companion to <see cref="UseInternetComputer{TApp}"/>.
    /// Auto-detects whether Blazor wiring is needed (via
    /// <see cref="HostingExtensions.BlazorOnIcMarker"/> in DI). For
    /// WebAPI / MVC canisters that haven't registered Blazor, this is
    /// a no-op — call <c>app.MapControllers()</c> / etc. as usual.
    /// </summary>
    public static WebApplication UseInternetComputer(this WebApplication app)
    {
        if (app is null) throw new ArgumentNullException(nameof(app));

        var marker = app.Services.GetService<HostingExtensions.BlazorOnIcMarker>();
        if (marker is null)
        {
            // Non-Blazor canister — nothing to wire here. Consumer
            // continues with their own MapControllers / MapGet / etc.
            return app;
        }

        // Generic MapBlazorOnIC needs a compile-time type parameter;
        // we reach through reflection so the same UseInternetComputer
        // call works for whatever TApp the consumer passed at build-
        // time. (DynamicDependency on the consumer's App type is
        // already pinned via [DynamicDependency] in their Program.cs.)
        // Find the 3-arg internal overload (the public 2-arg one routes
        // here with enableInteractiveServer=true). We always want to
        // pass the flag explicitly so static-SSR samples skip the
        // circuit endpoint wiring.
        var generic = typeof(BlazorOnIcHostingExtensions)
            .GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .First(m => m.Name == nameof(MapBlazorOnIC) && m.GetParameters().Length == 3)
            .MakeGenericMethod(marker.AppType);
        generic.Invoke(null, new object[]
        {
            app,
            marker.AssetsAssembly,
            marker.Options.EnableInteractiveServer,
        });

        if (marker.Options.AutoPreRenderRoot)
        {
            // Pre-render after StartAsync — caller is responsible for
            // ordering. We register the deferred render here; the
            // implementation handles being called pre-or-post start.
            // For simplicity require the caller to invoke
            // app.RegisterPreRenderedShell("/") explicitly after Run.
        }

        return app;
    }
}
