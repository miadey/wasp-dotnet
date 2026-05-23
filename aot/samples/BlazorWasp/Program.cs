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
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Place))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(CounterService))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(WeatherService))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ChatService))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(PixelService))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ImageStore))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(TetrisService))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(TetrisAssets))]
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
            builder.Services.AddSingleton<PixelService>();
            builder.Services.AddSingleton<ImageStore>();
            builder.Services.AddSingleton<TetrisService>();
            builder.Services.AddSingleton<IWaspRenderer>(sp =>
            {
                var r = new WaspRouter(sp);
                r.AddRoute<Home>("/");
                r.AddRoute<Counter>("/counter");
                r.AddRoute<Weather>("/weather");
                r.AddRoute<Chat>("/chat");
                r.AddRoute<Place>("/place");
                r.WrapShell((path, inner) => WrapWithSidebar(path, inner));
                return r;
            });

            builder.UseInternetComputerWasp();

            var app = builder.Build();
            app.UseInternetComputerWasp();
            app.StartAsync().GetAwaiter().GetResult();

            // Pre-render each route's SSR shell + register as a
            // certified static asset. Subsequent direct GETs to "/" /
            // "/counter" / "/weather" / "/chat" / "/place" hit the
            // query path (~300 ms on canonical mainnet).
            var renderer = app.Services.GetRequiredService<IWaspRenderer>();
            RegisterShell(renderer, "/");
            RegisterShell(renderer, "/counter");
            RegisterShell(renderer, "/weather");
            RegisterShell(renderer, "/chat");
            RegisterShell(renderer, "/place");

            // /_chat/img?id=N — serves an uploaded image from ImageStore.
            // Canister_query path so an <img src> roundtrips at ~300 ms
            // (same as /_wasp/render).
            var images = app.Services.GetRequiredService<ImageStore>();
            IcResponseCertV2.RegisterPassThroughPath("/_chat/img", "GET");
            IcServer.RegisterQueryHandler("/_chat/img", (req) =>
            {
                if (req.Method != "GET") return null;
                var idStr = ExtractQueryParam(req.Url, "id");
                if (idStr is null || !long.TryParse(idStr, out var id)) return null;
                if (!images.TryGet(id, out var ct, out var data)) return null;
                return (data, ct);
            });

            // ─── Realtime stores ──────────────────────────────────
            // Tiny typed query endpoints that return only the deltas
            // a client hasn't seen yet. The bridge polls them every
            // few hundred ms via [data-wasp-bind] — feels live, costs
            // almost no bandwidth. Mutations still use existing
            // /_wasp/event POST so reads/writes split cleanly:
            //   read  = query (~300 ms, free)
            //   write = update (~1.5 s, costs cycles)
            var counterSvc = app.Services.GetRequiredService<CounterService>();
            IcResponseCertV2.RegisterPassThroughPath("/api/counter", "GET");
            IcServer.RegisterQueryHandler("/api/counter", (req) =>
            {
                if (req.Method != "GET") return null;
                long since = 0;
                var s = ExtractQueryParam(req.Url, "since");
                if (s is not null) long.TryParse(s, out since);
                var json = counterSvc.DeltaSinceJson(since);
                return (Encoding.UTF8.GetBytes(json), "application/json; charset=utf-8");
            });

            var placeSvc = app.Services.GetRequiredService<PixelService>();
            IcResponseCertV2.RegisterPassThroughPath("/api/place", "GET");
            IcServer.RegisterQueryHandler("/api/place", (req) =>
            {
                if (req.Method != "GET") return null;
                long since = 0;
                var s = ExtractQueryParam(req.Url, "since");
                if (s is not null) long.TryParse(s, out since);
                var json = placeSvc.DeltaSinceJson(since);
                return (Encoding.UTF8.GetBytes(json), "application/json; charset=utf-8");
            });

            // ─── Tetris (Blazor WebAssembly) ──────────────────────
            // Static assets: index.html, app.css, audio.js, and the
            // entire _framework/ tree (the .NET runtime + DLLs). The
            // TetrisWasm publish output is embedded as managed
            // resources via .csproj and registered here on init.
            TetrisAssets.Register();

            // Leaderboard API for the game to talk to. GET on the
            // query path (~300 ms), POST on update (~1.5 s, only fires
            // on game-over).
            var tetris = app.Services.GetRequiredService<TetrisService>();
            IcResponseCertV2.RegisterPassThroughPath("/api/tetris/scores", "GET");
            IcServer.RegisterQueryHandler("/api/tetris/scores", (req) =>
            {
                if (req.Method != "GET") return null;
                int limit = 20;
                var lim = ExtractQueryParam(req.Url, "limit");
                if (lim is not null && int.TryParse(lim, out var n) && n > 0 && n <= 100) limit = n;
                var top = tetris.Top(limit);
                var sb = new StringBuilder();
                sb.Append("{\"scores\":[");
                for (int i = 0; i < top.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var e = top[i];
                    sb.Append("{\"username\":\"").Append(EscapeJson(e.Username))
                      .Append("\",\"score\":").Append(e.Score)
                      .Append(",\"lines\":").Append(e.Lines)
                      .Append(",\"level\":").Append(e.Level)
                      .Append(",\"atMs\":").Append(e.AtMs)
                      .Append('}');
                }
                sb.Append("]}");
                return (Encoding.UTF8.GetBytes(sb.ToString()), "application/json; charset=utf-8");
            });

            app.MapPost("/api/tetris/score", async (HttpContext ctx) =>
            {
                using var body = new System.IO.MemoryStream();
                await ctx.Request.Body.CopyToAsync(body);
                var json = Encoding.UTF8.GetString(body.ToArray());
                var username = ExtractJsonString(json, "username") ?? "Anonymous";
                var scoreStr = ExtractJsonNumber(json, "score");
                var linesStr = ExtractJsonNumber(json, "lines");
                var levelStr = ExtractJsonNumber(json, "level");
                long.TryParse(scoreStr, out var score);
                int.TryParse(linesStr, out var lines);
                int.TryParse(levelStr, out var level);
                var rank = tetris.Submit(username, score, lines, level);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                var resp = "{\"rank\":" + (rank?.ToString() ?? "null") + "}";
                var bytes = Encoding.UTF8.GetBytes(resp);
                ctx.Response.ContentLength = bytes.Length;
                await ctx.Response.Body.WriteAsync(bytes);
            }).DisableAntiforgery();
        }
        catch (Exception ex)
        {
            var msg = ex.GetType().FullName + ": " + ex.Message
                + (ex.StackTrace is { } st ? "\n" + st : "");
            IcServer.InitFailureMessage = msg;
            Reply.Print("[init-fail] " + msg);
        }
    }

    private static string? ExtractQueryParam(string url, string name)
    {
        int q = url.IndexOf('?');
        if (q < 0) return null;
        foreach (var part in url.Substring(q + 1).Split('&'))
        {
            int eq = part.IndexOf('=');
            if (eq < 0) continue;
            if (part.Substring(0, eq) == name)
                return Uri.UnescapeDataString(part.Substring(eq + 1));
        }
        return null;
    }

    private static string? ExtractJsonString(string json, string field)
    {
        var marker = "\"" + field + "\":\"";
        int i = json.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return null;
        i += marker.Length;
        var sb = new StringBuilder();
        while (i < json.Length)
        {
            var c = json[i];
            if (c == '\\' && i + 1 < json.Length)
            {
                var esc = json[i + 1];
                sb.Append(esc == 'n' ? '\n' : esc == 't' ? '\t' : esc);
                i += 2; continue;
            }
            if (c == '"') break;
            sb.Append(c); i++;
        }
        return sb.ToString();
    }

    private static string? ExtractJsonNumber(string json, string field)
    {
        var marker = "\"" + field + "\":";
        int i = json.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return null;
        i += marker.Length;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        int start = i;
        while (i < json.Length && (char.IsDigit(json[i]) || json[i] == '-' || json[i] == '.')) i++;
        return start == i ? null : json.Substring(start, i - start);
    }

    private static string EscapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c == '\\') sb.Append("\\\\");
            else if (c == '"') sb.Append("\\\"");
            else if (c == '\n') sb.Append("\\n");
            else if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
            else sb.Append(c);
        }
        return sb.ToString();
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
        var normalized = currentPath;
        int q = normalized.IndexOf('?');
        if (q >= 0) normalized = normalized.Substring(0, q);
        if (normalized.Length > 1 && normalized.EndsWith("/")) normalized = normalized.Substring(0, normalized.Length - 1);

        string Active(string p) => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase)
            ? " class=\"active\"" : "";
        var sb = new StringBuilder();
        sb.Append("<div class=\"page\">");
        sb.Append("<aside class=\"sidebar\">");
        sb.Append("<a class=\"brand\" href=\"/\">");
        sb.Append("<span class=\"brand-mark\">⚡</span>");
        sb.Append("<span class=\"brand-text\">Blazor on ICP</span>");
        sb.Append("</a>");
        sb.Append("<nav class=\"nav\">");
        sb.Append("<a href=\"/\"").Append(Active("/")).Append("><span class=\"nav-i\">⌂</span>Home</a>");
        sb.Append("<a href=\"/counter\"").Append(Active("/counter")).Append("><span class=\"nav-i\">±</span>Counter</a>");
        sb.Append("<a href=\"/weather\"").Append(Active("/weather")).Append("><span class=\"nav-i\">☂</span>Weather</a>");
        sb.Append("<a href=\"/chat\"").Append(Active("/chat")).Append("><span class=\"nav-i\">#</span>Chat</a>");
        sb.Append("<a href=\"/place\"").Append(Active("/place")).Append("><span class=\"nav-i\">▦</span>Place</a>");
        // Tetris is a Blazor WASM SPA — full-page reload (no SPA nav
        // since it has its own routing/runtime), so the link doesn't
        // include the bridge intercept. The "/tetris" path matches
        // both /tetris and /tetris/ for hover-active feedback.
        var inTetris = normalized.StartsWith("/tetris", StringComparison.OrdinalIgnoreCase);
        // data-wasp-no-spa: Tetris is a Blazor WASM sub-app; let the
        // browser do a full-page load so the WASM bootstrapper runs.
        sb.Append("<a href=\"/tetris\" data-wasp-no-spa")
          .Append(inTetris ? " class=\"active\"" : "")
          .Append("><span class=\"nav-i\">▼</span>Tetris</a>");
        sb.Append("</nav>");
        sb.Append("<div class=\"sidebar-foot\">on-chain · always</div>");
        sb.Append("</aside>");
        // Chat + Place want the full viewport (no padding, no max-width,
        // internal scroll only). Older browsers don't support :has() so
        // a class on <main> is more portable than a :has(.dc-shell) rule.
        var fullbleed = normalized == "/chat" || normalized == "/place"
            ? " class=\"fullbleed\"" : "";
        sb.Append("<main").Append(fullbleed).Append(">").Append(innerHtml).Append("</main>");
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
    <link rel=""preconnect"" href=""https://fonts.googleapis.com"" />
    <link rel=""preconnect"" href=""https://fonts.gstatic.com"" crossorigin />
    <link href=""https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&display=swap"" rel=""stylesheet"" />
    <style>
        :root {
            --bg: #0f1115;
            --bg-elev: #161922;
            --bg-side: linear-gradient(180deg, #0b1d3a 0%, #1d0a3a 100%);
            --text: #e6e8ee;
            --text-dim: #8b93a7;
            --accent: #5b8def;
            --accent-2: #b16cf2;
            --border: rgba(255,255,255,0.07);
            --radius: 12px;
        }
        * { box-sizing: border-box; }
        html, body {
            margin: 0; padding: 0;
            font-family: 'Inter', system-ui, -apple-system, sans-serif;
            color: var(--text); background: var(--bg);
            -webkit-font-smoothing: antialiased;
        }
        .page { display: flex; min-height: 100vh; }

        /* ── Sidebar ──────────────────────────────────────────────── */
        .sidebar {
            width: 240px; flex: 0 0 240px;
            background: var(--bg-side); color: #f8fafc;
            padding: 1.5rem 0 1rem;
            display: flex; flex-direction: column;
            border-right: 1px solid var(--border);
        }
        .brand {
            display: flex; align-items: center; gap: 0.65rem;
            color: #fff; text-decoration: none;
            font-size: 1.1rem; font-weight: 700; letter-spacing: -0.01em;
            padding: 0 1.5rem 1.25rem;
            border-bottom: 1px solid rgba(255,255,255,0.08);
            margin-bottom: 1rem;
        }
        .brand-mark {
            display: inline-grid; place-items: center;
            width: 28px; height: 28px; border-radius: 8px;
            background: linear-gradient(135deg, #5b8def 0%, #b16cf2 100%);
            font-size: 0.95rem;
        }
        .nav { display: flex; flex-direction: column; gap: 2px; padding: 0 0.5rem; }
        .nav a {
            display: flex; align-items: center; gap: 0.7rem;
            color: #c8d0e0; text-decoration: none;
            padding: 0.55rem 0.85rem;
            border-radius: 8px; font-size: 0.95rem; font-weight: 500;
            transition: background 0.12s ease, color 0.12s ease;
        }
        .nav a:hover { background: rgba(255,255,255,0.06); color: #fff; }
        .nav a.active {
            background: linear-gradient(90deg, rgba(91,141,239,0.25), rgba(177,108,242,0.15));
            color: #fff;
            box-shadow: inset 0 0 0 1px rgba(91,141,239,0.35);
        }
        .nav-i {
            display: inline-grid; place-items: center;
            width: 22px; height: 22px;
            color: var(--text-dim);
            font-size: 1rem;
        }
        .nav a.active .nav-i { color: #fff; }
        .sidebar-foot {
            margin-top: auto; padding: 1rem 1.5rem 0;
            color: rgba(255,255,255,0.35); font-size: 0.75rem;
            letter-spacing: 0.04em; text-transform: uppercase;
        }

        /* ── Main / generic page chrome ───────────────────────────── */
        main {
            flex: 1 1 auto; min-width: 0;
            padding: 2.5rem 3rem; max-width: 980px;
        }
        main h1 {
            color: #fff; margin: 0 0 0.5rem;
            font-size: 1.75rem; letter-spacing: -0.015em;
        }
        main p { color: var(--text-dim); line-height: 1.6; }
        main code {
            background: var(--bg-elev); color: #e6e8ee;
            padding: 0.15rem 0.4rem; border-radius: 4px;
            font-size: 0.88em; font-family: 'JetBrains Mono', ui-monospace, SFMono-Regular, monospace;
        }
        button.btn-primary, .btn-primary {
            background: linear-gradient(135deg, #5b8def 0%, #4a76d9 100%);
            color: #fff; border: 0;
            padding: 0.55rem 1.1rem; border-radius: 8px;
            font-size: 0.95rem; font-weight: 500; cursor: pointer;
            transition: filter 0.12s ease, transform 0.05s ease, opacity 0.12s ease;
        }
        button.btn-primary:hover:not(:disabled) { filter: brightness(1.08); }
        button.btn-primary:active:not(:disabled) { transform: translateY(1px); }
        /* While the click POST is mid-consensus the bridge sets
           disabled=true. Style it so the user sees the in-flight
           state instead of an apparently clickable button. */
        button.btn-primary:disabled {
            cursor: progress;
            opacity: 0.6;
            background: linear-gradient(135deg, #5b8def 0%, #4a76d9 100%);
            filter: none;
            position: relative;
        }
        button.btn-primary:disabled::after {
            content: "";
            display: inline-block;
            margin-left: 0.55rem;
            width: 0.85rem; height: 0.85rem;
            border: 2px solid rgba(255,255,255,0.4);
            border-top-color: #fff;
            border-radius: 50%;
            vertical-align: -0.15rem;
            animation: wasp-spin 0.7s linear infinite;
        }
        @keyframes wasp-spin {
            from { transform: rotate(0deg); }
            to   { transform: rotate(360deg); }
        }
        .table { border-collapse: collapse; width: 100%; margin-top: 1rem; }
        .table th, .table td {
            padding: 0.65rem 0.5rem;
            border-bottom: 1px solid var(--border); text-align: left;
        }
        .table th { background: var(--bg-elev); color: var(--text-dim); font-weight: 600; font-size: 0.85rem; }
        p[role=""status""] { font-size: 1.15rem; color: #fff; }

        /* ── Discord-style chat (multi-room) ──────────────────────── */
        main.fullbleed { padding: 0; max-width: none; height: 100vh; overflow: hidden; }
        .dc-shell {
            display: grid;
            grid-template-columns: 240px 1fr;
            grid-template-rows: 100%;   /* pin the single row to grid height so children
                                           don't stretch past the viewport */
            height: 100%;
            background: #313338; color: #dcddde;
            font: 15px/1.45 'Inter', system-ui, sans-serif;
        }
        .dc-rooms, .dc-channel { min-height: 0; }   /* allow shrink-below-content */
        .dc-rooms {
            background: #2b2d31; color: #c9cbd4;
            display: flex; flex-direction: column;
            border-right: 1px solid rgba(0,0,0,0.25);
            overflow: hidden;
        }
        .dc-server {
            padding: 1rem 1rem 0.85rem;
            border-bottom: 1px solid rgba(0,0,0,0.3);
            box-shadow: 0 1px 0 rgba(255,255,255,0.04);
        }
        .dc-server-name { font-weight: 700; color: #fff; font-size: 1.0rem; }
        .dc-server-sub  { color: #80848e; font-size: 0.72rem; margin-top: 2px; letter-spacing: 0.02em; }
        .dc-rooms-header {
            color: #949ba4; font-size: 0.72rem; font-weight: 700;
            text-transform: uppercase; letter-spacing: 0.04em;
            padding: 1rem 1rem 0.4rem;
        }
        .dc-rooms-list { list-style: none; margin: 0; padding: 0 0.5rem; flex: 0 1 auto; overflow-y: auto; }
        .dc-room {
            display: flex; align-items: center; gap: 0.35rem;
            padding: 0.4rem 0.6rem;
            border-radius: 4px; color: #949ba4; text-decoration: none;
            font-size: 0.95rem; font-weight: 500;
            transition: background 0.1s ease, color 0.1s ease;
        }
        .dc-room:hover { background: rgba(255,255,255,0.04); color: #dbdee1; }
        .dc-room.active { background: rgba(255,255,255,0.08); color: #fff; }
        .dc-room .dc-hash { color: #80848e; font-weight: 400; font-size: 1.1rem; }
        .dc-room.active .dc-hash { color: #b5bac1; }
        .dc-room-name { font-weight: 500; }

        .dc-room-add {
            display: grid; grid-template-columns: 1fr auto; gap: 0.4rem;
            padding: 0.75rem 1rem 0.5rem;
            background: transparent; border-top: 1px solid rgba(0,0,0,0.2); margin-top: 0.5rem;
        }
        .dc-input {
            background: #1e1f22; color: #fff; border: 1px solid rgba(255,255,255,0.04);
            outline: none; padding: 0.55rem 0.65rem; border-radius: 6px;
            font: inherit; font-size: 0.9rem;
            transition: border-color 0.12s ease;
        }
        .dc-input::placeholder { color: #6b6f76; }
        .dc-input:focus { border-color: rgba(88,101,242,0.6); }
        .dc-btn-add {
            background: #4e5058; color: #fff; border: 0; border-radius: 6px;
            cursor: pointer; padding: 0 0.75rem; font-size: 1.1rem; line-height: 1;
            transition: background 0.12s ease;
        }
        .dc-btn-add:hover { background: #5865f2; }

        .dc-user-card {
            margin-top: auto; padding: 0.75rem 1rem;
            background: #232428;
            display: grid; grid-template-columns: 1fr auto; gap: 0.5rem;
            align-items: center;
        }
        .dc-username-card { font-size: 0.9rem; }
        .dc-user-dot {
            width: 10px; height: 10px; border-radius: 50%;
            background: #23a55a; box-shadow: 0 0 0 2px #232428;
        }

        .dc-channel { display: flex; flex-direction: column; min-width: 0; background: #313338; }
        .dc-channel-header {
            flex: 0 0 auto; height: 48px;
            display: flex; align-items: center; justify-content: space-between;
            padding: 0 1.25rem; background: #313338; color: #f2f3f5;
            border-bottom: 1px solid rgba(0,0,0,0.2);
            box-shadow: 0 1px 0 rgba(0,0,0,0.2);
        }
        .dc-channel-title { display: flex; align-items: center; gap: 0.3rem; font-weight: 700; font-size: 1rem; }
        .dc-hash { color: #80848e; font-weight: 500; font-size: 1.4rem; }
        .dc-channel-tag { color: #80848e; font-size: 0.8rem; }
        .dc-messages {
            flex: 1 1 auto; min-height: 0; overflow-y: auto;
            padding: 1rem 0;
            /* No scroll-behavior: smooth — we manage scrollTop
               explicitly after each render-batch, and smooth would
               animate a visible top→bottom scroll on every poll. */
        }
        .dc-messages::-webkit-scrollbar { width: 14px; }
        .dc-messages::-webkit-scrollbar-track { background: #2b2d31; }
        .dc-messages::-webkit-scrollbar-thumb {
            background: #1a1b1e; border: 4px solid #2b2d31;
            border-radius: 8px; min-height: 40px;
        }
        .dc-empty {
            padding: 3rem 1.5rem 1rem; color: #b5bac1;
            display: flex; flex-direction: column; gap: 0.5rem;
        }
        .dc-empty-mark {
            width: 72px; height: 72px; border-radius: 50%;
            background: #4e5058; color: #fff; font-size: 2.5rem; font-weight: 600;
            display: grid; place-items: center;
            margin-bottom: 0.5rem;
        }
        .dc-empty h2 { color: #fff; margin: 0; font-size: 1.65rem; font-weight: 700; }
        .dc-empty p { margin: 0; color: #b5bac1; }
        .dc-divider {
            text-align: center; margin: 1rem 1rem 0.5rem;
            font-size: 0.72rem; color: #949ba4; font-weight: 700;
            border-top: 1px solid rgba(255,255,255,0.06);
            position: relative;
        }
        .dc-divider span { background: #313338; padding: 0 0.6rem; position: relative; top: -0.6rem; }
        .dc-message {
            display: grid; grid-template-columns: 60px 1fr;
            padding: 0.15rem 1rem 0.15rem 0; margin-top: 1.1rem;
        }
        .dc-message:hover { background: rgba(4,4,5,0.07); }
        .dc-message-grouped { margin-top: 0; padding-top: 0.1rem; }
        .dc-avatar {
            grid-column: 1; justify-self: center; align-self: start;
            width: 42px; height: 42px; border-radius: 50%;
            display: flex; align-items: center; justify-content: center;
            color: #fff; font-weight: 700; font-size: 1.1rem;
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
        .dc-username { font-weight: 700; font-size: 1rem; }
        .dc-time { color: #949ba4; font-size: 0.75rem; }
        .dc-text {
            color: #dbdee1; font-size: 1rem; line-height: 1.45;
            white-space: pre-wrap; word-wrap: break-word;
        }
        .dc-composer {
            flex: 0 0 auto; padding: 0 1.25rem 1.5rem; background: #313338;
            display: grid;
            grid-template-columns: 1fr auto;
            grid-template-rows: auto auto;
            gap: 0.5rem; align-items: stretch;
        }
        .dc-emoji-bar {
            grid-column: 1 / -1;
            display: flex; gap: 0.3rem; align-items: center;
            padding-bottom: 0.1rem;
        }
        .dc-emoji-btn {
            background: transparent; border: 1px solid transparent;
            cursor: pointer; padding: 0.2rem 0.45rem; font-size: 1.15rem;
            line-height: 1; border-radius: 8px;
            transition: background 0.1s ease, transform 0.05s ease, border-color 0.1s ease;
        }
        .dc-emoji-btn:hover { background: #383a40; border-color: rgba(255,255,255,0.06); }
        .dc-emoji-btn:active { transform: scale(0.92); }
        /* Inline reaction badges — sit in the message head row beside
           the timestamp, only rendered for emojis with count > 0. */
        .dc-message { position: relative; }
        .dc-message-head .dc-reactions {
            margin-left: 0.4rem;
            display: inline-flex; align-items: center;
            gap: 0.25rem; flex-wrap: wrap;
        }
        .dc-react-badge {
            display: inline-flex; align-items: center; gap: 0.25rem;
            background: rgba(88,101,242,0.18);
            border: 1px solid rgba(88,101,242,0.45);
            color: #c7d2fe;
            padding: 0.05rem 0.45rem;
            border-radius: 999px;
            font-size: 0.78rem; font-weight: 600;
            cursor: pointer; line-height: 1.4;
            transition: background 0.1s, border-color 0.1s, transform 0.05s;
        }
        .dc-react-badge:hover {
            background: rgba(88,101,242,0.32);
            border-color: rgba(88,101,242,0.7);
        }
        .dc-react-badge:active { transform: scale(0.94); }
        .dc-react-badge .dc-react-e { font-size: 0.9rem; line-height: 1; }
        .dc-react-badge .dc-react-n { font-variant-numeric: tabular-nums; }

        /* Per-message actions — inline at the end of the head row,
           right after the timestamp + reactions. Hidden until the
           message is hovered or focused. For grouped messages
           (dc-message-head-compact) the head row exists only to host
           the actions, so it has no min-height. */
        .dc-actions {
            /* Sit inline immediately after the timestamp — no
               margin-left:auto, so it doesn't get pushed to the far
               right of the row. */
            margin-left: 0.4rem;
            position: relative;            /* anchor for the popover */
            display: inline-flex;
            align-items: center; gap: 1px;
            background: #2b2d31;
            border: 1px solid rgba(255,255,255,0.08);
            border-radius: 6px;
            padding: 1px;
            box-shadow: 0 2px 8px rgba(0,0,0,0.25);
            opacity: 0; pointer-events: none;
            transition: opacity 0.12s ease;
            flex: 0 0 auto;
        }
        .dc-message:hover .dc-actions,
        .dc-message:focus-within .dc-actions {
            opacity: 1; pointer-events: auto;
        }
        .dc-message-head-compact {
            min-height: 0; margin-bottom: 0;
            /* Grouped (no username/time) — actions land at the left
               of the head row, vertically aligned with where the
               username would be on the previous non-grouped row. */
        }
        .dc-message-grouped .dc-message-head-compact {
            margin-top: 0;
        }
        .dc-action {
            background: transparent; border: 0; cursor: pointer;
            padding: 0.2rem 0.4rem;
            border-radius: 4px;
            font-size: 0.95rem; line-height: 1;
            color: #b5bac1;
            text-decoration: none;
            transition: background 0.1s, color 0.1s;
        }
        .dc-action:hover { background: rgba(255,255,255,0.08); color: #fff; }
        .dc-action:active { transform: scale(0.94); }

        /* Generic popover — toggled to display:flex when JS adds
           [data-open]. Drops below the trigger pill. */
        .dc-popover {
            position: absolute;
            top: calc(100% + 4px); right: 0;
            display: none;
            background: #2b2d31;
            border: 1px solid rgba(255,255,255,0.08);
            border-radius: 8px;
            padding: 3px 4px;
            box-shadow: 0 6px 18px rgba(0,0,0,0.4);
            z-index: 10;
        }
        .dc-popover[data-open] { display: flex; gap: 1px; }
        .dc-react-pick {
            background: transparent; border: 0; cursor: pointer;
            padding: 0.3rem 0.42rem;
            border-radius: 5px;
            font-size: 1.05rem; line-height: 1;
            transition: background 0.1s, transform 0.05s;
        }
        .dc-react-pick:hover {
            background: rgba(255,255,255,0.1);
            transform: scale(1.15);
        }
        .dc-react-pick:active { transform: scale(0.95); }

        /* Discord-style reply quote shown above a reply's text. */
        .dc-reply-quote {
            display: flex; align-items: center; gap: 0.35rem;
            font-size: 0.8rem; color: #b5bac1;
            margin-bottom: 0.25rem;
            padding-left: 0.4rem;
            border-left: 2px solid rgba(255,255,255,0.15);
            line-height: 1.3;
            overflow: hidden;
        }
        .dc-reply-quote-arrow { color: #80848e; flex: 0 0 auto; }
        .dc-reply-quote-user { font-weight: 600; flex: 0 0 auto; }
        .dc-reply-quote-text {
            color: #b5bac1;
            white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
            min-width: 0;
        }

        /* Image attachments inside messages. */
        .dc-attach {
            display: inline-block; margin-top: 0.5rem;
            border-radius: 8px; overflow: hidden;
            max-width: min(420px, 100%);
        }
        .dc-attach img {
            display: block; width: 100%; height: auto; max-height: 360px;
            object-fit: contain; background: #1e1f22;
        }

        /* Reply badge above the composer textarea. Hidden by default;
           the bridge toggles .is-active when the user clicks a reply
           button on a message. */
        .dc-reply-badge {
            display: none;
            align-items: center; gap: 0.5rem;
            background: #383a40; color: #dbdee1;
            border-radius: 8px 8px 0 0;
            padding: 0.4rem 0.7rem;
            font-size: 0.85rem;
            margin: 0 0 0.2rem;
            grid-column: 1 / -1;
        }
        .dc-reply-badge.is-active { display: flex; }
        .dc-reply-icon { color: #80848e; }
        .dc-reply-summary { flex: 1 1 auto; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }
        .dc-reply-user { font-weight: 600; color: #fff; }
        .dc-reply-text { color: #b5bac1; margin-left: 0.4rem; }
        .dc-reply-cancel {
            background: transparent; border: 0; color: #b5bac1;
            font-size: 1.15rem; cursor: pointer; line-height: 1;
            padding: 0 0.25rem;
        }
        .dc-reply-cancel:hover { color: #fff; }

        /* Image attachment preview shown above the textarea before send. */
        .dc-attach-preview {
            display: none;
            position: relative;
            padding: 0.5rem 0;
            grid-column: 1 / -1;
        }
        .dc-attach-preview.is-active { display: block; }
        .dc-attach-preview img {
            max-height: 140px; max-width: 100%;
            display: block; border-radius: 8px;
            background: #1e1f22;
        }
        .dc-attach-clear {
            position: absolute; top: 0.85rem; left: 0.35rem;
            background: rgba(0,0,0,0.7); color: #fff;
            border: 0; cursor: pointer;
            width: 22px; height: 22px; border-radius: 50%;
            font-size: 0.95rem; line-height: 1;
            display: flex; align-items: center; justify-content: center;
        }
        .dc-attach-clear:hover { background: rgba(0,0,0,0.9); }

        /* The composer file-input label (paperclip). */
        .dc-attach-btn {
            display: inline-flex; align-items: center; justify-content: center;
            cursor: pointer;
        }
        .dc-composer-input {
            background: #383a40; color: #dcddde; border: 0; outline: none; resize: none;
            padding: 0.8rem 1rem; border-radius: 10px; min-height: 46px; max-height: 50vh;
            font: inherit; font-size: 1rem; line-height: 1.4;
        }
        .dc-composer-input::placeholder { color: #80848e; }
        .dc-send {
            background: #5865f2; color: #fff; border: 0;
            padding: 0 1.15rem; border-radius: 10px; cursor: pointer;
            display: flex; align-items: center; justify-content: center;
            transition: background 0.12s ease, transform 0.05s ease;
        }
        .dc-send:hover { background: #4752c4; }
        .dc-send:active { transform: scale(0.96); }
        .dc-send:disabled { background: #4e5058; cursor: not-allowed; opacity: 0.6; }

        @media (max-width: 720px) {
            /* Chat: rooms become a horizontal scroller above the channel */
            .dc-shell {
                grid-template-columns: 1fr;
                grid-template-rows: auto 1fr;
                height: calc(100dvh - var(--top-nav-h, 56px));
            }
            .dc-rooms {
                flex-direction: row; align-items: stretch;
                padding: 0 0.5rem;
                border-right: 0; border-bottom: 1px solid rgba(0,0,0,0.35);
                overflow-x: auto; flex: 0 0 auto;
            }
            .dc-server, .dc-rooms-header, .dc-server-sub,
            .dc-room-add, .dc-user-card { display: none; }
            .dc-rooms-list {
                display: flex; flex-direction: row; flex: 1 1 auto;
                gap: 0.3rem; padding: 0.5rem 0; margin: 0;
                overflow: visible;
            }
            .dc-rooms-list li { flex: 0 0 auto; list-style: none; }
            .dc-room {
                padding: 0.4rem 0.75rem; border-radius: 999px;
                background: rgba(255,255,255,0.04);
                white-space: nowrap;
            }
            .dc-room.active {
                background: rgba(88,101,242,0.25);
                color: #fff;
            }
            .dc-channel { min-height: 0; }
            .dc-channel-header { padding: 0 0.85rem; height: 44px; }
            .dc-messages { padding: 0.5rem 0; }
            .dc-message { grid-template-columns: 48px 1fr; padding: 0.1rem 0.75rem 0.1rem 0; }
            .dc-avatar { width: 36px; height: 36px; font-size: 1rem; }
            .dc-composer { padding: 0 0.75rem 1rem; }
            .dc-emoji-bar { padding-top: 0.4rem; flex-wrap: wrap; }
            .dc-emoji-btn { padding: 0.25rem 0.4rem; font-size: 1.05rem; }
            .dc-composer-input { padding: 0.65rem 0.85rem; min-height: 42px; }
            .dc-send { padding: 0 0.9rem; }
        }

        /* ── Pixel canvas ─────────────────────────────────────────── */
        .px-shell {
            height: 100%; overflow-y: auto;
            background: #0d0f14;
            color: #e6e8ee;
            display: flex; flex-direction: column;
            padding: 2rem 2.5rem;
            gap: 1.5rem;
        }
        .px-header {
            display: flex; align-items: flex-end; justify-content: space-between;
            gap: 1.5rem; flex-wrap: wrap;
        }
        .px-title h1 { color: #fff; margin: 0 0 0.3rem; font-size: 1.75rem; letter-spacing: -0.015em; }
        .px-title p { color: var(--text-dim); margin: 0; font-size: 0.95rem; }
        .px-stats { display: flex; gap: 1.25rem; }
        .px-stat {
            background: var(--bg-elev);
            padding: 0.65rem 1rem; border-radius: 10px;
            border: 1px solid var(--border);
            display: flex; flex-direction: column; align-items: flex-start;
            min-width: 88px;
        }
        .px-stat-num { font-size: 1.25rem; font-weight: 700; color: #fff; font-variant-numeric: tabular-nums; }
        .px-stat-label { color: var(--text-dim); font-size: 0.72rem; text-transform: uppercase; letter-spacing: 0.05em; margin-top: 2px; }

        .px-board {
            flex: 1 1 auto;
            display: flex; align-items: center; justify-content: center;
            background: linear-gradient(180deg, #0a0c11, #11141b);
            border-radius: 14px; border: 1px solid var(--border);
            padding: 1.25rem;
            min-height: 0;
        }
        .px-grid {
            display: grid;
            gap: 0;
            background: #fff;
            border-radius: 6px; overflow: hidden;
            box-shadow: 0 30px 80px -30px rgba(0,0,0,0.7), 0 0 0 1px rgba(255,255,255,0.04);
            image-rendering: pixelated;
            cursor: crosshair;
            user-select: none;
            /* clamp size so very wide canvases stay on-screen */
            max-width: min(72vh, 880px);
            width: 100%;
            aspect-ratio: 1;
        }
        .px-cell {
            transition: transform 0.06s ease, outline 0.06s ease;
            outline: 0 solid rgba(0,0,0,0);
        }
        .px-cell:hover {
            outline: 2px solid rgba(91,141,239,0.95);
            outline-offset: -2px;
            z-index: 2; position: relative;
        }

        .px-toolbar {
            display: grid;
            grid-template-columns: auto 200px 1fr auto;
            gap: 1rem; align-items: center;
            background: var(--bg-elev);
            border: 1px solid var(--border);
            border-radius: 14px;
            padding: 0.9rem 1rem;
        }

        /* Cooldown status indicator — green plus when click is ready,
           red countdown while we are within the per-user cooldown
           window. Updated client-side by the bridge every 100 ms. */
        .wasp-status {
            display: inline-flex; align-items: center; justify-content: center;
            min-width: 42px; height: 42px; padding: 0 0.7rem;
            border-radius: 10px;
            font-weight: 700; font-size: 1.05rem;
            font-variant-numeric: tabular-nums;
            transition: background 0.15s, color 0.15s, border-color 0.15s;
            user-select: none;
        }
        .wasp-status-ready {
            background: rgba(46, 204, 113, 0.18);
            color: #2ecc71;
            border: 1px solid rgba(46, 204, 113, 0.45);
        }
        .wasp-status-cooling {
            background: rgba(231, 76, 60, 0.18);
            color: #ff6b5d;
            border: 1px solid rgba(231, 76, 60, 0.5);
        }
        .px-username {
            background: #1e2129; color: #fff;
            border: 1px solid var(--border); border-radius: 10px;
            outline: none; padding: 0.7rem 0.85rem;
            font: inherit; font-size: 0.95rem;
            transition: border-color 0.12s ease;
        }
        .px-username:focus { border-color: var(--accent); }
        .px-username::placeholder { color: var(--text-dim); }
        .px-palette {
            display: grid; grid-auto-flow: column; gap: 0.4rem;
            justify-content: center;
        }
        .px-swatch {
            display: inline-block; width: 30px; height: 30px;
            border-radius: 8px;
            box-shadow: inset 0 0 0 1px rgba(255,255,255,0.07);
            transition: transform 0.08s ease, box-shadow 0.08s ease;
        }
        .px-swatch:hover { transform: translateY(-2px) scale(1.08); }
        .px-swatch.active {
            transform: translateY(-3px) scale(1.18);
            box-shadow:
              inset 0 0 0 1px rgba(255,255,255,0.15),
              0 0 0 2px var(--bg-elev),
              0 0 0 4px var(--accent);
        }
        .px-selected {
            display: flex; align-items: center; gap: 0.5rem;
            color: var(--text-dim); font-size: 0.85rem;
        }
        .px-selected-label { text-transform: uppercase; letter-spacing: 0.05em; font-size: 0.7rem; }
        .px-selected-swatch {
            display: inline-block; width: 28px; height: 28px; border-radius: 8px;
            box-shadow: inset 0 0 0 1px rgba(255,255,255,0.08), 0 0 0 1px rgba(91,141,239,0.6);
        }
        .px-tip { color: var(--text-dim); font-size: 0.85rem; margin: 0; text-align: center; }

        @media (max-width: 720px) {
            /* Pixel canvas: full viewport width, palette in 8-col grid */
            .px-shell {
                padding: 0.75rem 0.75rem 1rem; gap: 0.75rem;
                min-height: calc(100dvh - var(--top-nav-h, 56px));
            }
            .px-header { gap: 0.75rem; }
            .px-title h1 { font-size: 1.3rem; }
            .px-title p { font-size: 0.85rem; }
            .px-stats { gap: 0.5rem; }
            .px-stat { padding: 0.45rem 0.7rem; min-width: 0; }
            .px-stat-num { font-size: 1.05rem; }
            .px-stat-label { font-size: 0.65rem; }
            .px-board { padding: 0.5rem; border-radius: 10px; }
            .px-grid { max-width: 96vw; }
            .px-toolbar {
                grid-template-columns: 1fr;
                gap: 0.65rem; padding: 0.75rem;
            }
            .px-palette {
                grid-auto-flow: row;
                grid-template-columns: repeat(8, minmax(0,1fr));
                gap: 0.35rem; justify-content: stretch;
            }
            .px-swatch { width: auto; height: 36px; }
            .px-username { padding: 0.6rem 0.75rem; }
            .px-selected { display: none; }
            .px-tip { font-size: 0.78rem; }
        }

        /* ── Mobile: outer sidebar becomes a top nav bar ────────────── */
        @media (max-width: 720px) {
            :root { --top-nav-h: 56px; }
            .page { flex-direction: column; }
            .sidebar {
                width: 100%; flex: 0 0 var(--top-nav-h);
                padding: 0;
                flex-direction: row; align-items: stretch;
                border-right: 0; border-bottom: 1px solid var(--border);
                position: sticky; top: 0; z-index: 50;
            }
            .brand {
                padding: 0 0.85rem; margin: 0; border-bottom: 0;
                font-size: 0.9rem; gap: 0.5rem;
                display: flex; align-items: center;
            }
            .brand-mark { width: 24px; height: 24px; font-size: 0.85rem; }
            .brand-text { display: none; }
            .nav {
                flex: 1 1 auto; flex-direction: row;
                gap: 0; padding: 0;
                overflow-x: auto; align-items: stretch;
            }
            .nav a {
                flex: 1 1 0; min-width: 56px;
                flex-direction: column; gap: 2px;
                padding: 0.35rem 0.25rem;
                font-size: 0.68rem; font-weight: 600;
                border-radius: 0; text-align: center;
                justify-content: center;
            }
            .nav a.active {
                background: linear-gradient(180deg, rgba(91,141,239,0.18), rgba(177,108,242,0.1));
                box-shadow: inset 0 -2px 0 var(--accent);
            }
            .nav-i { font-size: 1rem; width: auto; height: auto; }
            .sidebar-foot { display: none; }

            main { padding: 1.25rem; }
            main h1 { font-size: 1.4rem; }
            main.fullbleed { height: calc(100dvh - var(--top-nav-h)); }
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
