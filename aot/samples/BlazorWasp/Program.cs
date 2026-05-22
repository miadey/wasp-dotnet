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
        /* ── Discord-style chat ─────────────────────────────────────── */
        main:has(.dc-shell) { padding: 0; max-width: none; }
        .dc-shell {
            display: flex; flex-direction: column;
            height: 100vh; background: #313338; color: #dcddde;
            font: 16px/1.4 'Segoe UI', system-ui, sans-serif;
        }
        .dc-channel-header {
            flex: 0 0 auto; height: 48px;
            display: flex; align-items: center; justify-content: space-between;
            padding: 0 1rem; background: #313338; color: #f2f3f5;
            border-bottom: 1px solid rgba(0,0,0,0.2);
            box-shadow: 0 1px 0 rgba(0,0,0,0.2);
        }
        .dc-channel-title { display: flex; align-items: center; gap: 0.3rem; font-weight: 600; font-size: 1rem; }
        .dc-hash { color: #80848e; font-weight: 500; font-size: 1.4rem; }
        .dc-channel-tag { color: #80848e; font-size: 0.8rem; }
        .dc-messages {
            flex: 1 1 auto; min-height: 0; overflow-y: auto;
            padding: 1rem 0; scroll-behavior: smooth;
        }
        .dc-messages::-webkit-scrollbar { width: 16px; }
        .dc-messages::-webkit-scrollbar-track { background: #2b2d31; }
        .dc-messages::-webkit-scrollbar-thumb { background: #1a1b1e; border: 4px solid #2b2d31; border-radius: 8px; min-height: 40px; }
        .dc-empty { padding: 2rem 1rem; color: #b5bac1; }
        .dc-empty h2 { color: #fff; margin: 0 0 0.5rem; font-size: 1.6rem; font-weight: 700; }
        .dc-empty p { margin: 0; color: #b5bac1; }
        .dc-divider {
            text-align: center; margin: 1rem 1rem 0.5rem;
            font-size: 0.75rem; color: #949ba4; font-weight: 600;
            border-top: 1px solid rgba(255,255,255,0.06);
            position: relative;
        }
        .dc-divider span { background: #313338; padding: 0 0.6rem; position: relative; top: -0.6rem; }
        .dc-message {
            display: grid; grid-template-columns: 56px 1fr;
            padding: 0.15rem 1rem 0.15rem 0; margin-top: 1.0rem;
        }
        .dc-message:hover { background: rgba(4,4,5,0.07); }
        .dc-message-grouped { margin-top: 0; padding-top: 0.1rem; }
        .dc-avatar {
            grid-column: 1; justify-self: center; align-self: start;
            width: 40px; height: 40px; border-radius: 50%;
            display: flex; align-items: center; justify-content: center;
            color: #fff; font-weight: 600; font-size: 1.1rem;
            margin-top: 2px;
        }
        .dc-avatar-spacer {
            grid-column: 1; align-self: start; justify-self: end;
            color: #6e7177; font-size: 0.7rem;
            padding-right: 0.5rem; padding-top: 0.25rem;
            visibility: hidden; font-variant-numeric: tabular-nums;
        }
        .dc-message:hover .dc-avatar-spacer { visibility: visible; }
        .dc-message-body { grid-column: 2; min-width: 0; }
        .dc-message-head { display: flex; align-items: baseline; gap: 0.5rem; margin-bottom: 0.1rem; }
        .dc-username { font-weight: 600; font-size: 1rem; }
        .dc-time { color: #949ba4; font-size: 0.75rem; }
        .dc-text {
            color: #dbdee1; font-size: 1rem; line-height: 1.4;
            white-space: pre-wrap; word-wrap: break-word;
        }
        .dc-composer {
            flex: 0 0 auto; padding: 0 1rem 1.5rem; background: #313338;
            display: grid; grid-template-columns: 180px 1fr auto; gap: 0.5rem; align-items: stretch;
        }
        .dc-username-input {
            background: #383a40; color: #fff; border: 0; outline: none;
            padding: 0.75rem 0.85rem; border-radius: 8px;
            font: inherit; font-size: 0.95rem;
        }
        .dc-username-input::placeholder { color: #80848e; }
        .dc-composer-input {
            background: #383a40; color: #dcddde; border: 0; outline: none; resize: none;
            padding: 0.75rem 1rem; border-radius: 8px; min-height: 44px; max-height: 50vh;
            font: inherit; font-size: 1rem; line-height: 1.375;
        }
        .dc-composer-input::placeholder { color: #80848e; }
        .dc-send {
            background: #5865f2; color: #fff; border: 0;
            padding: 0 1rem; border-radius: 8px; cursor: pointer;
            display: flex; align-items: center; justify-content: center;
            transition: background 0.12s ease;
        }
        .dc-send:hover { background: #4752c4; }
        .dc-send:disabled { background: #4e5058; cursor: not-allowed; opacity: 0.6; }
        @@media (max-width: 600px) {
            .dc-composer { grid-template-columns: 1fr auto; }
            .dc-username-input { grid-column: 1 / -1; }
        }
    </style>
</head>
<body>
    <div id=""wasp-root"">" + contentHtml + @"</div>
    <script src=""/_wasp/wasp.js""></script>
</body>
</html>";
    }
}
