using System;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;

namespace Wasp.AspNetCore;

/// <summary>
/// One-line IC adapter for any stock .NET web app — Blazor, WebAPI,
/// MVC, Minimal API. Consumer's Program.cs becomes:
///
/// <code>
/// var builder = WebApplication.CreateBuilder(args);
/// builder.UseInternetComputer();                 // ← THE ONE LINE
/// builder.Services.AddControllers();             // stock
/// var app = builder.Build();
/// app.MapControllers();                          // stock
/// app.Run();
/// </code>
///
/// For Blazor consumers the typed overload &lt;TApp&gt; auto-registers
/// the Razor Components stack plus Wasp's Blazor circuit transport.
/// </summary>
public static class HostingExtensions
{
    /// <summary>
    /// Marker object placed in DI when the consumer registers
    /// Blazor-aware IC hosting. The app-side <see cref="UseInternetComputer"/>
    /// extension reads it to decide whether to wire the Long-Polling
    /// endpoints + JS bridge.
    /// </summary>
    public sealed class BlazorOnIcMarker
    {
        public Type AppType { get; }
        public System.Reflection.Assembly AssetsAssembly { get; }
        public IcOptions Options { get; }
        public BlazorOnIcMarker(Type appType, System.Reflection.Assembly assetsAssembly, IcOptions options)
        {
            AppType = appType;
            AssetsAssembly = assetsAssembly;
            Options = options;
        }
    }

    /// <summary>
    /// Replaces the default Kestrel <see cref="IServer"/> with
    /// <see cref="IcServer"/>, which routes IC HTTP gateway requests
    /// through the ASP.NET Core middleware pipeline instead of a TCP
    /// socket. Use this directly when you don't want Wasp's auxiliary
    /// services (encoders / antiforgery / data-protection).
    /// </summary>
    public static IWebHostBuilder UseIcCanister(this IWebHostBuilder builder)
        => builder.ConfigureServices(services =>
            services.AddSingleton<IServer, IcServer>());

    /// <summary>
    /// One-line IC adapter. Swaps Kestrel for <see cref="IcServer"/>
    /// and registers the auxiliary services every canister needs
    /// (data-protection no-op, HTML/JS/URL encoders, antiforgery,
    /// logging). Use this for WebAPI / MVC / Minimal API canisters
    /// that don't need Blazor wiring.
    /// </summary>
    public static WebApplicationBuilder UseInternetComputer(
        this WebApplicationBuilder builder,
        Action<IcOptions>? configure = null)
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));

        var options = new IcOptions();
        configure?.Invoke(options);

        builder.Services.UseIcDataProtection();
        builder.Services.AddLogging();
        builder.Services.AddSingleton<HtmlEncoder>(HtmlEncoder.Default);
        builder.Services.AddSingleton<JavaScriptEncoder>(JavaScriptEncoder.Default);
        builder.Services.AddSingleton<UrlEncoder>(UrlEncoder.Default);
        builder.Services.AddSingleton(options);
        builder.WebHost.UseIcCanister();

        return builder;
    }

    /// <summary>
    /// Configuration knobs for <see cref="UseInternetComputer"/>.
    /// </summary>
    public sealed class IcOptions
    {
        public string ContentRoot { get; set; } = "/canister";
        public string ApplicationName { get; set; } = "WaspCanister";
        public bool AutoPreRenderRoot { get; set; } = true;
        public bool DetailedBlazorErrors { get; set; } = true;

        /// <summary>
        /// When false (Blazor Static SSR only — e.g. BlazorVanilla),
        /// skip AddInteractiveServerComponents + the circuit transport
        /// registry + the /_blazor Long Polling endpoint chain. Cuts
        /// ~1–2 MB of code section that's pure dead weight for
        /// non-interactive samples. Default true (preserves CircuitOnIc-
        /// style live circuits).
        /// </summary>
        public bool EnableInteractiveServer { get; set; } = true;
    }

    /// <summary>
    /// Canister-friendly equivalent of <c>app.Run()</c>. Starts the
    /// host synchronously (so the routing table + services are live
    /// for incoming <c>http_request*</c> messages) and returns
    /// immediately — does NOT dispose the host. Use as the final
    /// line of your <c>[ModuleInitializer]</c>.
    ///
    /// <para>Stock <c>app.Run()</c> blocks on <c>WaitForShutdownAsync</c>
    /// and disposes the host in its <c>finally</c> block. Neither is
    /// appropriate for a canister: there's no message loop to block on
    /// (the IC runtime wakes us per-message) and disposing the host
    /// would tear down DI between messages.</para>
    /// </summary>
    public static void RunOnIC(this WebApplication app)
    {
        if (app is null) throw new ArgumentNullException(nameof(app));
        app.StartAsync().GetAwaiter().GetResult();
    }
}
