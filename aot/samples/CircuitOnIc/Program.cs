using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
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

            app.StartAsync().GetAwaiter().GetResult();

            // After StartAsync: pre-render the SSR shell into the
            // canister's static-asset cache so subsequent page loads
            // serve in ~5 ms instead of ~1.5 s.
            app.RegisterPreRenderedShell("/");
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
