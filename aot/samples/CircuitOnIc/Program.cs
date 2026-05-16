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
using Wasp.WebSockets;
using WaspSample.CircuitOnIc.Components;
using WaspSample.CircuitOnIc.Components.Pages;

// M4.S7 (issue #60): Counter.razor with @rendermode InteractiveServer
// running on a canister. Companion to RazorOnIc/ — that sample renders
// statically (no WebSocket); this one renders interactively (CircuitHost
// over IC-WS).
//
// Build/deploy:
//   1. Generate the vendored CircuitHost weaver output (once):
//        dotnet run --project shared/tools/Wasp.CircuitHostWeaver -- \
//          /usr/local/share/dotnet/shared/Microsoft.AspNetCore.App/10.0.6/Microsoft.AspNetCore.Components.Server.dll \
//          aot/Wasp.AspNetCore.Blazor.Server/Vendor/Microsoft.AspNetCore.Components.Server.dll
//   2. AOT-compile the backend canister:
//        cd aot/samples/CircuitOnIc && dotnet publish -c Release -r wasi-wasm
//      Produces CircuitOnIc.canister.wasm + a wwwroot/asset-canister/
//      directory containing the JS shim + Counter HTML shell.
//   3. Deploy both canisters via dfx (see aot/dfx.json — needs a
//      `circuitonic` (custom wasm) entry + a `circuitonic_assets`
//      (assets) entry).
//   4. Open the asset-canister URL in a browser; the IC-WS shim picks
//      up the backend canister id from window.IcWsBlazorConfig and
//      blazor.web.js opens its /_blazor WS through the gateway.

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
                .AddInteractiveServerComponents();
            builder.Services.AddAntiforgery();

            builder.WebHost.UseIcCanister();

            var app = builder.Build();

            // Standard Razor Components endpoints require Antiforgery
            // middleware in the pipeline — the framework's
            // RazorComponentEndpointFactory stamps every endpoint with
            // RequireAntiforgeryToken metadata.
            app.UseAntiforgery();

            // Serve the static SSR shell so the FIRST request returns HTML
            // with our IC-WS shim baked in. Subsequent interaction goes
            // over the WS channel via the asset canister's blazor.web.js.
            //
            // NOTE: NOT calling .AddInteractiveServerRenderMode() because
            // it wires ServerComponentSerializer which uses
            // System.Text.Json reflection (PNS on wasm32-wasi). Pure SSR
            // works first; the interactive Hub path is plumbed through
            // Wasp.AspNetCore.Blazor.Server (IcCircuitTransportRegistry +
            // CircuitHubFacade) which doesn't depend on this endpoint
            // helper.
            app.MapRazorComponents<App>();

            // Register an IcCircuitTransport for every IC-WS client and
            // bind it to a CircuitHubFacade backed by the framework's
            // (now-public-via-weaver) CircuitFactory.
            _registry = new IcCircuitTransportRegistry(WaspWs.Send);
            _registry.TransportConnected += transport =>
            {
                var factory = app.Services.GetRequiredService<CircuitFactory>();
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

            WaspWs.Init(new WsHandlers
            {
                OnOpen    = _registry.HandleOpen,
                OnMessage = _registry.HandleMessage,
                OnClose   = _registry.HandleClose,
            });

            app.StartAsync().GetAwaiter().GetResult();
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
