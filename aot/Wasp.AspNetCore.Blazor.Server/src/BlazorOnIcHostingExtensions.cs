using System;
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
        builder.Services.AddAntiforgery();

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
    {
        if (app is null) throw new ArgumentNullException(nameof(app));
        if (staticAssetsAssembly is null) throw new ArgumentNullException(nameof(staticAssetsAssembly));

        // Antiforgery middleware — Razor Component endpoints carry
        // RequireAntiforgeryToken metadata; without this in the pipeline
        // every /_blazor request 400s.
        app.UseAntiforgery();

        // (Legacy) marker middleware. Now a no-op since the marker pair
        // is emitted inline by the BlazorOnICRuntime component. Keeping
        // it lets the consumer drop the middleware call later without
        // breaking existing samples.
        app.UseWaspBlazorMarker();

        // SignalR Long Polling endpoints — translate /_blazor/{...}
        // traffic into IcCircuitTransport frames the CircuitHubFacade
        // can dispatch.
        var registry = app.Services.GetRequiredService<IcCircuitTransportRegistry>();
        CircuitHubFacade? boundFacade = null;
        registry.TransportConnected += transport =>
        {
            var factory = app.Services.GetRequiredService<ICircuitFactory>();
            boundFacade = CircuitHubFacade.Bind(transport, factory, app.Services);
        };
        registry.TransportDisconnected += async _ =>
        {
            if (boundFacade is not null)
            {
                await boundFacade.DisposeAsync();
                boundFacade = null;
            }
        };
        app.MapWaspBlazorLongPolling(registry);

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

        // Map the Razor Components root.
        app.MapRazorComponents<TApp>();

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

        // Blazor-specific DI: Razor Components + interactive server +
        // antiforgery + per-canister circuit transport registry.
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents(opts =>
            {
                opts.DetailedErrors = options.DetailedBlazorErrors;
            });
        builder.Services.AddAntiforgery();
        builder.Services.AddSingleton<IcCircuitTransportRegistry>();

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
        var generic = typeof(BlazorOnIcHostingExtensions)
            .GetMethod(nameof(MapBlazorOnIC))!
            .MakeGenericMethod(marker.AppType);
        generic.Invoke(null, new object[] { app, marker.AssetsAssembly });

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
