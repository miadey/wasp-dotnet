using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
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

            // App-side mirror — auto-detects the BlazorOnIcMarker via DI
            // and wires Long-Polling / JS bridge / static assets.
            app.UseInternetComputer();

            // Canister-friendly app.Run() — StartAsync().GetAwaiter()
            // .GetResult() without disposing the host.
            app.RunOnIC();

            // Pre-render the SSR shell for the home route so cold loads
            // serve from the ~5 ms query path.
            IcServer.RegisterRenderedPath("/");
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
