using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Wasp.AspNetCore;
using Wasp.AspNetCore.Blazor.Wasp;
using Wasp.IcCdk;
using WaspSample.BlazorWasp.Components.Pages;

// BlazorWasp — gh #118 v2: stock vanilla Razor with @onclick driving
// the render-as-query protocol. No SignalR, no Long Polling, no
// negotiate handshake. Just two HTTP endpoints:
//   GET  /_wasp/render → canister_query  (sub-300ms on mainnet)
//   POST /_wasp/event  → canister_update (one consensus round per click)

namespace WaspSample.BlazorWasp;

public static class Program
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Counter))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(CounterService))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(WaspComponentRenderer<Counter>))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(WaspHtmlRenderer))]
    [ModuleInitializer]
    internal static void Init()
    {
        try
        {
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
            {
                ContentRootPath = "/canister",
                ApplicationName = "BlazorWasp",
            });

            // Singletons: state survives across calls. Component
            // instances are throwaway (re-created each render).
            builder.Services.AddSingleton<CounterService>();
            builder.Services.AddSingleton<IWaspRenderer, WaspComponentRenderer<Counter>>();

            builder.UseInternetComputerWasp();

            var app = builder.Build();
            app.UseInternetComputerWasp();
            app.StartAsync().GetAwaiter().GetResult();

            // SSR pre-render: shell with the Counter's initial HTML
            // inlined. Bridge hydrates by wiring event listeners on
            // the existing DOM.
            var renderer = app.Services.GetRequiredService<IWaspRenderer>();
            var initial = renderer.Render(new WaspRenderRequest { Path = "/" });
            var shellHtml = BuildShell(initial.Html);
            IcServer.RegisterStaticAsset(
                "/",
                Encoding.UTF8.GetBytes(shellHtml),
                "text/html; charset=utf-8");
            IcCertifiedAssets.Insert("/", Encoding.UTF8.GetBytes(shellHtml));
        }
        catch (Exception ex)
        {
            var msg = ex.GetType().FullName + ": " + ex.Message
                + (ex.StackTrace is { } st ? "\n" + st : "");
            IcServer.InitFailureMessage = msg;
            Reply.Print("[init-fail] " + msg);
        }
    }

    private static string BuildShell(string initialHtml)
    {
        return
@"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""utf-8"" />
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"" />
    <title>Blazor on ICP — render-as-query</title>
    <style>
        body { font-family: system-ui; max-width: 720px; margin: 2rem auto; padding: 0 1rem; color: #1e293b; }
        h1 { color: #2563eb; }
        button.btn-primary {
            background: #2563eb; color: #fff; border: 0;
            padding: 0.5rem 1.25rem; border-radius: 4px; cursor: pointer;
            font-size: 1rem;
        }
        button.btn-primary:hover { background: #1d4ed8; }
        p[role=""status""] { font-size: 1.1rem; }
        .badge {
            display: inline-block; padding: 0.15rem 0.4rem; border-radius: 3px;
            background: #ecfdf5; color: #047857; font-size: 0.85rem;
            font-family: ui-monospace, monospace;
        }
    </style>
</head>
<body>
    <p class=""badge"">stock @@onclick — render-as-query — no SignalR</p>
    <div id=""wasp-root"">" + initialHtml + @"</div>
    <p style=""color:#64748b;font-size:0.85rem;margin-top:2rem"">
        Counter.razor is vanilla Blazor markup: <code>@@onclick=""Counter.Increment""</code>.
        Each click is one IC update call (~2 s consensus) with the
        post-event render inline. No warmup, no polling.
    </p>
    <script src=""/_wasp/wasp.js""></script>
</body>
</html>";
    }
}
