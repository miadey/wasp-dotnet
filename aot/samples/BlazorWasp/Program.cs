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
        .chat-container { display: flex; flex-direction: column; gap: 1rem; height: calc(100vh - 4rem); max-height: 800px; }
        .chat-header h1 { margin: 0 0 0.25rem; }
        .chat-subtitle { color: #64748b; font-size: 0.85rem; margin: 0; }
        .chat-scroll {
            flex: 1; min-height: 200px; overflow-y: auto;
            background: #fff; border: 1px solid #e2e8f0; border-radius: 8px;
            padding: 0.75rem 1rem; display: flex; flex-direction: column; gap: 0.6rem;
            scroll-behavior: smooth;
        }
        .chat-empty { color: #94a3b8; font-style: italic; text-align: center; margin: auto; }
        .chat-message { background: #f8fafc; border-radius: 8px; padding: 0.6rem 0.85rem; box-shadow: 0 1px 2px rgba(15,23,42,0.04); }
        .chat-message-header { display: flex; align-items: center; gap: 0.5rem; margin-bottom: 0.25rem; }
        .chat-avatar {
            width: 1.6rem; height: 1.6rem; border-radius: 50%; color: #fff;
            display: inline-flex; align-items: center; justify-content: center;
            font-size: 0.75rem; font-weight: 700; letter-spacing: 0.5px;
        }
        .chat-sender { font-family: ui-monospace, SFMono-Regular, monospace; font-size: 0.78rem; color: #475569; font-weight: 600; letter-spacing: 0.2px; }
        .chat-time { font-size: 0.7rem; color: #94a3b8; margin-left: auto; font-variant-numeric: tabular-nums; }
        .chat-body { margin: 0; padding-left: 2.1rem; white-space: pre-wrap; word-wrap: break-word; color: #1e293b; font-size: 0.95rem; line-height: 1.4; }
        .chat-composer {
            display: flex; gap: 0.6rem; align-items: stretch;
            background: #fff; border: 1px solid #e2e8f0; border-radius: 8px;
            padding: 0.5rem; box-shadow: 0 1px 2px rgba(15,23,42,0.04);
        }
        .chat-input {
            flex: 1; resize: none; border: 0; outline: none;
            font: inherit; font-size: 0.95rem; padding: 0.4rem 0.6rem;
            line-height: 1.4; color: #1e293b; background: transparent;
        }
        .chat-input::placeholder { color: #94a3b8; }
        .chat-send {
            background: #2563eb; color: #fff; border: 0;
            padding: 0 1.4rem; border-radius: 6px; cursor: pointer;
            font-size: 0.95rem; font-weight: 600;
            transition: background 0.15s ease;
        }
        .chat-send:hover { background: #1d4ed8; }
        .chat-send:disabled { background: #94a3b8; cursor: not-allowed; }
    </style>
</head>
<body>
    <div id=""wasp-root"">" + contentHtml + @"</div>
    <script src=""/_wasp/wasp.js""></script>
</body>
</html>";
    }
}
