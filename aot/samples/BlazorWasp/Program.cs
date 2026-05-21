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

namespace WaspSample.BlazorWasp;

public static class Program
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Home))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Counter))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Weather))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Chat))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(CounterService))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(WeatherService))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ChatService))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(WaspHtmlRenderer))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(WaspRouter))]
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

            builder.Services.AddSingleton<CounterService>();
            builder.Services.AddSingleton<WeatherService>();
            builder.Services.AddSingleton<ChatService>();
            // Router is configured at registration time with the route map.
            builder.Services.AddSingleton<IWaspRenderer>(sp =>
            {
                var r = new WaspRouter(sp);
                r.AddRoute<Home>("/");
                r.AddRoute<Counter>("/counter");
                r.AddRoute<Weather>("/weather");
                r.AddRoute<Chat>("/chat");
                r.WrapShell((path, inner) => WrapWithSidebar(path, inner));
                return r;
            });

            builder.UseInternetComputerWasp();

            var app = builder.Build();
            app.UseInternetComputerWasp();
            app.StartAsync().GetAwaiter().GetResult();

            // Pre-render each route's SSR shell + register as a
            // certified static asset. Subsequent GETs to "/" / "/counter"
            // / "/weather" hit the query path (~300 ms on canonical
            // mainnet).
            var renderer = app.Services.GetRequiredService<IWaspRenderer>();
            RegisterShell(renderer, "/");
            RegisterShell(renderer, "/counter");
            RegisterShell(renderer, "/weather");
            RegisterShell(renderer, "/chat");
        }
        catch (Exception ex)
        {
            var msg = ex.GetType().FullName + ": " + ex.Message
                + (ex.StackTrace is { } st ? "\n" + st : "");
            IcServer.InitFailureMessage = msg;
            Reply.Print("[init-fail] " + msg);
        }
    }

    private static void RegisterShell(IWaspRenderer renderer, string path)
    {
        var batch = renderer.Render(new WaspRenderRequest { Path = path });
        var shell = BuildPage(batch.Html);
        var bytes = Encoding.UTF8.GetBytes(shell);
        IcServer.RegisterStaticAsset(path, bytes, "text/html; charset=utf-8");
        IcCertifiedAssets.Insert(path, bytes);
    }

    private static string WrapWithSidebar(string currentPath, string innerHtml)
    {
        string Active(string p) => string.Equals(p, currentPath, StringComparison.OrdinalIgnoreCase)
            ? " class=\"active\"" : "";
        var sb = new StringBuilder();
        sb.Append("<div class=\"page\">");
        sb.Append("<aside class=\"sidebar\">");
        sb.Append("<a class=\"brand\" href=\"/\">Blazor on ICP</a>");
        sb.Append("<nav class=\"nav\">");
        sb.Append("<a href=\"/\"").Append(Active("/")).Append(">Home</a>");
        sb.Append("<a href=\"/counter\"").Append(Active("/counter")).Append(">Counter</a>");
        sb.Append("<a href=\"/weather\"").Append(Active("/weather")).Append(">Weather</a>");
        sb.Append("<a href=\"/chat\"").Append(Active("/chat")).Append(">Chat</a>");
        sb.Append("</nav>");
        sb.Append("</aside>");
        sb.Append("<main>").Append(innerHtml).Append("</main>");
        sb.Append("</div>");
        return sb.ToString();
    }

    private static string BuildPage(string contentHtml)
    {
        return
@"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""utf-8"" />
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"" />
    <title>Blazor on ICP</title>
    <style>
        html, body { margin: 0; padding: 0; font-family: 'Helvetica Neue', Helvetica, Arial, sans-serif; color: #1e293b; background: #f8fafc; }
        .page { display: flex; min-height: 100vh; }
        .sidebar {
            width: 250px;
            background: linear-gradient(180deg, rgb(5, 39, 103) 0%, #3a0647 70%);
            color: #f8fafc;
            padding: 1.25rem 0;
        }
        .brand {
            color: #f8fafc; text-decoration: none; font-size: 1.1rem; font-weight: 600;
            padding: 0 1.25rem 1rem 1.25rem; display: block;
            border-bottom: 1px solid rgba(255,255,255,0.1); margin-bottom: 1rem;
        }
        .nav a {
            display: block; color: #cbd5f5; text-decoration: none;
            padding: 0.6rem 1.25rem; border-left: 4px solid transparent; font-size: 0.95rem;
        }
        .nav a:hover { background: rgba(255,255,255,0.1); color: #fff; }
        .nav a.active { background: rgba(255,255,255,0.18); color: #fff; border-left-color: #fff; }
        main { flex: 1; padding: 2rem; max-width: 960px; }
        main h1 { color: #1e3a8a; margin-top: 0; }
        main code { background: #f1f5f9; padding: 0.1rem 0.3rem; border-radius: 3px; font-size: 0.9em; }
        button.btn-primary {
            background: #2563eb; color: #fff; border: 0;
            padding: 0.45rem 1rem; border-radius: 4px; cursor: pointer; font-size: 0.95rem;
        }
        button.btn-primary:hover { background: #1d4ed8; }
        .table { border-collapse: collapse; width: 100%; margin-top: 1rem; }
        .table th, .table td { padding: 0.5rem; border-bottom: 1px solid #e2e8f0; text-align: left; }
        .table th { background: #f1f5f9; border-bottom-color: #cbd5f5; }
        p[role=""status""] { font-size: 1.1rem; }
        .chat-messages { list-style: none; padding: 0; margin: 1rem 0; max-height: 400px; overflow-y: auto; border: 1px solid #e2e8f0; border-radius: 4px; }
        .chat-messages li { padding: 0.5rem 0.75rem; border-bottom: 1px solid #f1f5f9; }
        .chat-messages li:last-child { border-bottom: 0; }
        .chat-sender { color: #2563eb; font-weight: 600; font-family: ui-monospace, monospace; font-size: 0.85rem; margin-right: 0.5rem; }
        .chat-text { color: #1e293b; }
        .chat-form { display: flex; gap: 0.5rem; }
        .chat-form input[type=text] { flex: 1; padding: 0.5rem 0.75rem; border: 1px solid #cbd5f5; border-radius: 4px; font-size: 0.95rem; }
        .chat-form button { white-space: nowrap; }
    </style>
</head>
<body>
    <div id=""wasp-root"">" + contentHtml + @"</div>
    <script src=""/_wasp/wasp.js""></script>
</body>
</html>";
    }
}
