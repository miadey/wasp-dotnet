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

// BlazorWasp — proof of concept for gh #118 (IC-native render-as-query).
// One Counter, no SignalR, no Long Polling, no negotiate handshake.
// Two endpoints behind the IC HTTP gateway:
//   GET  /_wasp/render → canister_query  (sub-300ms on mainnet)
//   POST /_wasp/event  → canister_update (one consensus round per click)

namespace WaspSample.BlazorWasp;

public static class Program
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(CounterRenderer))]
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

            // Renderer is a normal DI singleton. v1: developer writes
            // an IWaspRenderer by hand; v2 will autogenerate it from
            // Razor markup.
            builder.Services.AddSingleton<IWaspRenderer, CounterRenderer>();

            // One line of IC hosting setup.
            builder.UseInternetComputerWasp();

            var app = builder.Build();
            app.UseInternetComputerWasp();
            app.StartAsync().GetAwaiter().GetResult();

            // SSR pre-render: the index page is just a tiny shell that
            // loads the wasp.js bridge and includes the initial render
            // inline so the user sees something immediately. The bridge
            // hydrates by wiring events on the existing DOM.
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
    <p class=""badge"">render-as-query — no SignalR</p>
    <div id=""wasp-root"">" + initialHtml + @"</div>
    <p style=""color:#64748b;font-size:0.85rem;margin-top:2rem"">
        Click the button. Each click is one IC update call (~2 s consensus).
        No warmup, no polling. View state always reflects the canister's
        latest certified state.
    </p>
    <script src=""/_wasp/wasp.js""></script>
</body>
</html>";
    }
}
