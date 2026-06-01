using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
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
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Forum))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Feed))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(CounterService))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(WeatherService))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ChatService))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(PixelService))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ImageStore))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(TetrisService))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(TetrisAssets))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(CrmService))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(CrmAssets))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(PresenceService))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(StableExplorerPage))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(StableExplorer))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(AdminService))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ServerService))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(DmService))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ThreadService))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(RoleService))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ModService))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(MembershipService))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ServerKindService))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ForumService))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(FollowService))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(RepostService))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(UserFollowService))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(RepostTimeService))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(VoteService))]
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
            builder.Services.AddSingleton<CrmService>();
            builder.Services.AddSingleton<PresenceService>();
            builder.Services.AddSingleton<AdminService>();
            builder.Services.AddSingleton<IdentityService>();
            builder.Services.AddSingleton<ServerService>();   // depends on ChatService (registered above)
            builder.Services.AddSingleton<DmService>();
            builder.Services.AddSingleton<ThreadService>();
            builder.Services.AddSingleton<RoleService>();     // M2 per-server role grants (region 32M)
            builder.Services.AddSingleton<ModService>();      // M2-B pins + channel locks (region 48M)
            builder.Services.AddSingleton<MembershipService>();// B4 per-server visibility + members (region 64M)
            builder.Services.AddSingleton<ServerKindService>();// F0 per-server kind discussion/forum/feed (region 80M)
            builder.Services.AddSingleton<ForumService>();     // F1 forum topics: title + accepted-answer (region 81M)
            builder.Services.AddSingleton<FollowService>();    // F2 feed follows (region 90M)
            builder.Services.AddSingleton<RepostService>();    // F2 feed reposts (region 95M)
            builder.Services.AddSingleton<UserFollowService>(); // F2-A user follows (region 100M)
            builder.Services.AddSingleton<RepostTimeService>(); // F2-A repost timestamps (region 110M)
            builder.Services.AddSingleton<VoteService>();       // identity-weighted voting (region 120M)
            builder.Services.AddSingleton<IWaspRenderer>(sp =>
            {
                var r = new WaspRouter(sp);
                r.AddRoute<Home>("/");
                r.AddRoute<Counter>("/counter");
                r.AddRoute<Weather>("/weather");
                r.AddRoute<Chat>("/chat");
                r.AddRoute<Place>("/place");
                r.AddRoute<StableExplorerPage>("/stable");
                r.AddRoute<Forum>("/forum");
                r.AddRoute<Feed>("/feed");
                r.WrapShell((path, inner) => WrapWithSidebar(path, inner,
                    sp.GetRequiredService<ServerService>(),
                    sp.GetRequiredService<ServerKindService>(),
                    sp.GetRequiredService<MembershipService>()));
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
            RegisterShell(renderer, "/stable");
            RegisterShell(renderer, "/forum");
            RegisterShell(renderer, "/feed");

            // Register stable-memory collections with the explorer.
            // Counter, ChatService rooms+messages, ImageStore are the
            // services that actually own stable-memory regions; the
            // explorer's tree walks this registry.
            StableExplorer.Register(new CounterCollection(
                app.Services.GetRequiredService<CounterService>()));
            var chat = app.Services.GetRequiredService<ChatService>();
            StableExplorer.Register(new ChatRoomsCollection(chat));
            // ChatMessagesCollection registered below, AFTER servers+membership
            // resolve, so it can exclude private-server messages from the explorer.
            StableExplorer.Register(new ImageStoreCollection(
                app.Services.GetRequiredService<ImageStore>()));

            // /_chat/img?id=N — serves an uploaded image from ImageStore.
            // Canister_query path so an <img src> roundtrips at ~300 ms
            // (same as /_wasp/render).
            var images = app.Services.GetRequiredService<ImageStore>();
            // ── Auth/role/mod services resolved early so every endpoint below can
            //    gate on them (signed msg_caller via AdminService). ──
            var servers = app.Services.GetRequiredService<ServerService>();
            var dms = app.Services.GetRequiredService<DmService>();
            var admins = app.Services.GetRequiredService<AdminService>();
            var roles = app.Services.GetRequiredService<RoleService>();   // M2 per-server roles
            var mod = app.Services.GetRequiredService<ModService>();      // M2-B pins/locks
            var threads = app.Services.GetRequiredService<ThreadService>();// M2-B signed thread replies
            var membership = app.Services.GetRequiredService<MembershipService>();// B4 visibility + members
            var kinds = app.Services.GetRequiredService<ServerKindService>();   // F0 per-server kind
            var forum = app.Services.GetRequiredService<ForumService>();        // F1 forum topics
            var follows = app.Services.GetRequiredService<FollowService>();      // F2 feed follows
            var reposts = app.Services.GetRequiredService<RepostService>();      // F2 feed reposts
            var userFollows = app.Services.GetRequiredService<UserFollowService>(); // F2-A user follows
            var repostTimes = app.Services.GetRequiredService<RepostTimeService>(); // F2-A repost timestamps
            var votes = app.Services.GetRequiredService<VoteService>();           // identity-weighted voting
            var identity = app.Services.GetRequiredService<IdentityService>();
            // B4: explorer excludes private-server messages (anonymous /stable page).
            StableExplorer.Register(new ChatMessagesCollection(chat, servers, membership));
            // Super-admin = explicit AdminService allowlist OR the canister
            // controller (the dfx identity that installed it) — implicitly super so
            // there's no bootstrap chicken-and-egg and the operator can seed admins.
            Func<byte[], bool> isSuper = c => admins.IsAdmin(c) || AdminService.IsCurrentCallerController();
            // Can the signed caller moderate this channel? Resolve the channel's
            // owning server, require Moderator+ there (super-admin anywhere). The
            // virtual default server 0 has no per-server roles → super-admin only.
            Func<byte[], int, bool> canModerate = (c, roomId) =>
            {
                var srv = servers.ServerOfChannel(roomId);
                return srv == ServerService.DefaultServerId
                    ? isSuper(c)
                    : (roles.RoleOf(srv, c) >= ServerRole.Moderator || isSuper(c));
            };
            // Server-level moderation gate (for mutes, which are per-server not per-room).
            Func<byte[], int, bool> canModerateServer = (c, sid) =>
                sid == ServerService.DefaultServerId ? isSuper(c) : (roles.RoleOf(sid, c) >= ServerRole.Moderator || isSuper(c));
            // Canister time in ms (same formula the services use).
            Func<long> nowMs = () => (long)(Ic0.time() / 1_000_000UL);
            // B4 read/post access to a (possibly private) server: public servers
            // (incl. the virtual default 0) are open; a private server admits only
            // explicit members + its role-holders (mod/admin/owner) + super-admin.
            // Single source of truth shared by the signed read endpoint and the
            // write gates, so "who can read" == "who can write" for a private server.
            Func<int, byte[], bool> canAccess = (sid, c) =>
                !membership.IsPrivate(sid)
                    ? true
                    : (membership.IsMember(sid, c) || roles.RoleOf(sid, c) >= ServerRole.Moderator || isSuper(c));
            // F2b: render + register a forum topic's standalone CERTIFIED page at /t/{roomId}
            // so a hard-reload / crawler gets a real server-rendered page (the full-page GET is
            // otherwise query-agnostic). renderer.Render("/forum?s=&t=") returns the topic CONTENT
            // for a PUBLIC forum or the members-only GATE for a PRIVATE one (Forum.razor branches
            // on IsPrivate) — so a private topic's content is NEVER certified. On a public→private
            // flip the page is re-certified to the gate; private→public re-certifies to content.
            Action<int> certifyTopic = (roomId) =>
            {
                try
                {
                    var sid = servers.ServerOfChannel(roomId);
                    var batch = renderer.Render(new WaspRenderRequest { Path = "/forum?s=" + sid + "&t=" + roomId });
                    var bytes = Encoding.UTF8.GetBytes(BuildPage(batch.Html));
                    IcServer.RegisterStaticAsset("/t/" + roomId, bytes, "text/html; charset=utf-8");
                    IcCertifiedAssets.Insert("/t/" + roomId, bytes);
                }
                catch (Exception ex) { try { Reply.Print("[certify-topic] " + ex.Message); } catch { } }
            };
            // Sitemap of PUBLIC forum topics for crawlers (private forums excluded).
            IcResponseCertV2.RegisterPassThroughPath("/sitemap.xml", "GET");
            IcServer.RegisterQueryHandler("/sitemap.xml", (req) =>
            {
                if (req.Method != "GET") return null;
                var baseUrl = "https://" + AdminService.CanisterIdText() + ".icp0.io";
                var sbm = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">");
                foreach (var srv in servers.ListServers())
                {
                    if (kinds.KindOf(srv.Id) != ServerKind.Forum || membership.IsPrivate(srv.Id)) continue;
                    foreach (var rid in servers.ChannelsOf(srv.Id))
                        if (forum.IsTopic(rid)) sbm.Append("<url><loc>").Append(baseUrl).Append("/t/").Append(rid).Append("</loc></url>");
                }
                sbm.Append("</urlset>");
                return (Encoding.UTF8.GetBytes(sbm.ToString()), "application/xml; charset=utf-8");
            });
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

            // ─── CRM (Blazor WebAssembly) ─────────────────────────
            CrmAssets.Register();
            var crm = app.Services.GetRequiredService<CrmService>();
            var presence = app.Services.GetRequiredService<PresenceService>();

            IcResponseCertV2.RegisterPassThroughPath("/api/crm/contacts", "GET");
            IcServer.RegisterQueryHandler("/api/crm/contacts", (req) =>
            {
                if (req.Method != "GET") return null;
                var qStr = ExtractQueryParam(req.Url, "q");
                var matched = crm.Search(qStr);
                var sb = new StringBuilder();
                sb.Append("{\"items\":[");
                for (int i = 0; i < matched.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    SerializeContact(sb, matched[i]);
                }
                sb.Append("],\"total\":").Append(matched.Count).Append("}");
                return (Encoding.UTF8.GetBytes(sb.ToString()), "application/json; charset=utf-8");
            });

            IcResponseCertV2.RegisterPassThroughPath("/api/crm/contact-get", "GET");
            IcServer.RegisterQueryHandler("/api/crm/contact-get", (req) =>
            {
                // Single-contact lookup. Implemented as /api/crm/contact-get?id=N
                // because IcServer query routing is exact-path only — for
                // /api/crm/contacts/{id} we'd need prefix matching.
                if (req.Method != "GET") return null;
                var ids = ExtractQueryParam(req.Url, "id");
                if (ids is null || !long.TryParse(ids, out var id)) return null;
                var c = crm.Find(id);
                if (c is null) return null;
                var sb = new StringBuilder();
                SerializeContact(sb, c);
                return (Encoding.UTF8.GetBytes(sb.ToString()), "application/json; charset=utf-8");
            });

            app.MapPost("/api/crm/contacts", async (HttpContext ctx) =>
            {
                var c = await ReadContactFromBodyAsync(ctx);
                if (c is null) { ctx.Response.StatusCode = 400; return; }
                var saved = crm.Create(c);
                var sb = new StringBuilder();
                SerializeContact(sb, saved);
                await WriteJsonAsync(ctx, sb.ToString());
            }).DisableAntiforgery();

            app.MapMethods("/api/crm/contacts/{id:long}", new[] { "PUT" }, async (HttpContext ctx) =>
            {
                if (!long.TryParse(ctx.Request.RouteValues["id"]?.ToString(), out var id))
                { ctx.Response.StatusCode = 400; return; }
                var draft = await ReadContactFromBodyAsync(ctx);
                if (draft is null) { ctx.Response.StatusCode = 400; return; }
                var saved = crm.Update(id, draft);
                if (saved is null) { ctx.Response.StatusCode = 404; return; }
                var sb = new StringBuilder();
                SerializeContact(sb, saved);
                await WriteJsonAsync(ctx, sb.ToString());
            }).DisableAntiforgery();

            app.MapMethods("/api/crm/contacts/{id:long}", new[] { "DELETE" }, (HttpContext ctx) =>
            {
                if (!long.TryParse(ctx.Request.RouteValues["id"]?.ToString(), out var id))
                { ctx.Response.StatusCode = 400; return Task.CompletedTask; }
                var ok = crm.Delete(id);
                ctx.Response.StatusCode = ok ? 200 : 404;
                return Task.CompletedTask;
            }).DisableAntiforgery();

            // ─── Presence ─────────────────────────────────────────
            // Use a query-param ?recordId=... POST to avoid the
            // route-template + colon-in-value parsing quirks of
            // Minimal API routing on the canister.
            app.MapPost("/api/crm/presence-heartbeat", async (HttpContext ctx) =>
            {
                var recordId = ctx.Request.Query["recordId"].ToString();
                using var body = new System.IO.MemoryStream();
                await ctx.Request.Body.CopyToAsync(body);
                var json = Encoding.UTF8.GetString(body.ToArray());
                var principal = ExtractJsonString(json, "principal") ?? ExtractJsonString(json, "Principal") ?? "anon";
                var name = ExtractJsonString(json, "name") ?? ExtractJsonString(json, "Name") ?? "Guest";
                presence.Heartbeat(recordId, principal, name);
                Reply.Print($"[crm-presence] heartbeat record={recordId} principal={principal} name={name}");
                ctx.Response.StatusCode = 200;
                await ctx.Response.WriteAsync("{\"ok\":true}");
            }).DisableAntiforgery();

            // ─── Global online counter ────────────────────────────
            // Every browser tab pings /api/online-ping every ~10 s. The
            // count of distinct principals that pinged within the last
            // 30 s = "online right now". Heap-only; resets on upgrade.
            app.MapPost("/api/online-ping", async (HttpContext ctx) =>
            {
                var p = ctx.Request.Query["p"].ToString();
                if (string.IsNullOrEmpty(p)) p = "anon-" + Random.Shared.Next(10000, 99999);
                var n = ctx.Request.Query["n"].ToString();
                if (string.IsNullOrEmpty(n)) n = "Guest";
                presence.Heartbeat("__online__", p, n);
                ctx.Response.StatusCode = 200;
                await ctx.Response.WriteAsync("{\"ok\":true}");
            }).DisableAntiforgery();
            IcResponseCertV2.RegisterPassThroughPath("/api/online-count", "GET");
            IcServer.RegisterQueryHandler("/api/online-count", (req) =>
            {
                if (req.Method != "GET") return null;
                var n = presence.Viewers("__online__").Count;
                var resp = "{\"count\":" + n + "}";
                return (Encoding.UTF8.GetBytes(resp), "application/json; charset=utf-8");
            });

            // ─── Chat realtime: typing indicators + unread cursors ──
            // Typing reuses PresenceService (heap-only, transient — resets
            // on upgrade, which is fine) with a per-room bucket
            // "typing:<roomId>" and a short 6 s stale window. The client
            // POSTs a *throttled* ping (~every 3 s) while composing; the
            // query side is polled. NO stable memory is touched → zero
            // migration risk. Writes here are heap-only updates.
            const long TypingWindowMs = 6000;
            app.MapPost("/api/chat/typing-ping", async (HttpContext ctx) =>
            {
                var roomId = ctx.Request.Query["roomId"].ToString();
                var name = ctx.Request.Query["n"].ToString();
                var pid = ctx.Request.Query["p"].ToString();
                if (string.IsNullOrEmpty(roomId)) { ctx.Response.StatusCode = 400; return; }
                if (string.IsNullOrEmpty(pid)) pid = "anon-" + Random.Shared.Next(10000, 99999);
                if (string.IsNullOrEmpty(name)) name = "Someone";
                presence.Heartbeat("typing:" + roomId, pid, name);
                ctx.Response.StatusCode = 200;
                await ctx.Response.WriteAsync("{\"ok\":true}");
            }).DisableAntiforgery();

            IcResponseCertV2.RegisterPassThroughPath("/api/chat/typing", "GET");
            IcServer.RegisterQueryHandler("/api/chat/typing", (req) =>
            {
                if (req.Method != "GET") return null;
                var roomId = ExtractQueryParam(req.Url, "roomId");
                if (roomId is null) return null;
                // B4: don't leak typing-member names for a private room on the anon query.
                if (int.TryParse(roomId, out var trid) && membership.IsPrivate(servers.ServerOfChannel(trid)))
                    return (Encoding.UTF8.GetBytes("{\"names\":[]}"), "application/json; charset=utf-8");
                var self = ExtractQueryParam(req.Url, "self");   // url-encoded client id to drop
                if (self is not null) self = Uri.UnescapeDataString(self);
                var viewers = presence.Viewers("typing:" + roomId, TypingWindowMs);
                var names = new List<string>();
                foreach (var v in viewers)
                {
                    if (self is not null && v.Principal == self) continue;   // exclude self by stable id, not name
                    if (!names.Contains(v.Name)) names.Add(v.Name);
                    if (names.Count >= 5) break;
                }
                var sb = new StringBuilder();
                sb.Append("{\"names\":[");
                for (int i = 0; i < names.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(EscapeJson(names[i])).Append('"');
                }
                sb.Append("]}");
                return (Encoding.UTF8.GetBytes(sb.ToString()), "application/json; charset=utf-8");
            });

            // Unread cursors — latest message id per room. The client
            // compares these against its localStorage last-read marks to
            // light up channel badges. Pure query; no per-user server
            // state (cosmetic identity couldn't key it reliably anyway).
            IcResponseCertV2.RegisterPassThroughPath("/api/chat/room-cursors", "GET");
            IcServer.RegisterQueryHandler("/api/chat/room-cursors", (req) =>
            {
                if (req.Method != "GET") return null;
                var cursors = chat.LatestMsgIdPerRoom();
                var sb = new StringBuilder();
                sb.Append("{\"rooms\":{");
                bool first = true;
                foreach (var kv in cursors)
                {
                    if (membership.IsPrivate(servers.ServerOfChannel(kv.Key))) continue;   // B4: don't leak private-room activity on the anon query
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(kv.Key).Append("\":").Append(kv.Value);
                }
                sb.Append("}}");
                return (Encoding.UTF8.GetBytes(sb.ToString()), "application/json; charset=utf-8");
            });

            // ─── Global Cmd-K message search (free certified query) ──
            // Linear-scans the in-memory log (500-cap FIFO across rooms)
            // newest-first, up to 20 hits whose author/body contains q.
            IcResponseCertV2.RegisterPassThroughPath("/api/chat/search", "GET");
            IcServer.RegisterQueryHandler("/api/chat/search", (req) =>
            {
                if (req.Method != "GET") return null;
                var q = (ExtractQueryParam(req.Url, "q") ?? "").Trim().ToLowerInvariant();
                var sb = new StringBuilder();
                sb.Append("{\"results\":[");
                int hit = 0;
                if (q.Length > 0)
                {
                    var all = chat.AllMessages();   // oldest-first, <=500
                    for (int i = all.Count - 1; i >= 0 && hit < 20; i--)
                    {
                        var m = all[i];
                        if (m.IsDeleted) continue;
                        var text = m.Text ?? "";
                        var author = m.Username ?? "";
                        if (!text.ToLowerInvariant().Contains(q) && !author.ToLowerInvariant().Contains(q)) continue;
                        var room = chat.FindRoom(m.RoomId);
                        if (room is null) continue;
                        if (membership.IsPrivate(servers.ServerOfChannel(m.RoomId))) continue;   // B4: never surface private content in the anon search
                        var snippet = text.Replace('\n', ' ');
                        if (snippet.Length > 80) snippet = snippet.Substring(0, 80) + "…";
                        if (hit++ > 0) sb.Append(',');
                        sb.Append("{\"channelId\":").Append(room.Id)
                          .Append(",\"channelName\":\"").Append(EscapeJson(room.Name))
                          .Append("\",\"msgId\":").Append(m.Id)
                          .Append(",\"author\":\"").Append(EscapeJson(author))
                          .Append("\",\"snippet\":\"").Append(EscapeJson(snippet)).Append("\"}");
                    }
                }
                sb.Append("]}");
                return (Encoding.UTF8.GetBytes(sb.ToString()), "application/json; charset=utf-8");
            });

            // ─── M2-B: signed chat writes — SIGN-IN REQUIRED ──────────────────────
            // Posting/editing/deleting moved OFF the anonymous bridge onto signed
            // @dfinity/agent http_request_update calls so msg_caller is the real
            // author. The author principal is recorded on each message (7th payload
            // field), so own-message edit/delete is authorized by IDENTITY, not the
            // old spoofable display-name string. Reading stays open/anonymous.
            //   POST /api/chat/post   body={roomId,text,username,replyTo,imageData}
            //   POST /api/chat/edit   body={msgId,text}
            //   POST /api/chat/delete body={msgId}
            app.MapPost("/api/chat/post", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in to post\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                if (!int.TryParse(ExtractJsonNumber(body, "roomId") ?? "", out var roomId))
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"bad roomId\"}"); return; }
                var text = ExtractJsonString(body, "text") ?? "";
                var msgArgs = new Dictionary<string, string> { ["text"] = text };
                var uname = ExtractJsonString(body, "username");
                if (!string.IsNullOrEmpty(uname)) msgArgs["username"] = uname!;
                var replyTo = ExtractJsonNumber(body, "replyTo");
                if (!string.IsNullOrEmpty(replyTo) && long.TryParse(replyTo, out var rt) && rt > 0) msgArgs["replyTo"] = replyTo!;
                // Optional inline image: "data:image/png;base64,XXXX" → ImageStore.
                // B4: images are served over the anonymous /_chat/img query (by id),
                // which can't enforce membership — so do NOT accept image attachments
                // in a PRIVATE channel (they'd be world-fetchable). Text-only there.
                var dataUrl = ExtractJsonString(body, "imageData");
                if (!string.IsNullOrEmpty(dataUrl) && membership.IsPrivate(servers.ServerOfChannel(roomId)))
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"image attachments aren't supported in private channels yet\"}"); return; }
                if (!string.IsNullOrEmpty(dataUrl))
                {
                    var comma = dataUrl!.IndexOf(',');
                    var contentType = "application/octet-stream";
                    string b64 = comma > 0 ? dataUrl.Substring(comma + 1) : dataUrl;
                    if (comma > 0)
                    {
                        var prefix = dataUrl.Substring(0, comma);
                        var colon = prefix.IndexOf(':'); var semi = prefix.IndexOf(';');
                        if (colon >= 0 && semi > colon) contentType = prefix.Substring(colon + 1, semi - colon - 1);
                    }
                    try { msgArgs["imageId"] = images.Add(contentType, Convert.FromBase64String(b64)).ToString(); }
                    catch { /* invalid image data — drop the attachment */ }
                }
                if (string.IsNullOrWhiteSpace(text) && !msgArgs.ContainsKey("imageId"))
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"empty message\"}"); return; }
                if (!canAccess(servers.ServerOfChannel(roomId), caller))    // B4: private server = members only
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"members only\"}"); return; }
                if (mod.IsLocked(roomId) && !canModerate(caller, roomId))   // M2-B: locked channel
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"channel is locked\"}"); return; }
                if (mod.IsMuted(servers.ServerOfChannel(roomId), caller, nowMs()))   // M2-B3: muted user
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"you are muted in this server\"}"); return; }
                chat.Post(roomId, msgArgs, AdminService.ToText(caller));
                if (forum.IsTopic(roomId)) certifyTopic(roomId);   // forum reply → refresh the certified /t/{id} page
                await WriteJsonAsync(ctx, "{\"ok\":true}");
            }).DisableAntiforgery();

            app.MapPost("/api/chat/edit", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in required\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                if (!long.TryParse(ExtractJsonNumber(body, "msgId") ?? "", out var mid))
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"bad msgId\"}"); return; }
                var text = ExtractJsonString(body, "text") ?? "";
                // canModerate=false in B1 (own-message only; mod-edit lands in B2).
                var ok = chat.Edit(mid, text, AdminService.ToText(caller));
                if (!ok) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"not your message\"}"); return; }
                await WriteJsonAsync(ctx, "{\"ok\":true}");
            }).DisableAntiforgery();

            app.MapPost("/api/chat/delete", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in required\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                if (!long.TryParse(ExtractJsonNumber(body, "msgId") ?? "", out var mid))
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"bad msgId\"}"); return; }
                var ok = chat.Delete(mid, AdminService.ToText(caller));
                if (!ok) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"not your message\"}"); return; }
                await WriteJsonAsync(ctx, "{\"ok\":true}");
            }).DisableAntiforgery();

            // React — SIGN-IN REQUIRED (no role check; reacting is a universal
            // signed action). Counter model unchanged; just off the anon bridge.
            app.MapPost("/api/chat/react", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in to react\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                if (!long.TryParse(ExtractJsonNumber(body, "msgId") ?? "", out var mid))
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"bad msgId\"}"); return; }
                var emoji = ExtractJsonString(body, "emoji") ?? "";
                if (string.IsNullOrEmpty(emoji))
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"bad emoji\"}"); return; }
                var rmsg = chat.FindMessage(mid);
                if (rmsg is not null && !canAccess(servers.ServerOfChannel(rmsg.RoomId), caller))   // B4: members only
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"members only\"}"); return; }
                chat.React(mid, emoji);   // validates emoji + no-ops on dead msg internally
                await WriteJsonAsync(ctx, "{\"ok\":true}");
            }).DisableAntiforgery();

            // Thread reply — SIGN-IN REQUIRED. Author principal captured; the
            // display name is derived server-side from the II binding (NOT a
            // client field), so the old spoofable thread username is gone.
            app.MapPost("/api/thread/reply", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in to reply\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                if (!long.TryParse(ExtractJsonNumber(body, "parentMsgId") ?? "", out var parent) || parent <= 0)
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"bad parentMsgId\"}"); return; }
                var text = ExtractJsonString(body, "text") ?? "";
                // A locked channel rejects new content — including thread replies on
                // its messages (mods/super bypass). Same gate as /api/chat/post.
                var pm = chat.FindMessage(parent);
                if (pm is not null && !canAccess(servers.ServerOfChannel(pm.RoomId), caller))   // B4: members only
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"members only\"}"); return; }
                if (pm is not null && mod.IsLocked(pm.RoomId) && !canModerate(caller, pm.RoomId))
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"channel is locked\"}"); return; }
                if (pm is not null && mod.IsMuted(servers.ServerOfChannel(pm.RoomId), caller, nowMs()))   // M2-B3: muted
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"you are muted in this server\"}"); return; }
                var callerText = AdminService.ToText(caller);
                var name = identity.Lookup(callerText)?.DisplayName ?? "";
                var ok = threads.Post(parent, name, text, callerText);
                if (!ok) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"could not post reply\"}"); return; }
                await WriteJsonAsync(ctx, "{\"ok\":true}");
            }).DisableAntiforgery();

            // ─── M2-B moderation — SIGNED + RoleOf>=Moderator (super anywhere) ────
            // delete/redact any message, pin/unpin, lock/unlock a channel. Gate via
            // canModerate(caller, room) which resolves the channel's server.
            app.MapPost("/api/chat/mod/delete", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in required\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                if (!long.TryParse(ExtractJsonNumber(body, "msgId") ?? "", out var mid))
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"bad msgId\"}"); return; }
                var m = chat.FindMessage(mid);
                if (m is null) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("{\"error\":\"no such message\"}"); return; }
                if (!canModerate(caller, m.RoomId))
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"moderator only\"}"); return; }
                chat.Delete(mid, AdminService.ToText(caller), canModerate: true);
                mod.SetPin(mid, false);   // a tombstone can't stay pinned
                Reply.Print($"[mod-delete] {AdminService.ToText(caller)} -> msg {mid}");
                await WriteJsonAsync(ctx, "{\"ok\":true}");
            }).DisableAntiforgery();

            app.MapPost("/api/chat/mod/redact", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in required\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                if (!long.TryParse(ExtractJsonNumber(body, "msgId") ?? "", out var mid))
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"bad msgId\"}"); return; }
                var m = chat.FindMessage(mid);
                if (m is null) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("{\"error\":\"no such message\"}"); return; }
                if (!canModerate(caller, m.RoomId))
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"moderator only\"}"); return; }
                var text = ExtractJsonString(body, "text") ?? "";
                chat.Edit(mid, text, AdminService.ToText(caller), canModerate: true);
                Reply.Print($"[mod-redact] {AdminService.ToText(caller)} -> msg {mid}");
                await WriteJsonAsync(ctx, "{\"ok\":true}");
            }).DisableAntiforgery();

            app.MapPost("/api/chat/mod/pin", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in required\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                if (!long.TryParse(ExtractJsonNumber(body, "msgId") ?? "", out var mid))
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"bad msgId\"}"); return; }
                var m = chat.FindMessage(mid);
                if (m is null) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("{\"error\":\"no such message\"}"); return; }
                if (!canModerate(caller, m.RoomId))
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"moderator only\"}"); return; }
                var pinned = (ExtractJsonString(body, "pinned") ?? "") == "true";
                mod.SetPin(mid, pinned);
                await WriteJsonAsync(ctx, "{\"ok\":true}");
            }).DisableAntiforgery();

            app.MapPost("/api/chat/mod/lock", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in required\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                if (!int.TryParse(ExtractJsonNumber(body, "roomId") ?? "", out var roomId))
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"bad roomId\"}"); return; }
                if (chat.FindRoom(roomId) is null)
                { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("{\"error\":\"no such room\"}"); return; }
                if (!canModerate(caller, roomId))
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"moderator only\"}"); return; }
                var locked = (ExtractJsonString(body, "locked") ?? "") == "true";
                mod.SetLock(roomId, locked);
                Reply.Print($"[mod-lock] {AdminService.ToText(caller)} room {roomId} locked={locked}");
                await WriteJsonAsync(ctx, "{\"ok\":true}");
            }).DisableAntiforgery();

            // Mute/timeout — SIGNED, per-server. A Moderator+/super of the server may
            // mute a regular user; CANNOT mute a Moderator+/admin of that server.
            // durationMs<=0 = unmute; long.MaxValue (or huge) = forever; else
            // untilMs = now + durationMs. A muted principal can't post/thread-reply
            // in that server until it expires (enforced in /api/chat/post + /thread/reply).
            app.MapPost("/api/chat/mod/mute", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in required\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                if (!int.TryParse(ExtractJsonNumber(body, "serverId") ?? "", out var sid))
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"bad serverId\"}"); return; }
                if (!canModerateServer(caller, sid))
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"moderator only\"}"); return; }
                var pText = ExtractJsonString(body, "principal");
                if (string.IsNullOrEmpty(pText))
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"missing principal\"}"); return; }
                byte[] target;
                try { target = AdminService.FromText(pText!); }
                catch (Exception ex)
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"" + EscapeJson(ex.Message) + "\"}"); return; }
                // Don't allow muting a moderator/admin of the server (or a global admin).
                if (roles.RoleOf(sid, target) >= ServerRole.Moderator || admins.IsAdmin(target))
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"cannot mute a moderator or admin\"}"); return; }
                long.TryParse(ExtractJsonNumber(body, "durationMs") ?? "0", out var durMs);
                var now = nowMs();
                long until = durMs <= 0 ? 0
                    : (durMs > long.MaxValue - now ? long.MaxValue : now + durMs);   // clamp forever/overflow
                var ok = mod.SetMute(sid, target, until, now);
                Reply.Print($"[mod-mute] {AdminService.ToText(caller)} server {sid} target {pText} until {until}");
                await WriteJsonAsync(ctx, "{\"ok\":" + (ok ? "true" : "false") + "}");
            }).DisableAntiforgery();

            // ─── B4 private channel READ — SIGNED, members-only (mirror /api/dm/read) ─
            // The SSR render emits NO message bodies for a private server's channels;
            // members fetch them here over a signed update call. NO RegisterQueryHandler
            // / NO pass-through — that would re-leak content on the anonymous channel.
            app.MapPost("/api/chat/private-read", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in required\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                if (!int.TryParse(ExtractJsonNumber(body, "roomId") ?? "", out var roomId))
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"bad roomId\"}"); return; }
                if (!canAccess(servers.ServerOfChannel(roomId), caller))
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"not a member\"}"); return; }
                var msgs = chat.MessagesIn(roomId);
                var sb = new StringBuilder();
                sb.Append("{\"roomId\":").Append(roomId).Append(",\"messages\":[");
                for (int i = 0; i < msgs.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var m = msgs[i];
                    sb.Append("{\"id\":").Append(m.Id)
                      .Append(",\"author\":\"").Append(EscapeJson(m.Username))
                      .Append("\",\"atMs\":").Append(m.AtMs)
                      .Append(",\"text\":\"").Append(EscapeJson(m.IsDeleted ? "" : m.Text))
                      .Append("\",\"deleted\":").Append(m.IsDeleted ? "true" : "false")
                      .Append(",\"edited\":").Append(m.IsEdited ? "true" : "false")
                      .Append(",\"imageId\":").Append(m.ImageId).Append('}');
                }
                sb.Append("]}");
                await WriteJsonAsync(ctx, sb.ToString());
            }).DisableAntiforgery();

            // B4 private THREAD read — SIGNED, members-only. The SSR suppresses a
            // private thread's parent+replies; members fetch them here.
            app.MapPost("/api/thread/read", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in required\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                if (!long.TryParse(ExtractJsonNumber(body, "parentMsgId") ?? "", out var parent) || parent <= 0)
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"bad parentMsgId\"}"); return; }
                var pm = chat.FindMessage(parent);
                if (pm is null) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("{\"error\":\"no such message\"}"); return; }
                if (!canAccess(servers.ServerOfChannel(pm.RoomId), caller))
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"not a member\"}"); return; }
                var reps = threads.Read(parent);
                var sb = new StringBuilder();
                sb.Append("{\"parent\":{\"author\":\"").Append(EscapeJson(pm.Username))
                  .Append("\",\"atMs\":").Append(pm.AtMs)
                  .Append(",\"text\":\"").Append(EscapeJson(pm.IsDeleted ? "" : pm.Text))
                  .Append("\",\"deleted\":").Append(pm.IsDeleted ? "true" : "false").Append("},\"replies\":[");
                for (int i = 0; i < reps.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var t = reps[i];
                    sb.Append("{\"author\":\"").Append(EscapeJson(t.Username))
                      .Append("\",\"atMs\":").Append(t.AtMs)
                      .Append(",\"text\":\"").Append(EscapeJson(t.Text)).Append("\"}");
                }
                sb.Append("]}");
                await WriteJsonAsync(ctx, sb.ToString());
            }).DisableAntiforgery();

            // ─── Identity (II display-name binding) ───────────────
            // POST /api/account/bind  body={"name":"..."}  — SIGNED "subscribe":
            // binds the CALLER's own principal (msg_caller) to a display name, so the
            // name↔principal mapping is verified (no anyone-can-claim spoof). This is
            // the free sign-up: II login + claim your name = a real account.
            app.MapPost("/api/account/bind", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in required\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                var name = ExtractJsonString(body, "name");
                if (string.IsNullOrEmpty(name))
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"missing name\"}"); return; }
                var b = identity.Bind(AdminService.ToText(caller), name!);   // bind the SIGNED caller, not a client-supplied principal
                await WriteJsonAsync(ctx, "{\"principal\":\"" + EscapeJson(b.Principal) + "\",\"name\":\"" + EscapeJson(b.DisplayName) + "\"}");
            }).DisableAntiforgery();

            // POST /api/identity/bind  body={"principal":"...","name":"..."}
            //   → LEGACY cosmetic bind (anyone-can-claim). Kept for back-compat;
            //     new sign-ups use the SIGNED /api/account/bind above.
            // GET  /api/identity/lookup?p=<principal>
            //   → returns {"name":"..."} or 404 if unbound.
            app.MapPost("/api/identity/bind", async (HttpContext ctx) =>
            {
                using var body = new System.IO.MemoryStream();
                await ctx.Request.Body.CopyToAsync(body);
                var json = Encoding.UTF8.GetString(body.ToArray());
                var principal = ExtractJsonString(json, "principal");
                var name = ExtractJsonString(json, "name");
                if (string.IsNullOrEmpty(principal) || string.IsNullOrEmpty(name))
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsync("{\"error\":\"missing principal or name\"}");
                    return;
                }
                var b = identity.Bind(principal, name);
                var resp = "{\"principal\":\"" + EscapeJson(b.Principal)
                         + "\",\"name\":\"" + EscapeJson(b.DisplayName)
                         + "\",\"boundAtMs\":" + b.BoundAtMs + "}";
                await WriteJsonAsync(ctx, resp);
            }).DisableAntiforgery();
            IcResponseCertV2.RegisterPassThroughPath("/api/identity/lookup", "GET");
            IcServer.RegisterQueryHandler("/api/identity/lookup", (req) =>
            {
                if (req.Method != "GET") return null;
                var p = ExtractQueryParam(req.Url, "p");
                if (string.IsNullOrEmpty(p)) return null;
                var b = identity.Lookup(p);
                if (b is null)
                {
                    var miss = "{\"bound\":false}";
                    return (Encoding.UTF8.GetBytes(miss), "application/json; charset=utf-8");
                }
                var hit = "{\"bound\":true,\"principal\":\"" + EscapeJson(b.Principal)
                        + "\",\"name\":\"" + EscapeJson(b.DisplayName)
                        + "\",\"boundAtMs\":" + b.BoundAtMs + "}";
                return (Encoding.UTF8.GetBytes(hit), "application/json; charset=utf-8");
            });

            IcResponseCertV2.RegisterPassThroughPath("/api/identity/lookup-name", "GET");
            IcServer.RegisterQueryHandler("/api/identity/lookup-name", (req) =>
            {
                if (req.Method != "GET") return null;
                var n = ExtractQueryParam(req.Url, "n");
                if (string.IsNullOrEmpty(n)) return null;
                var b = identity.LookupByName(n);
                var json = b is null
                    ? "{\"bound\":false}"
                    : "{\"bound\":true,\"principal\":\"" + EscapeJson(b.Principal) + "\",\"name\":\"" + EscapeJson(b.DisplayName) + "\"}";
                return (Encoding.UTF8.GetBytes(json), "application/json; charset=utf-8");
            });

            // ─── Servers (guilds) — PUBLIC, same trust model as chat rooms ───────
            // Reads are queries (free, ~300ms). Server 0 "Wasp" is synthesised from
            // every chat room NOT claimed by a user server, so rooms 1 & 2 appear
            // without any change to ChatService.
            IcResponseCertV2.RegisterPassThroughPath("/api/servers", "GET");
            IcServer.RegisterQueryHandler("/api/servers", (req) =>
            {
                if (req.Method != "GET") return null;
                var list = servers.ListServers();
                var sb = new StringBuilder();
                sb.Append("{\"servers\":[");
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var s = list[i];
                    sb.Append("{\"id\":").Append(s.Id)
                      .Append(",\"name\":\"").Append(EscapeJson(s.Name))
                      .Append("\",\"createdAtMs\":").Append(s.CreatedAtMs)
                      .Append(",\"private\":").Append(membership.IsPrivate(s.Id) ? "true" : "false")   // B4: rail lock glyph (flag only, never content)
                      .Append(",\"channelIds\":[");
                    for (int j = 0; j < s.ChannelIds.Count; j++) { if (j > 0) sb.Append(','); sb.Append(s.ChannelIds[j]); }
                    sb.Append("]}");
                }
                sb.Append("]}");
                return (Encoding.UTF8.GetBytes(sb.ToString()), "application/json; charset=utf-8");
            });

            IcResponseCertV2.RegisterPassThroughPath("/api/server-channels", "GET");
            IcServer.RegisterQueryHandler("/api/server-channels", (req) =>
            {
                if (req.Method != "GET") return null;
                int.TryParse(ExtractQueryParam(req.Url, "serverId") ?? "0", out var sid);
                // Private servers: do NOT reveal channel names over the anonymous query
                // (private means private — members read content via signed paths).
                if (membership.IsPrivate(sid))
                    return (Encoding.UTF8.GetBytes("{\"serverId\":" + sid + ",\"channels\":[]}"), "application/json; charset=utf-8");
                var ids = servers.ChannelsOf(sid);
                var sb = new StringBuilder();
                sb.Append("{\"serverId\":").Append(sid).Append(",\"channels\":[");
                bool first = true;
                foreach (var cid in ids)
                {
                    var rm = chat.FindRoom(cid);
                    if (rm is null) continue;
                    if (!first) sb.Append(','); first = false;
                    sb.Append("{\"id\":").Append(rm.Id).Append(",\"name\":\"").Append(EscapeJson(rm.Name)).Append("\"}");
                }
                sb.Append("]}");
                return (Encoding.UTF8.GetBytes(sb.ToString()), "application/json; charset=utf-8");
            });

            // ─── M2 role-gated server/channel creation — SIGNED UPDATE CALLS ONLY ──
            // These were "public, no auth". Now: caller must arrive via a signed
            // @dfinity/agent http_request_update (AdminService.CurrentCaller()), and:
            //   • create server  → global super-admin (AdminService.IsAdmin); the
            //                       creator is recorded as the server's Owner.
            //   • create channel → Admin+ on THAT server (RoleService) OR super-admin.
            // The anonymous wasp.js bridge path cannot satisfy these (caller is
            // anonymous), so the UI issues them through the signed server-actions
            // client (mirrors the DM client). Reaching these via plain fetch lands
            // anonymous and is rejected 401.
            app.MapPost("/api/servers", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in required\"}"); return; }
                if (!isSuper(caller))
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"super-admin only\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                var name = ExtractJsonString(body, "name");
                if (string.IsNullOrWhiteSpace(name))
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"missing name\"}"); return; }
                var s = servers.CreateServer(name!);
                roles.SetOwner(s.Id, caller);   // creator becomes the server Owner (sticky)
                // Server KIND chosen at create (default discussion). Forum/feed reuse the
                // same room+message substrate; only kind-specific state lands in new regions.
                var kind = (ExtractJsonString(body, "kind") ?? "").ToLowerInvariant() switch
                {
                    "forum" => ServerKind.Forum,
                    "feed"  => ServerKind.Feed,
                    _       => ServerKind.Discussion,
                };
                kinds.SetKind(s.Id, kind);
                // Private-on-create for ANY kind — forum/feed content is served to members over
                // the signed /api/forum/read + /api/feed/read paths; anonymous SSR shows a gate.
                if ((ExtractJsonString(body, "private") ?? "") == "true")
                    membership.SetPrivate(s.Id, true);
                // A feed is one timeline; create its single room now so it's usable on first load.
                // MUST be CreateRoomForced — CreateRoom dedups on the name "timeline", which
                // would make every feed share ONE room (cross-feed post bleed).
                if (kind == ServerKind.Feed)
                {
                    var wall = chat.CreateRoomForced("timeline");
                    servers.AddChannel(s.Id, wall.Id);
                }
                Reply.Print($"[server-create] {AdminService.ToText(caller)} -> #{s.Id} {s.Name} kind={kind}");
                await WriteJsonAsync(ctx, "{\"id\":" + s.Id + ",\"name\":\"" + EscapeJson(s.Name) + "\",\"kind\":\"" + kind.ToString().ToLowerInvariant() + "\"}");
            }).DisableAntiforgery();

            // add channel: create a ChatService room AND record it under the server.
            app.MapPost("/api/server-channels", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in required\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                int.TryParse(ExtractJsonNumber(body, "serverId") ?? "0", out var sid);
                if (roles.RoleOf(sid, caller) < ServerRole.Admin && !isSuper(caller))
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"server-admin only\"}"); return; }
                var name = ExtractJsonString(body, "name");
                if (string.IsNullOrWhiteSpace(name))
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"missing name\"}"); return; }
                var room = chat.CreateRoom(name!);          // owns the room record (layout unchanged)
                servers.AddChannel(sid, room.Id);           // owns the server->channel association
                Reply.Print($"[channel-create] {AdminService.ToText(caller)} -> server {sid} #{room.Name}");
                await WriteJsonAsync(ctx, "{\"serverId\":" + sid + ",\"channelId\":" + room.Id
                    + ",\"channelName\":\"" + EscapeJson(room.Name) + "\"}");
            }).DisableAntiforgery();

            // ─── F1 FORUM — SIGNED UPDATE CALLS ONLY ──────────────────────────
            // A forum topic = a NEW ChatService room inside a forum-kind server,
            // whose first message is the OP. Replies reuse /api/chat/post (post to
            // the topic room); upvotes reuse /api/chat/react. Only the topic title
            // + accepted-answer live in ForumService. Create a topic:
            //   POST /api/forum/topic  body={serverId,title,text}
            app.MapPost("/api/forum/topic", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in to post\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                if (!int.TryParse(ExtractJsonNumber(body, "serverId") ?? "", out var sid) || sid <= 0)
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"bad serverId\"}"); return; }
                if (kinds.KindOf(sid) != ServerKind.Forum)
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"not a forum\"}"); return; }
                if (!canAccess(sid, caller))
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"members only\"}"); return; }
                if (mod.IsMuted(sid, caller, nowMs()))
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"you are muted in this server\"}"); return; }
                var title = ExtractJsonString(body, "title");
                var text = ExtractJsonString(body, "text") ?? "";
                if (string.IsNullOrWhiteSpace(title))
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"missing title\"}"); return; }
                if (string.IsNullOrWhiteSpace(text))
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"missing body\"}"); return; }
                // room name = a short slug of the title (display title lives in ForumService).
                var slug = title!.Length > 24 ? title.Substring(0, 24) : title;
                var room = chat.CreateRoomForced(slug);
                servers.AddChannel(sid, room.Id);
                var opArgs = new Dictionary<string, string> { ["text"] = text };
                var uname = ExtractJsonString(body, "username");
                if (!string.IsNullOrEmpty(uname)) opArgs["username"] = uname!;
                chat.Post(room.Id, opArgs, AdminService.ToText(caller));   // the OP
                forum.RegisterTopic(room.Id, sid, title!);
                certifyTopic(room.Id);   // register the certified /t/{id} page (gate if private)
                Reply.Print($"[forum-topic] {AdminService.ToText(caller)} -> server {sid} topic #{room.Id} {title}");
                await WriteJsonAsync(ctx, "{\"serverId\":" + sid + ",\"topicId\":" + room.Id + "}");
            }).DisableAntiforgery();

            // Mark / clear the accepted answer. Authorized: the topic's OP author
            // OR a moderator of the category. body={roomId,msgId} (msgId 0 = clear).
            app.MapPost("/api/forum/solve", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in required\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                if (!int.TryParse(ExtractJsonNumber(body, "roomId") ?? "", out var roomId) || !forum.IsTopic(roomId))
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"not a topic\"}"); return; }
                long.TryParse(ExtractJsonNumber(body, "msgId") ?? "0", out var msgId);
                // OP author = author of the topic room's first (lowest-id) message.
                string opAuthor = ""; long opId = long.MaxValue;
                foreach (var m in chat.AllMessages())
                    if (m.RoomId == roomId && m.Id < opId) { opId = m.Id; opAuthor = m.AuthorPrincipal; }
                var callerText = AdminService.ToText(caller);
                var isOpAuthor = opAuthor.Length > 0 && opAuthor == callerText;
                if (!isOpAuthor && !canModerate(caller, roomId))
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"only the author or a moderator can mark the answer\"}"); return; }
                // The marked answer must be a live reply in THIS topic (0 = clear).
                if (msgId > 0)
                {
                    var m = chat.FindMessage(msgId);
                    if (m is null || m.RoomId != roomId || m.IsDeleted)
                    { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"answer must be a reply in this topic\"}"); return; }
                }
                forum.SetSolved(roomId, msgId);
                certifyTopic(roomId);   // accepted-answer changed → refresh the certified page
                await WriteJsonAsync(ctx, "{\"ok\":true}");
            }).DisableAntiforgery();

            // Identity-weighted vote on a message (forum OP/replies) — SIGNED. One vote per
            // principal; clicking the same arrow again toggles it off. body={msgId, dir:"up"|"down"}
            app.MapPost("/api/forum/vote", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in to vote\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                if (!long.TryParse(ExtractJsonNumber(body, "msgId") ?? "", out var msgId) || msgId <= 0)
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"bad msgId\"}"); return; }
                var m = chat.FindMessage(msgId);
                if (m is null || m.IsDeleted)
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"no such message\"}"); return; }
                if (!canAccess(servers.ServerOfChannel(m.RoomId), caller))
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"members only\"}"); return; }
                var dv = ExtractJsonString(body, "dir") ?? "";
                int want = dv == "up" ? 1 : (dv == "down" ? -1 : 0);
                var ct = AdminService.ToText(caller);
                int finalDir = (want != 0 && votes.VoteOf(ct, msgId) == want) ? 0 : want;   // re-click same arrow → clear
                if (!votes.SetVote(ct, msgId, finalDir))
                { ctx.Response.StatusCode = 507; await ctx.Response.WriteAsync("{\"error\":\"vote capacity reached\"}"); return; }
                await WriteJsonAsync(ctx, "{\"score\":" + votes.Score(msgId) + ",\"yourVote\":" + votes.VoteOf(ct, msgId) + "}");
            }).DisableAntiforgery();

            // SIGNED read of a PRIVATE forum's content (members only). The anonymous SSR
            // renders a gate; members fetch here. body={serverId, roomId?} — roomId>0 = a
            // topic's OP+replies; omitted = the category's topic list.
            app.MapPost("/api/forum/read", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in required\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                if (!int.TryParse(ExtractJsonNumber(body, "serverId") ?? "", out var sid) || kinds.KindOf(sid) != ServerKind.Forum)
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"not a forum\"}"); return; }
                if (!canAccess(sid, caller))
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"members only\"}"); return; }
                int.TryParse(ExtractJsonNumber(body, "roomId") ?? "0", out var roomId);
                var sb = new StringBuilder();
                if (roomId > 0 && forum.IsTopic(roomId))
                {
                    // topic view: OP + replies (oldest first)
                    var solved = forum.SolvedOf(roomId);
                    sb.Append("{\"mode\":\"topic\",\"title\":\"").Append(EscapeJson(forum.TitleOf(roomId)))
                      .Append("\",\"locked\":").Append(mod.IsLocked(roomId) ? "true" : "false")
                      .Append(",\"posts\":[");
                    var tmsgs = new List<ChatService.Message>();
                    foreach (var m in chat.AllMessages()) if (m.RoomId == roomId && !m.IsDeleted) tmsgs.Add(m);
                    tmsgs.Sort((a, b) => a.Id.CompareTo(b.Id));
                    int k = 0;
                    foreach (var m in tmsgs)
                    {
                        var author = !string.IsNullOrWhiteSpace(m.Username) ? m.Username : (m.AuthorPrincipal.Length >= 5 ? m.AuthorPrincipal.Substring(0, 5) : "anon");
                        if (k > 0) sb.Append(',');
                        sb.Append("{\"id\":").Append(m.Id).Append(",\"author\":\"").Append(EscapeJson(author))
                          .Append("\",\"html\":\"").Append(EscapeJson(MessageHtml.Render(m.Text)))
                          .Append("\",\"score\":").Append(votes.Score(m.Id))
                          .Append(",\"isAnswer\":").Append(m.Id == solved ? "true" : "false")
                          .Append(",\"op\":").Append(k == 0 ? "true" : "false").Append("}");
                        k++;
                    }
                    sb.Append("]}");
                }
                else
                {
                    // category list: topics with meta
                    string catName = ""; foreach (var s2 in servers.ListServers()) if (s2.Id == sid) { catName = s2.Name; break; }
                    sb.Append("{\"mode\":\"list\",\"name\":\"").Append(EscapeJson(catName)).Append("\",\"topics\":[");
                    int k = 0;
                    foreach (var rid in servers.ChannelsOf(sid))
                    {
                        if (!forum.IsTopic(rid)) continue;
                        ChatService.Message? op = null; long last = 0; int replies = 0;
                        foreach (var m in chat.AllMessages())
                            if (m.RoomId == rid && !m.IsDeleted) { if (op is null || m.Id < op.Id) op = m; if (m.AtMs > last) last = m.AtMs; replies++; }
                        if (op is null) continue;
                        var author = !string.IsNullOrWhiteSpace(op.Username) ? op.Username : (op.AuthorPrincipal.Length >= 5 ? op.AuthorPrincipal.Substring(0, 5) : "anon");
                        if (k > 0) sb.Append(',');
                        sb.Append("{\"roomId\":").Append(rid).Append(",\"title\":\"").Append(EscapeJson(forum.TitleOf(rid)))
                          .Append("\",\"author\":\"").Append(EscapeJson(author)).Append("\",\"replies\":").Append(Math.Max(0, replies - 1))
                          .Append(",\"solved\":").Append(forum.SolvedOf(rid) > 0 ? "true" : "false").Append("}");
                        k++;
                    }
                    sb.Append("]}");
                }
                await WriteJsonAsync(ctx, sb.ToString());
            }).DisableAntiforgery();

            // SIGNED read of a PRIVATE feed's timeline (members only). body={serverId}
            app.MapPost("/api/feed/read", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in required\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                if (!int.TryParse(ExtractJsonNumber(body, "serverId") ?? "", out var sid) || kinds.KindOf(sid) != ServerKind.Feed)
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"not a feed\"}"); return; }
                if (!canAccess(sid, caller))
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"members only\"}"); return; }
                var ch = servers.ChannelsOf(sid);
                int tl = ch.Count > 0 ? ch[0] : 0;
                var posts = new List<ChatService.Message>();
                foreach (var m in chat.AllMessages()) if (m.RoomId == tl && m.ReplyToId == 0 && !m.IsDeleted) posts.Add(m);
                posts.Sort((a, b) => b.AtMs.CompareTo(a.AtMs));
                var sb = new StringBuilder("{\"roomId\":" + tl + ",\"posts\":[");
                int k = 0;
                foreach (var m in posts)
                {
                    if (k >= 60) break;
                    var author = !string.IsNullOrWhiteSpace(m.Username) ? m.Username : (m.AuthorPrincipal.Length >= 5 ? m.AuthorPrincipal.Substring(0, 5) : "anon");
                    int likes = 0; foreach (var kv in chat.ReactionsOf(m.Id)) likes += kv.Value;
                    int replies = 0; foreach (var x in chat.AllMessages()) if (x.ReplyToId == m.Id && !x.IsDeleted) replies++;
                    if (k > 0) sb.Append(',');
                    sb.Append("{\"id\":").Append(m.Id).Append(",\"author\":\"").Append(EscapeJson(author))
                      .Append("\",\"authorPrincipal\":\"").Append(EscapeJson(m.AuthorPrincipal))
                      .Append("\",\"html\":\"").Append(EscapeJson(MessageHtml.Render(m.Text)))
                      .Append("\",\"srcId\":").Append(sid).Append(",\"likes\":").Append(likes)
                      .Append(",\"replies\":").Append(replies).Append(",\"reposts\":").Append(reposts.RepostCount(m.Id)).Append("}");
                    k++;
                }
                sb.Append("]}");
                await WriteJsonAsync(ctx, sb.ToString());
            }).DisableAntiforgery();

            // ─── F2 FEED — follow a feed + repost a post — SIGNED UPDATE CALLS ONLY ──
            // Posting / replying / liking reuse /api/chat/post + /api/chat/react.
            //   POST /api/feed/follow  body={serverId, on:"true"|"false"}
            app.MapPost("/api/feed/follow", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in to follow\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                if (!int.TryParse(ExtractJsonNumber(body, "serverId") ?? "", out var sid) || sid <= 0)
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"bad serverId\"}"); return; }
                if (kinds.KindOf(sid) != ServerKind.Feed)
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"not a feed\"}"); return; }
                if (!canAccess(sid, caller))
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"members only\"}"); return; }
                var on = (ExtractJsonString(body, "on") ?? "true") != "false";
                if (!follows.SetFollow(AdminService.ToText(caller), sid, on))
                { ctx.Response.StatusCode = 507; await ctx.Response.WriteAsync("{\"error\":\"follow capacity reached\"}"); return; }
                await WriteJsonAsync(ctx, "{\"following\":" + (on ? "true" : "false") + ",\"count\":" + follows.FollowerCount(sid) + "}");
            }).DisableAntiforgery();

            // Repost (amplify) a feed post. body={msgId, on:"true"|"false"}
            app.MapPost("/api/feed/repost", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in to repost\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                if (!long.TryParse(ExtractJsonNumber(body, "msgId") ?? "", out var msgId) || msgId <= 0)
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"bad msgId\"}"); return; }
                var m = chat.FindMessage(msgId);
                if (m is null || m.IsDeleted)
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"no such post\"}"); return; }
                var sid = servers.ServerOfChannel(m.RoomId);
                if (kinds.KindOf(sid) != ServerKind.Feed)
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"not a feed post\"}"); return; }
                if (!canAccess(sid, caller))
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"members only\"}"); return; }
                var on = (ExtractJsonString(body, "on") ?? "true") != "false";
                if (!reposts.SetRepost(AdminService.ToText(caller), msgId, on))
                { ctx.Response.StatusCode = 507; await ctx.Response.WriteAsync("{\"error\":\"repost capacity reached\"}"); return; }
                // record/clear the repost time so it can bump the post in followers' homes
                var rtp = AdminService.ToText(caller);
                if (on) repostTimes.SetRepostTime(rtp, msgId, nowMs());
                else    repostTimes.ClearRepostTime(rtp, msgId);
                await WriteJsonAsync(ctx, "{\"reposted\":" + (on ? "true" : "false") + ",\"count\":" + reposts.RepostCount(msgId) + "}");
            }).DisableAntiforgery();

            // Follow / unfollow a PERSON (principal) — signed. Makes reposts surface:
            // posts + reposts by people you follow appear in your home. body={principal, on}
            app.MapPost("/api/user/follow", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in to follow\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                var who = ExtractJsonString(body, "principal");
                string target;
                try { var p = AdminService.FromText(who ?? ""); if (AdminService.IsAnonymous(p)) throw new Exception(); target = AdminService.ToText(p); }
                catch { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"bad principal\"}"); return; }
                var on = (ExtractJsonString(body, "on") ?? "true") != "false";
                if (!userFollows.SetFollow(AdminService.ToText(caller), target, on))
                { ctx.Response.StatusCode = 507; await ctx.Response.WriteAsync("{\"error\":\"follow capacity reached\"}"); return; }
                await WriteJsonAsync(ctx, "{\"following\":" + (on ? "true" : "false") + ",\"count\":" + userFollows.FollowerCount(target) + "}");
            }).DisableAntiforgery();

            // Personalized FOLLOWING home — SIGNED read (anonymous SSR can't know the viewer).
            // Union of: root posts from feeds you follow + root posts BY people you follow +
            // posts REPOSTED by people you follow (tagged repostedBy). Deduped, newest first. body={}
            app.MapPost("/api/feed/home", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in required\"}"); return; }
                var callerText = AdminService.ToText(caller);
                var homeBody = await ReadBodyAsync(ctx);
                int homeLimit = 60; if (int.TryParse(ExtractJsonNumber(homeBody, "limit") ?? "", out var hl) && hl > 0) homeLimit = hl > 300 ? 300 : hl;
                var nameOf = new Dictionary<int, string>();
                foreach (var srv in servers.ListServers()) nameOf[srv.Id] = srv.Name;
                // every public feed timeline room (resolve any feed post's source) + the subset
                // the caller follows (a feed-follow includes every post in that feed).
                var feedRooms = new Dictionary<int, int>();   // roomId -> feedServerId
                var followedRooms = new HashSet<int>();
                foreach (var srv in servers.ListServers())
                {
                    if (kinds.KindOf(srv.Id) != ServerKind.Feed || membership.IsPrivate(srv.Id)) continue;
                    var ch = servers.ChannelsOf(srv.Id);
                    if (ch.Count > 0) feedRooms[ch[0]] = srv.Id;
                }
                foreach (var sid in follows.FollowingOf(callerText))
                {
                    if (kinds.KindOf(sid) != ServerKind.Feed || membership.IsPrivate(sid)) continue;
                    var ch = servers.ChannelsOf(sid);
                    if (ch.Count > 0) followedRooms.Add(ch[0]);
                }
                // people the caller follows + the msgIds they reposted (→ surface + tag + bump).
                var followedUsers = new HashSet<string>(userFollows.FollowedOf(callerText));
                var repostTag = new Dictionary<long, string>();    // msgId -> latest followed reposter
                var repostBumpMs = new Dictionary<long, long>();   // msgId -> max followed repost ms (0 = legacy/none)
                foreach (var u in followedUsers)
                    foreach (var mid in reposts.RepostsBy(u))
                    {
                        var ts = repostTimes.RepostTimeOf(u, mid);
                        if (!repostBumpMs.TryGetValue(mid, out var cur) || ts > cur) { repostBumpMs[mid] = ts; repostTag[mid] = u; }
                        else if (!repostTag.ContainsKey(mid)) repostTag[mid] = u;
                    }
                // qualifying root posts that live in a feed room (no LINQ). A reposted item
                // sorts by the followed reposter's repost time (bumps); else by post AtMs.
                var picked = new List<(ChatService.Message m, string rb, long sortMs)>();
                var seen = new HashSet<long>();
                foreach (var m in chat.AllMessages())
                {
                    if (m.ReplyToId != 0 || m.IsDeleted || !feedRooms.ContainsKey(m.RoomId)) continue;
                    string rb = ""; long sortMs = m.AtMs;
                    bool include = followedRooms.Contains(m.RoomId) || followedUsers.Contains(m.AuthorPrincipal);
                    if (repostTag.TryGetValue(m.Id, out var rep))
                    {
                        include = true; rb = rep;
                        long bump = repostBumpMs.TryGetValue(m.Id, out var bm) && bm > 0 ? bm : m.AtMs;   // legacy ts==0 → AtMs
                        if (bump > sortMs) sortMs = bump;
                    }
                    if (include && seen.Add(m.Id)) picked.Add((m, rb, sortMs));
                }
                picked.Sort((a, b) => b.sortMs.CompareTo(a.sortMs));
                var sb = new StringBuilder("{\"posts\":[");
                int n = 0;
                foreach (var item in picked)
                {
                    if (n >= homeLimit) break;
                    var m = item.m;
                    var srcId = feedRooms[m.RoomId];
                    var author = !string.IsNullOrWhiteSpace(m.Username) ? m.Username
                        : (m.AuthorPrincipal.Length >= 5 ? m.AuthorPrincipal.Substring(0, 5) : (m.AuthorPrincipal.Length > 0 ? m.AuthorPrincipal : "anon"));
                    int likes = 0; foreach (var kv in chat.ReactionsOf(m.Id)) likes += kv.Value;
                    int replies = 0; foreach (var x in chat.AllMessages()) if (x.ReplyToId == m.Id && !x.IsDeleted) replies++;
                    var rbShort = item.rb.Length >= 8 ? item.rb.Substring(0, 8) : item.rb;
                    if (n > 0) sb.Append(',');
                    sb.Append("{\"id\":").Append(m.Id)
                      .Append(",\"author\":\"").Append(EscapeJson(author)).Append("\"")
                      .Append(",\"authorPrincipal\":\"").Append(EscapeJson(m.AuthorPrincipal)).Append("\"")
                      .Append(",\"html\":\"").Append(EscapeJson(MessageHtml.Render(m.Text))).Append("\"")
                      .Append(",\"srcId\":").Append(srcId)
                      .Append(",\"srcName\":\"").Append(EscapeJson(nameOf.TryGetValue(srcId, out var nm) ? nm : "")).Append("\"")
                      .Append(",\"likes\":").Append(likes)
                      .Append(",\"replies\":").Append(replies)
                      .Append(",\"reposts\":").Append(reposts.RepostCount(m.Id))
                      .Append(",\"reposted\":").Append(reposts.HasReposted(callerText, m.Id) ? "true" : "false")
                      .Append(",\"following\":").Append(userFollows.IsFollowing(callerText, m.AuthorPrincipal) ? "true" : "false")
                      .Append(",\"repostedBy\":\"").Append(EscapeJson(rbShort)).Append("\"")
                      .Append("}");
                    n++;
                }
                sb.Append("]}");
                await WriteJsonAsync(ctx, sb.ToString());
            }).DisableAntiforgery();

            // ─── M2 per-server role administration — SIGNED UPDATE CALLS ONLY ─────
            // grant: body {serverId, principal, role:"admin"|"moderator"}.
            //   • granting Admin     → caller must be Owner of that server OR super-admin.
            //   • granting Moderator → caller must be Admin+ on that server OR super-admin.
            //   • Owner cannot be granted/changed here (set once at create); the
            //     existing Owner is never demoted by a grant/revoke.
            app.MapPost("/api/server/grant", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in required\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                int.TryParse(ExtractJsonNumber(body, "serverId") ?? "0", out var sid);
                if (sid <= 0)
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"bad serverId\"}"); return; }
                var roleStr = (ExtractJsonString(body, "role") ?? "").Trim().ToLowerInvariant();
                var target = roleStr == "admin" ? ServerRole.Admin
                           : roleStr == "moderator" || roleStr == "mod" ? ServerRole.Moderator
                           : ServerRole.None;
                if (target == ServerRole.None)
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"role must be admin or moderator\"}"); return; }
                var callerRole = roles.RoleOf(sid, caller);
                bool allowed = target == ServerRole.Admin
                    ? (callerRole == ServerRole.Owner || isSuper(caller))
                    : (callerRole >= ServerRole.Admin || isSuper(caller));
                if (!allowed)
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"not permitted to grant that role\"}"); return; }
                var pText = ExtractJsonString(body, "principal");
                if (string.IsNullOrEmpty(pText))
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"missing principal\"}"); return; }
                byte[] target_p;
                try { target_p = AdminService.FromText(pText!); }
                catch (Exception ex)
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"" + EscapeJson(ex.Message) + "\"}"); return; }
                var victimRole = roles.RoleOf(sid, target_p);
                if (victimRole == ServerRole.Owner)
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"cannot change the owner\"}"); return; }
                // Changing someone who is CURRENTLY an Admin (e.g. demoting them to
                // Moderator) requires Owner/super — same bar as revoking an Admin.
                // Otherwise an Admin could demote-then-revoke a peer Admin, defeating
                // the revoke matrix's owner-only protection.
                if (victimRole == ServerRole.Admin && !(callerRole == ServerRole.Owner || isSuper(caller)))
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"only the owner can change another admin\"}"); return; }
                var ok = roles.Grant(sid, target_p, target);
                Reply.Print($"[role-grant] {AdminService.ToText(caller)} set {pText}={roleStr} on server {sid}");
                await WriteJsonAsync(ctx, "{\"ok\":" + (ok ? "true" : "false") + "}");
            }).DisableAntiforgery();

            // revoke: body {serverId, principal}. Caller must be Owner/super-admin to
            // revoke an Admin, or Admin+/super-admin to revoke a Moderator. Owner is
            // never revocable.
            app.MapPost("/api/server/revoke", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in required\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                int.TryParse(ExtractJsonNumber(body, "serverId") ?? "0", out var sid);
                var pText = ExtractJsonString(body, "principal");
                if (sid <= 0 || string.IsNullOrEmpty(pText))
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"missing serverId or principal\"}"); return; }
                byte[] target_p;
                try { target_p = AdminService.FromText(pText!); }
                catch (Exception ex)
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"" + EscapeJson(ex.Message) + "\"}"); return; }
                var victimRole = roles.RoleOf(sid, target_p);
                if (victimRole == ServerRole.Owner)
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"cannot revoke the owner\"}"); return; }
                var callerRole = roles.RoleOf(sid, caller);
                bool allowed = victimRole == ServerRole.Admin
                    ? (callerRole == ServerRole.Owner || isSuper(caller))
                    : (callerRole >= ServerRole.Admin || isSuper(caller));
                if (!allowed)
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"not permitted\"}"); return; }
                var ok = roles.Revoke(sid, target_p);
                Reply.Print($"[role-revoke] {AdminService.ToText(caller)} removed {pText} on server {sid}");
                await WriteJsonAsync(ctx, "{\"ok\":" + (ok ? "true" : "false") + "}");
            }).DisableAntiforgery();

            // myrole: SIGNED. body {serverId}. Returns the caller's principal, whether
            // they are a global super-admin, and their role on that server. The UI
            // gates the create-channel "+" and the roles panel on this.
            app.MapPost("/api/server/myrole", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                var body = await ReadBodyAsync(ctx);
                int.TryParse(ExtractJsonNumber(body, "serverId") ?? "0", out var sid);
                var role = roles.RoleOf(sid, caller);
                var sb = new StringBuilder();
                sb.Append("{\"principal\":\"").Append(AdminService.ToText(caller)).Append('"');
                sb.Append(",\"isSuperAdmin\":").Append(isSuper(caller) ? "true" : "false");
                sb.Append(",\"role\":\"").Append(role.ToString().ToLowerInvariant()).Append('"');
                // B4: lets the client reveal the settings/members UI and decide
                // whether this server's content is SSR (public) or signed-fetched (private).
                sb.Append(",\"serverPrivate\":").Append(membership.IsPrivate(sid) ? "true" : "false");
                sb.Append(",\"canManageMembers\":").Append((role >= ServerRole.Admin || isSuper(caller)) ? "true" : "false");
                sb.Append(",\"canAccess\":").Append(canAccess(sid, caller) ? "true" : "false").Append('}');
                await WriteJsonAsync(ctx, sb.ToString());
            }).DisableAntiforgery();

            // ─── B4 membership + visibility management — SIGNED, owner/admin only ──
            // visibility flip + member add/remove + roster. Gate RoleOf>=Admin||isSuper.
            app.MapPost("/api/server/visibility", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in required\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                if (!int.TryParse(ExtractJsonNumber(body, "serverId") ?? "", out var sid) || sid <= 0)
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"bad serverId\"}"); return; }
                if (!(roles.RoleOf(sid, caller) >= ServerRole.Admin || isSuper(caller)))
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"server-admin only\"}"); return; }
                // Forum/feed CAN now be private: members read content over the signed
                // /api/forum/read + /api/feed/read paths; anonymous SSR renders a members-only
                // gate (no content); certified topic pages exclude private topics (see F2b).
                var priv = (ExtractJsonString(body, "private") ?? "") == "true";
                membership.SetPrivate(sid, priv);
                // Re-certify the forum's topic pages: making it private overwrites every /t/{id}
                // with the members-only gate (no content); making it public restores content.
                if (kinds.KindOf(sid) == ServerKind.Forum)
                    foreach (var rid in servers.ChannelsOf(sid))
                        if (forum.IsTopic(rid)) certifyTopic(rid);
                Reply.Print($"[visibility] {AdminService.ToText(caller)} server {sid} private={priv}");
                await WriteJsonAsync(ctx, "{\"ok\":true,\"private\":" + (priv ? "true" : "false") + "}");
            }).DisableAntiforgery();

            // DELETE a user server — SIGNED, server-admin-or-super. Permanently
            // removes the server, ERASES each of its channel rooms + their messages
            // (so they don't resurface under the virtual default), and re-certifies
            // any forum topic pages to a now-gone page. Private servers' /t pages were
            // already gates, so there's never a content leak. Body {serverId}.
            app.MapPost("/api/server/delete", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in required\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                if (!int.TryParse(ExtractJsonNumber(body, "serverId") ?? "", out var sid) || sid <= 0)
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"bad serverId\"}"); return; }
                if (!(roles.RoleOf(sid, caller) >= ServerRole.Admin || isSuper(caller)))
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"server-admin only\"}"); return; }
                // Confirm it exists (RemoveServer can't distinguish "not found" from
                // "no channels" — both yield an empty array).
                var exists = false;
                foreach (var sv in servers.ListServers()) if (sv.Id == sid) { exists = true; break; }
                if (!exists)
                { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("{\"error\":\"no such server\"}"); return; }
                var wasForum = kinds.KindOf(sid) == ServerKind.Forum;
                var rooms = servers.RemoveServer(sid);   // server record gone
                int erased = 0;
                foreach (var rid in rooms)
                {
                    var isTopic = wasForum && forum.IsTopic(rid);
                    if (chat.RemoveRoom(rid)) erased++;
                    // Overwrite the certified /t/{rid} page now that the room is gone.
                    if (isTopic) certifyTopic(rid);
                }
                Reply.Print($"[server-delete] {AdminService.ToText(caller)} removed server {sid} ({erased} room(s))");
                await WriteJsonAsync(ctx, "{\"ok\":true,\"serverId\":" + sid + ",\"rooms\":" + erased + "}");
            }).DisableAntiforgery();

            // member add/remove — body {serverId, principal}
            Func<HttpContext, bool, Task> memberMutate = async (ctx, add) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in required\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                if (!int.TryParse(ExtractJsonNumber(body, "serverId") ?? "", out var sid) || sid <= 0)
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"bad serverId\"}"); return; }
                if (!(roles.RoleOf(sid, caller) >= ServerRole.Admin || isSuper(caller)))
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"server-admin only\"}"); return; }
                var pText = ExtractJsonString(body, "principal");
                if (string.IsNullOrEmpty(pText))
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"missing principal\"}"); return; }
                byte[] target;
                try { target = AdminService.FromText(pText!); }
                catch (Exception ex)
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"" + EscapeJson(ex.Message) + "\"}"); return; }
                var ok = add ? membership.AddMember(sid, target) : membership.RemoveMember(sid, target);
                Reply.Print($"[member-{(add ? "add" : "remove")}] {AdminService.ToText(caller)} server {sid} {pText}");
                await WriteJsonAsync(ctx, "{\"ok\":" + (ok ? "true" : "false") + "}");
            };
            app.MapPost("/api/server/member/add", (HttpContext ctx) => memberMutate(ctx, true)).DisableAntiforgery();
            app.MapPost("/api/server/member/remove", (HttpContext ctx) => memberMutate(ctx, false)).DisableAntiforgery();

            // roster — SIGNED, manager-gated (the member list IS sensitive, unlike public role lists).
            app.MapPost("/api/server/members", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in required\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                if (!int.TryParse(ExtractJsonNumber(body, "serverId") ?? "", out var sid) || sid <= 0)
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"bad serverId\"}"); return; }
                if (!(roles.RoleOf(sid, caller) >= ServerRole.Admin || isSuper(caller)))
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"server-admin only\"}"); return; }
                var list = membership.ListMembers(sid);
                var sb = new StringBuilder();
                sb.Append("{\"serverId\":").Append(sid).Append(",\"private\":").Append(membership.IsPrivate(sid) ? "true" : "false").Append(",\"members\":[");
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var pt = AdminService.ToText(list[i]);
                    var b = identity.Lookup(pt);
                    sb.Append("{\"principal\":\"").Append(EscapeJson(pt)).Append("\",\"name\":\"").Append(EscapeJson(b?.DisplayName ?? "")).Append("\"}");
                }
                sb.Append("]}");
                await WriteJsonAsync(ctx, sb.ToString());
            }).DisableAntiforgery();

            // roles list: public query — who holds which role on a server (role lists
            // are not secret). GET /api/server/roles?serverId=<id>.
            IcResponseCertV2.RegisterPassThroughPath("/api/server/roles", "GET");
            IcServer.RegisterQueryHandler("/api/server/roles", (req) =>
            {
                if (req.Method != "GET") return null;
                int.TryParse(ExtractQueryParam(req.Url, "serverId") ?? "0", out var sid);
                var grants = roles.ListGrants(sid);
                var sb = new StringBuilder();
                sb.Append("{\"serverId\":").Append(sid).Append(",\"roles\":[");
                for (int i = 0; i < grants.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var g = grants[i];
                    var binding = identity.Lookup(AdminService.ToText(g.Principal));
                    sb.Append("{\"principal\":\"").Append(EscapeJson(AdminService.ToText(g.Principal)))
                      .Append("\",\"name\":\"").Append(EscapeJson(binding?.DisplayName ?? ""))
                      .Append("\",\"role\":\"").Append(g.Role.ToString().ToLowerInvariant()).Append("\"}");
                }
                sb.Append("]}");
                return (Encoding.UTF8.GetBytes(sb.ToString()), "application/json; charset=utf-8");
            });

            // ─── Direct messages — HONESTLY private, SIGNED UPDATE CALLS ONLY ────
            // NO RegisterQueryHandler / NO RegisterPassThroughPath here — that would
            // expose reads on the anonymous query channel (privacy-by-obscurity, a
            // lie). All three authenticate via AdminService.CurrentCaller() (the
            // signed @dfinity/agent update-call msg_caller) and reject anonymous
            // (caller.Length == 0) with 401. Same posture as /api/admin/stable_edit.
            //   POST /api/dm/send    body={"peer":"<principal text>","text":".."}
            //   POST /api/dm/threads body={}
            //   POST /api/dm/read     body={"peer":"<principal text>"}
            app.MapPost("/api/dm/send", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in required\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                var peer = ExtractJsonString(body, "peer") ?? "";
                var text = ExtractJsonString(body, "text") ?? "";
                var ok = dms.SendDm(caller, peer, text);
                if (!ok) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"could not send\"}"); return; }
                Reply.Print($"[dm] {AdminService.ToText(caller)} -> {peer} ({text.Length} chars)");
                await WriteJsonAsync(ctx, "{\"ok\":true}");
            }).DisableAntiforgery();

            app.MapPost("/api/dm/threads", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in required\"}"); return; }
                var threads = dms.ThreadsFor(caller);
                var sb = new StringBuilder();
                sb.Append("{\"threads\":[");
                for (int i = 0; i < threads.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var t = threads[i];
                    var binding = identity.Lookup(t.Peer);
                    var peerName = binding?.DisplayName ?? "";
                    sb.Append("{\"peer\":\"").Append(EscapeJson(t.Peer))
                      .Append("\",\"name\":\"").Append(EscapeJson(peerName))
                      .Append("\",\"lastText\":\"").Append(EscapeJson(t.LastText))
                      .Append("\",\"lastAtMs\":").Append(t.LastAtMs)
                      .Append(",\"count\":").Append(t.MsgCount).Append('}');
                }
                sb.Append("]}");
                await WriteJsonAsync(ctx, sb.ToString());
            }).DisableAntiforgery();

            app.MapPost("/api/dm/read", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (AdminService.IsAnonymous(caller))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{\"error\":\"sign in required\"}"); return; }
                var callerText = AdminService.ToText(caller);
                var body = await ReadBodyAsync(ctx);
                var peer = ExtractJsonString(body, "peer") ?? "";
                var msgs = dms.ReadThread(caller, peer);
                var sb = new StringBuilder();
                sb.Append("{\"peer\":\"").Append(EscapeJson(peer)).Append("\",\"messages\":[");
                for (int i = 0; i < msgs.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var m = msgs[i];
                    sb.Append("{\"from\":\"").Append(EscapeJson(m.SenderPrincipal))
                      .Append("\",\"mine\":").Append(m.SenderPrincipal == callerText ? "true" : "false")
                      .Append(",\"atMs\":").Append(m.AtMs)
                      .Append(",\"text\":\"").Append(EscapeJson(m.Text)).Append("\"}");
                }
                sb.Append("]}");
                await WriteJsonAsync(ctx, sb.ToString());
            }).DisableAntiforgery();


            IcResponseCertV2.RegisterPassThroughPath("/api/crm/presence-get", "GET");
            IcServer.RegisterQueryHandler("/api/crm/presence-get", (req) =>
            {
                if (req.Method != "GET") return null;
                var rid = ExtractQueryParam(req.Url, "recordId");
                if (rid is null) return null;
                var list = presence.Viewers(rid);
                var sb = new StringBuilder();
                sb.Append("{\"viewers\":[");
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var v = list[i];
                    sb.Append("{\"principal\":\"").Append(EscapeJson(v.Principal))
                      .Append("\",\"name\":\"").Append(EscapeJson(v.Name))
                      .Append("\",\"lastSeenMs\":").Append(v.LastSeenMs).Append('}');
                }
                sb.Append("]}");
                return (Encoding.UTF8.GetBytes(sb.ToString()), "application/json; charset=utf-8");
            });

            // ─── Companies ────────────────────────────────────────
            IcResponseCertV2.RegisterPassThroughPath("/api/crm/companies", "GET");
            IcServer.RegisterQueryHandler("/api/crm/companies", (req) =>
            {
                if (req.Method != "GET") return null;
                var items = crm.AllCompanies();
                var sb = new StringBuilder();
                sb.Append("{\"items\":[");
                for (int i = 0; i < items.Count; i++) { if (i>0) sb.Append(','); SerializeCompany(sb, items[i]); }
                sb.Append("],\"total\":").Append(items.Count).Append('}');
                return (Encoding.UTF8.GetBytes(sb.ToString()), "application/json; charset=utf-8");
            });
            IcResponseCertV2.RegisterPassThroughPath("/api/crm/company-get", "GET");
            IcServer.RegisterQueryHandler("/api/crm/company-get", (req) =>
            {
                if (req.Method != "GET") return null;
                var ids = ExtractQueryParam(req.Url, "id");
                if (ids is null || !long.TryParse(ids, out var id)) return null;
                var c = crm.FindCompany(id);
                if (c is null) return null;
                var sb = new StringBuilder(); SerializeCompany(sb, c);
                return (Encoding.UTF8.GetBytes(sb.ToString()), "application/json; charset=utf-8");
            });
            app.MapPost("/api/crm/companies", async (HttpContext ctx) =>
            {
                var c = await ReadCompanyFromBodyAsync(ctx);
                if (c is null) { ctx.Response.StatusCode = 400; return; }
                var saved = crm.CreateCompany(c);
                var sb = new StringBuilder(); SerializeCompany(sb, saved);
                await WriteJsonAsync(ctx, sb.ToString());
            }).DisableAntiforgery();
            app.MapMethods("/api/crm/companies/{id:long}", new[] { "PUT" }, async (HttpContext ctx) =>
            {
                if (!long.TryParse(ctx.Request.RouteValues["id"]?.ToString(), out var id))
                { ctx.Response.StatusCode = 400; return; }
                var d = await ReadCompanyFromBodyAsync(ctx);
                if (d is null) { ctx.Response.StatusCode = 400; return; }
                var saved = crm.UpdateCompany(id, d);
                if (saved is null) { ctx.Response.StatusCode = 404; return; }
                var sb = new StringBuilder(); SerializeCompany(sb, saved);
                await WriteJsonAsync(ctx, sb.ToString());
            }).DisableAntiforgery();
            app.MapMethods("/api/crm/companies/{id:long}", new[] { "DELETE" }, (HttpContext ctx) =>
            {
                if (!long.TryParse(ctx.Request.RouteValues["id"]?.ToString(), out var id))
                { ctx.Response.StatusCode = 400; return Task.CompletedTask; }
                ctx.Response.StatusCode = crm.DeleteCompany(id) ? 200 : 404;
                return Task.CompletedTask;
            }).DisableAntiforgery();

            // ─── Deals ────────────────────────────────────────────
            IcResponseCertV2.RegisterPassThroughPath("/api/crm/deals", "GET");
            IcServer.RegisterQueryHandler("/api/crm/deals", (req) =>
            {
                if (req.Method != "GET") return null;
                var items = crm.AllDeals();
                var sb = new StringBuilder();
                sb.Append("{\"items\":[");
                for (int i = 0; i < items.Count; i++) { if (i>0) sb.Append(','); SerializeDeal(sb, items[i]); }
                sb.Append("],\"total\":").Append(items.Count).Append('}');
                return (Encoding.UTF8.GetBytes(sb.ToString()), "application/json; charset=utf-8");
            });
            IcResponseCertV2.RegisterPassThroughPath("/api/crm/deal-get", "GET");
            IcServer.RegisterQueryHandler("/api/crm/deal-get", (req) =>
            {
                if (req.Method != "GET") return null;
                var ids = ExtractQueryParam(req.Url, "id");
                if (ids is null || !long.TryParse(ids, out var id)) return null;
                var d = crm.FindDeal(id);
                if (d is null) return null;
                var sb = new StringBuilder(); SerializeDeal(sb, d);
                return (Encoding.UTF8.GetBytes(sb.ToString()), "application/json; charset=utf-8");
            });
            app.MapPost("/api/crm/deals", async (HttpContext ctx) =>
            {
                var d = await ReadDealFromBodyAsync(ctx);
                if (d is null) { ctx.Response.StatusCode = 400; return; }
                var saved = crm.CreateDeal(d);
                var sb = new StringBuilder(); SerializeDeal(sb, saved);
                await WriteJsonAsync(ctx, sb.ToString());
            }).DisableAntiforgery();
            app.MapMethods("/api/crm/deals/{id:long}", new[] { "PUT" }, async (HttpContext ctx) =>
            {
                if (!long.TryParse(ctx.Request.RouteValues["id"]?.ToString(), out var id))
                { ctx.Response.StatusCode = 400; return; }
                var d = await ReadDealFromBodyAsync(ctx);
                if (d is null) { ctx.Response.StatusCode = 400; return; }
                var saved = crm.UpdateDeal(id, d);
                if (saved is null) { ctx.Response.StatusCode = 404; return; }
                var sb = new StringBuilder(); SerializeDeal(sb, saved);
                await WriteJsonAsync(ctx, sb.ToString());
            }).DisableAntiforgery();
            app.MapMethods("/api/crm/deals/{id:long}", new[] { "DELETE" }, (HttpContext ctx) =>
            {
                if (!long.TryParse(ctx.Request.RouteValues["id"]?.ToString(), out var id))
                { ctx.Response.StatusCode = 400; return Task.CompletedTask; }
                ctx.Response.StatusCode = crm.DeleteDeal(id) ? 200 : 404;
                return Task.CompletedTask;
            }).DisableAntiforgery();
            // Drag-to-stage on the kanban — light-weight PATCH-style endpoint.
            app.MapPost("/api/crm/deal-stage", async (HttpContext ctx) =>
            {
                using var ms = new System.IO.MemoryStream();
                await ctx.Request.Body.CopyToAsync(ms);
                var json = Encoding.UTF8.GetString(ms.ToArray());
                if (!long.TryParse(ExtractJsonNumber(json, "id"), out var id)) { ctx.Response.StatusCode = 400; return; }
                if (!int.TryParse(ExtractJsonNumber(json, "stage"), out var stage)) { ctx.Response.StatusCode = 400; return; }
                var d = crm.FindDeal(id);
                if (d is null) { ctx.Response.StatusCode = 404; return; }
                var saved = crm.UpdateDeal(id, d with { Stage = stage });
                var sb = new StringBuilder(); SerializeDeal(sb, saved!);
                await WriteJsonAsync(ctx, sb.ToString());
            }).DisableAntiforgery();

            // ─── Activities ───────────────────────────────────────
            IcResponseCertV2.RegisterPassThroughPath("/api/crm/activities", "GET");
            IcServer.RegisterQueryHandler("/api/crm/activities", (req) =>
            {
                if (req.Method != "GET") return null;
                long.TryParse(ExtractQueryParam(req.Url, "contactId") ?? "0", out var cid);
                long.TryParse(ExtractQueryParam(req.Url, "dealId") ?? "0", out var did);
                var items = crm.ActivitiesFor(cid, did);
                var sb = new StringBuilder();
                sb.Append("{\"items\":[");
                for (int i = 0; i < items.Count; i++) { if (i>0) sb.Append(','); SerializeActivity(sb, items[i]); }
                sb.Append("],\"total\":").Append(items.Count).Append('}');
                return (Encoding.UTF8.GetBytes(sb.ToString()), "application/json; charset=utf-8");
            });
            app.MapPost("/api/crm/activities", async (HttpContext ctx) =>
            {
                var a = await ReadActivityFromBodyAsync(ctx);
                if (a is null) { ctx.Response.StatusCode = 400; return; }
                var saved = crm.CreateActivity(a);
                var sb = new StringBuilder(); SerializeActivity(sb, saved);
                await WriteJsonAsync(ctx, sb.ToString());
            }).DisableAntiforgery();
            app.MapMethods("/api/crm/activities/{id:long}", new[] { "DELETE" }, (HttpContext ctx) =>
            {
                if (!long.TryParse(ctx.Request.RouteValues["id"]?.ToString(), out var id))
                { ctx.Response.StatusCode = 400; return Task.CompletedTask; }
                ctx.Response.StatusCode = crm.DeleteActivity(id) ? 200 : 404;
                return Task.CompletedTask;
            }).DisableAntiforgery();

            // ─── Tasks ────────────────────────────────────────────
            IcResponseCertV2.RegisterPassThroughPath("/api/crm/tasks", "GET");
            IcServer.RegisterQueryHandler("/api/crm/tasks", (req) =>
            {
                if (req.Method != "GET") return null;
                var items = crm.AllTasks();
                var sb = new StringBuilder();
                sb.Append("{\"items\":[");
                for (int i = 0; i < items.Count; i++) { if (i>0) sb.Append(','); SerializeTask(sb, items[i]); }
                sb.Append("],\"total\":").Append(items.Count).Append('}');
                return (Encoding.UTF8.GetBytes(sb.ToString()), "application/json; charset=utf-8");
            });
            app.MapPost("/api/crm/tasks", async (HttpContext ctx) =>
            {
                var t = await ReadTaskFromBodyAsync(ctx);
                if (t is null) { ctx.Response.StatusCode = 400; return; }
                var saved = crm.CreateTask(t);
                var sb = new StringBuilder(); SerializeTask(sb, saved);
                await WriteJsonAsync(ctx, sb.ToString());
            }).DisableAntiforgery();
            app.MapMethods("/api/crm/tasks/{id:long}", new[] { "PUT" }, async (HttpContext ctx) =>
            {
                if (!long.TryParse(ctx.Request.RouteValues["id"]?.ToString(), out var id))
                { ctx.Response.StatusCode = 400; return; }
                var d = await ReadTaskFromBodyAsync(ctx);
                if (d is null) { ctx.Response.StatusCode = 400; return; }
                var saved = crm.UpdateTask(id, d);
                if (saved is null) { ctx.Response.StatusCode = 404; return; }
                var sb = new StringBuilder(); SerializeTask(sb, saved);
                await WriteJsonAsync(ctx, sb.ToString());
            }).DisableAntiforgery();
            app.MapMethods("/api/crm/tasks/{id:long}", new[] { "DELETE" }, (HttpContext ctx) =>
            {
                if (!long.TryParse(ctx.Request.RouteValues["id"]?.ToString(), out var id))
                { ctx.Response.StatusCode = 400; return Task.CompletedTask; }
                ctx.Response.StatusCode = crm.DeleteTask(id) ? 200 : 404;
                return Task.CompletedTask;
            }).DisableAntiforgery();

            // ─── Lead score (computed on the fly) ─────────────────
            IcResponseCertV2.RegisterPassThroughPath("/api/crm/lead-score", "GET");
            IcServer.RegisterQueryHandler("/api/crm/lead-score", (req) =>
            {
                if (req.Method != "GET") return null;
                var ids = ExtractQueryParam(req.Url, "contactId");
                if (ids is null || !long.TryParse(ids, out var id)) return null;
                var score = crm.LeadScore(id);
                var resp = "{\"contactId\":" + id + ",\"score\":" + score + "}";
                return (Encoding.UTF8.GetBytes(resp), "application/json; charset=utf-8");
            });

            // ─── Dashboard aggregates ─────────────────────────────
            IcResponseCertV2.RegisterPassThroughPath("/api/crm/dashboard", "GET");
            IcServer.RegisterQueryHandler("/api/crm/dashboard", (req) =>
            {
                if (req.Method != "GET") return null;
                var deals = crm.AllDeals();
                var tasks = crm.AllTasks();
                var contacts = crm.All();
                var now = (long)(Ic0.time() / 1_000_000UL);
                long monthStart = now - 30L * 24 * 3600 * 1000;
                // Per-stage totals + weighted pipeline.
                long[] stageValueCents = new long[6];
                int[]  stageCount      = new int[6];
                long   weightedCents   = 0;
                long   wonMonthCents   = 0;
                foreach (var d in deals)
                {
                    var s = Math.Max(0, Math.Min(5, d.Stage));
                    stageValueCents[s] += d.ValueCents;
                    stageCount[s]++;
                    weightedCents += d.ValueCents * CrmService.StageProbability[s] / 100;
                    if (s == (int)CrmService.DealStage.Won && d.StageChangedAtMs >= monthStart)
                        wonMonthCents += d.ValueCents;
                }
                int overdueTasks = 0, todayTasks = 0;
                long dayMs = 24L * 3600 * 1000;
                foreach (var t in tasks)
                {
                    if (t.Done) continue;
                    if (t.DueAtMs == 0) continue;
                    if (t.DueAtMs < now) overdueTasks++;
                    else if (t.DueAtMs - now < dayMs) todayTasks++;
                }
                // Hottest 5 leads.
                var ranked = new List<(long id, string name, int score)>();
                foreach (var c in contacts)
                {
                    var sc = crm.LeadScore(c.Id);
                    if (sc > 0) ranked.Add((c.Id, (c.FirstName + " " + c.LastName).Trim(), sc));
                }
                ranked.Sort((a, b) => b.score.CompareTo(a.score));
                var sb = new StringBuilder();
                sb.Append("{\"stages\":[");
                for (int i = 0; i < 6; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"name\":\"").Append(CrmService.StageNames[i])
                      .Append("\",\"count\":").Append(stageCount[i])
                      .Append(",\"valueCents\":").Append(stageValueCents[i])
                      .Append(",\"probability\":").Append(CrmService.StageProbability[i]).Append('}');
                }
                sb.Append("],\"weightedCents\":").Append(weightedCents)
                  .Append(",\"wonThisMonthCents\":").Append(wonMonthCents)
                  .Append(",\"overdueTasks\":").Append(overdueTasks)
                  .Append(",\"todayTasks\":").Append(todayTasks)
                  .Append(",\"contactCount\":").Append(contacts.Count)
                  .Append(",\"hotLeads\":[");
                for (int i = 0; i < Math.Min(5, ranked.Count); i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"id\":").Append(ranked[i].id)
                      .Append(",\"name\":\"").Append(EscapeJson(ranked[i].name))
                      .Append("\",\"score\":").Append(ranked[i].score).Append('}');
                }
                sb.Append("]}");
                return (Encoding.UTF8.GetBytes(sb.ToString()), "application/json; charset=utf-8");
            });

            // ─── Global Cmd-K search ──────────────────────────────
            IcResponseCertV2.RegisterPassThroughPath("/api/crm/search", "GET");
            IcServer.RegisterQueryHandler("/api/crm/search", (req) =>
            {
                if (req.Method != "GET") return null;
                var q = (ExtractQueryParam(req.Url, "q") ?? "").Trim().ToLowerInvariant();
                var sb = new StringBuilder();
                sb.Append("{\"contacts\":[");
                int n = 0;
                if (q.Length > 0)
                {
                    foreach (var c in crm.Search(q))
                    {
                        if (n++ > 0) sb.Append(',');
                        sb.Append("{\"id\":").Append(c.Id)
                          .Append(",\"label\":\"").Append(EscapeJson((c.FirstName + " " + c.LastName).Trim()))
                          .Append("\",\"sub\":\"").Append(EscapeJson(c.Email)).Append("\"}");
                        if (n >= 8) break;
                    }
                }
                sb.Append("],\"companies\":[");
                n = 0;
                if (q.Length > 0) foreach (var c in crm.AllCompanies())
                {
                    if (!c.Name.ToLowerInvariant().Contains(q) &&
                        !c.Industry.ToLowerInvariant().Contains(q)) continue;
                    if (n++ > 0) sb.Append(',');
                    sb.Append("{\"id\":").Append(c.Id)
                      .Append(",\"label\":\"").Append(EscapeJson(c.Name))
                      .Append("\",\"sub\":\"").Append(EscapeJson(c.Industry)).Append("\"}");
                    if (n >= 8) break;
                }
                sb.Append("],\"deals\":[");
                n = 0;
                if (q.Length > 0) foreach (var d in crm.AllDeals())
                {
                    if (!d.Title.ToLowerInvariant().Contains(q)) continue;
                    if (n++ > 0) sb.Append(',');
                    sb.Append("{\"id\":").Append(d.Id)
                      .Append(",\"label\":\"").Append(EscapeJson(d.Title))
                      .Append("\",\"sub\":\"").Append(CrmService.StageNames[Math.Max(0,Math.Min(5,d.Stage))]).Append("\"}");
                    if (n >= 8) break;
                }
                sb.Append("]}");
                return (Encoding.UTF8.GetBytes(sb.ToString()), "application/json; charset=utf-8");
            });

            // ─── /stable admin endpoints ──────────────────────────
            // Auth model:
            //   /api/admin/whoami       — public; returns caller principal
            //                             + isAdmin + isController. The
            //                             /stable page reads it to decide
            //                             whether to render edit inputs.
            //   /api/admin/list_admins  — public; returns the allowlist.
            //   /api/admin/add_admin    — gated by ic0.is_controller. The
            //                             canister controller seeds the
            //                             first II principal via dfx.
            //   /api/admin/remove_admin — same gate.
            //   /api/admin/stable_edit  — gated by AdminService.IsAdmin
            //                             on msg_caller. The browser
            //                             must use @dfinity/agent to make
            //                             a signed update call so caller
            //                             is the II principal (a plain
            //                             fetch() lands as anonymous).
            app.MapGet("/api/admin/whoami", async (HttpContext ctx) =>
            {
                var raw = AdminService.CurrentCaller();
                var sb = new StringBuilder();
                sb.Append("{\"principal\":\"").Append(AdminService.ToText(raw)).Append('"');
                sb.Append(",\"isAdmin\":").Append(admins.IsAdmin(raw) ? "true" : "false");
                sb.Append(",\"isController\":").Append(AdminService.IsCurrentCallerController() ? "true" : "false");
                sb.Append('}');
                await WriteJsonAsync(ctx, sb.ToString());
            });

            app.MapGet("/api/admin/list_admins", async (HttpContext ctx) =>
            {
                var sb = new StringBuilder();
                sb.Append("{\"admins\":[");
                int n = 0;
                foreach (var p in admins.List())
                {
                    if (n++ > 0) sb.Append(',');
                    sb.Append('"').Append(AdminService.ToText(p)).Append('"');
                }
                sb.Append("]}");
                await WriteJsonAsync(ctx, sb.ToString());
            });

            app.MapPost("/api/admin/add_admin", async (HttpContext ctx) =>
            {
                if (!AdminService.IsCurrentCallerController())
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"controller only\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                var text = ExtractJsonString(body, "principal");
                if (string.IsNullOrEmpty(text))
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"missing principal\"}"); return; }
                byte[] raw;
                try { raw = AdminService.FromText(text!); }
                catch (Exception ex)
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"" + EscapeJson(ex.Message) + "\"}"); return; }
                var added = admins.Add(raw);
                await WriteJsonAsync(ctx, "{\"ok\":" + (added ? "true" : "false") + "}");
            }).DisableAntiforgery();

            app.MapPost("/api/admin/remove_admin", async (HttpContext ctx) =>
            {
                if (!AdminService.IsCurrentCallerController())
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"controller only\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                var text = ExtractJsonString(body, "principal");
                if (string.IsNullOrEmpty(text))
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"missing principal\"}"); return; }
                byte[] raw;
                try { raw = AdminService.FromText(text!); }
                catch (Exception ex)
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"" + EscapeJson(ex.Message) + "\"}"); return; }
                var removed = admins.Remove(raw);
                await WriteJsonAsync(ctx, "{\"ok\":" + (removed ? "true" : "false") + "}");
            }).DisableAntiforgery();

            app.MapPost("/api/admin/stable_edit", async (HttpContext ctx) =>
            {
                var caller = AdminService.CurrentCaller();
                if (!admins.IsAdmin(caller))
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("{\"error\":\"not an admin\"}"); return; }
                var body = await ReadBodyAsync(ctx);
                var collection = ExtractJsonString(body, "collection");
                var keyId = ExtractJsonString(body, "key");
                if (string.IsNullOrEmpty(collection) || string.IsNullOrEmpty(keyId))
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"missing collection or key\"}"); return; }
                var col = StableExplorer.Find(collection!);
                if (col is null)
                { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("{\"error\":\"no such collection\"}"); return; }
                // Body shape: {"collection":"...","key":"...","patch":{"field":"value",...}}
                var patch = ExtractJsonStringDict(body, "patch");
                var err = col.TryEdit(keyId!, patch);
                if (err is not null)
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"" + EscapeJson(err) + "\"}"); return; }
                Reply.Print($"[stable-edit] {AdminService.ToText(caller)} edited {collection}/{keyId}");
                await WriteJsonAsync(ctx, "{\"ok\":true}");
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

    private static void SerializeContact(StringBuilder sb, CrmService.Contact c)
    {
        sb.Append("{\"id\":").Append(c.Id)
          .Append(",\"firstName\":\"").Append(EscapeJson(c.FirstName))
          .Append("\",\"lastName\":\"").Append(EscapeJson(c.LastName))
          .Append("\",\"email\":\"").Append(EscapeJson(c.Email))
          .Append("\",\"phone\":\"").Append(EscapeJson(c.Phone))
          .Append("\",\"company\":\"").Append(EscapeJson(c.Company))
          .Append("\",\"title\":\"").Append(EscapeJson(c.Title))
          .Append("\",\"notes\":\"").Append(EscapeJson(c.Notes))
          .Append("\",\"tags\":\"").Append(EscapeJson(c.Tags))
          .Append("\",\"createdAtMs\":").Append(c.CreatedAtMs)
          .Append(",\"updatedAtMs\":").Append(c.UpdatedAtMs)
          .Append('}');
    }

    private static async Task<CrmService.Contact?> ReadContactFromBodyAsync(HttpContext ctx)
    {
        using var ms = new System.IO.MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        var json = Encoding.UTF8.GetString(ms.ToArray());
        if (string.IsNullOrWhiteSpace(json)) return null;
        var idStr = ExtractJsonNumber(json, "id");
        long.TryParse(idStr, out var id);
        return new CrmService.Contact(
            id,
            ExtractJsonString(json, "firstName") ?? "",
            ExtractJsonString(json, "lastName")  ?? "",
            ExtractJsonString(json, "email")     ?? "",
            ExtractJsonString(json, "phone")     ?? "",
            ExtractJsonString(json, "company")   ?? "",
            ExtractJsonString(json, "title")     ?? "",
            ExtractJsonString(json, "notes")     ?? "",
            ExtractJsonString(json, "tags")      ?? "",
            0, 0);
    }

    private static void SerializeCompany(StringBuilder sb, CrmService.Company c)
    {
        sb.Append("{\"id\":").Append(c.Id)
          .Append(",\"name\":\"").Append(EscapeJson(c.Name))
          .Append("\",\"industry\":\"").Append(EscapeJson(c.Industry))
          .Append("\",\"website\":\"").Append(EscapeJson(c.Website))
          .Append("\",\"size\":\"").Append(EscapeJson(c.Size))
          .Append("\",\"notes\":\"").Append(EscapeJson(c.Notes))
          .Append("\",\"createdAtMs\":").Append(c.CreatedAtMs)
          .Append(",\"updatedAtMs\":").Append(c.UpdatedAtMs)
          .Append('}');
    }

    private static async Task<CrmService.Company?> ReadCompanyFromBodyAsync(HttpContext ctx)
    {
        using var ms = new System.IO.MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        var json = Encoding.UTF8.GetString(ms.ToArray());
        if (string.IsNullOrWhiteSpace(json)) return null;
        long.TryParse(ExtractJsonNumber(json, "id") ?? "0", out var id);
        return new CrmService.Company(id,
            ExtractJsonString(json, "name") ?? "",
            ExtractJsonString(json, "industry") ?? "",
            ExtractJsonString(json, "website") ?? "",
            ExtractJsonString(json, "size") ?? "",
            ExtractJsonString(json, "notes") ?? "",
            0, 0);
    }

    private static void SerializeDeal(StringBuilder sb, CrmService.Deal d)
    {
        var s = Math.Max(0, Math.Min(5, d.Stage));
        sb.Append("{\"id\":").Append(d.Id)
          .Append(",\"title\":\"").Append(EscapeJson(d.Title))
          .Append("\",\"contactId\":").Append(d.ContactId)
          .Append(",\"companyId\":").Append(d.CompanyId)
          .Append(",\"valueCents\":").Append(d.ValueCents)
          .Append(",\"stage\":").Append(s)
          .Append(",\"stageName\":\"").Append(CrmService.StageNames[s])
          .Append("\",\"probability\":").Append(CrmService.StageProbability[s])
          .Append(",\"expectedCloseAtMs\":").Append(d.ExpectedCloseAtMs)
          .Append(",\"stageChangedAtMs\":").Append(d.StageChangedAtMs)
          .Append(",\"createdAtMs\":").Append(d.CreatedAtMs)
          .Append(",\"updatedAtMs\":").Append(d.UpdatedAtMs)
          .Append('}');
    }

    private static async Task<CrmService.Deal?> ReadDealFromBodyAsync(HttpContext ctx)
    {
        using var ms = new System.IO.MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        var json = Encoding.UTF8.GetString(ms.ToArray());
        if (string.IsNullOrWhiteSpace(json)) return null;
        long.TryParse(ExtractJsonNumber(json, "id") ?? "0", out var id);
        long.TryParse(ExtractJsonNumber(json, "contactId") ?? "0", out var cid);
        long.TryParse(ExtractJsonNumber(json, "companyId") ?? "0", out var coid);
        long.TryParse(ExtractJsonNumber(json, "valueCents") ?? "0", out var val);
        int.TryParse(ExtractJsonNumber(json, "stage") ?? "0", out var stage);
        long.TryParse(ExtractJsonNumber(json, "expectedCloseAtMs") ?? "0", out var ec);
        return new CrmService.Deal(id,
            ExtractJsonString(json, "title") ?? "",
            cid, coid, val, stage, ec, 0, 0, 0);
    }

    private static void SerializeActivity(StringBuilder sb, CrmService.Activity a)
    {
        var t = Math.Max(0, Math.Min(3, a.Type));
        sb.Append("{\"id\":").Append(a.Id)
          .Append(",\"type\":").Append(t)
          .Append(",\"typeName\":\"").Append(CrmService.ActivityTypeNames[t])
          .Append("\",\"subject\":\"").Append(EscapeJson(a.Subject))
          .Append("\",\"body\":\"").Append(EscapeJson(a.Body))
          .Append("\",\"contactId\":").Append(a.ContactId)
          .Append(",\"dealId\":").Append(a.DealId)
          .Append(",\"createdBy\":\"").Append(EscapeJson(a.CreatedBy))
          .Append("\",\"atMs\":").Append(a.AtMs)
          .Append('}');
    }

    private static async Task<CrmService.Activity?> ReadActivityFromBodyAsync(HttpContext ctx)
    {
        using var ms = new System.IO.MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        var json = Encoding.UTF8.GetString(ms.ToArray());
        if (string.IsNullOrWhiteSpace(json)) return null;
        int.TryParse(ExtractJsonNumber(json, "type") ?? "0", out var t);
        long.TryParse(ExtractJsonNumber(json, "contactId") ?? "0", out var cid);
        long.TryParse(ExtractJsonNumber(json, "dealId") ?? "0", out var did);
        long.TryParse(ExtractJsonNumber(json, "atMs") ?? "0", out var at);
        return new CrmService.Activity(0, t,
            ExtractJsonString(json, "subject") ?? "",
            ExtractJsonString(json, "body") ?? "",
            cid, did,
            ExtractJsonString(json, "createdBy") ?? "Guest",
            at);
    }

    private static void SerializeTask(StringBuilder sb, CrmService.TaskItem t)
    {
        var p = Math.Max(0, Math.Min(2, t.Priority));
        sb.Append("{\"id\":").Append(t.Id)
          .Append(",\"title\":\"").Append(EscapeJson(t.Title))
          .Append("\",\"notes\":\"").Append(EscapeJson(t.Notes))
          .Append("\",\"contactId\":").Append(t.ContactId)
          .Append(",\"dealId\":").Append(t.DealId)
          .Append(",\"dueAtMs\":").Append(t.DueAtMs)
          .Append(",\"done\":").Append(t.Done ? "true" : "false")
          .Append(",\"priority\":").Append(p)
          .Append(",\"priorityName\":\"").Append(CrmService.TaskPriorityNames[p])
          .Append("\",\"createdBy\":\"").Append(EscapeJson(t.CreatedBy))
          .Append("\",\"createdAtMs\":").Append(t.CreatedAtMs)
          .Append(",\"updatedAtMs\":").Append(t.UpdatedAtMs)
          .Append('}');
    }

    private static async Task<CrmService.TaskItem?> ReadTaskFromBodyAsync(HttpContext ctx)
    {
        using var ms = new System.IO.MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        var json = Encoding.UTF8.GetString(ms.ToArray());
        if (string.IsNullOrWhiteSpace(json)) return null;
        long.TryParse(ExtractJsonNumber(json, "id") ?? "0", out var id);
        long.TryParse(ExtractJsonNumber(json, "contactId") ?? "0", out var cid);
        long.TryParse(ExtractJsonNumber(json, "dealId") ?? "0", out var did);
        long.TryParse(ExtractJsonNumber(json, "dueAtMs") ?? "0", out var due);
        int.TryParse(ExtractJsonNumber(json, "priority") ?? "0", out var prio);
        // crude bool extract — match the field text after the colon
        bool done = json.IndexOf("\"done\":true", StringComparison.Ordinal) >= 0;
        return new CrmService.TaskItem(id,
            ExtractJsonString(json, "title") ?? "",
            ExtractJsonString(json, "notes") ?? "",
            cid, did, due, done, prio,
            ExtractJsonString(json, "createdBy") ?? "Guest",
            0, 0);
    }

    private static async Task WriteJsonAsync(HttpContext ctx, string json)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentLength = bytes.Length;
        await ctx.Response.Body.WriteAsync(bytes);
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

    private static async Task<string> ReadBodyAsync(HttpContext ctx)
    {
        using var ms = new System.IO.MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Extract the contents of a <c>"field":{...}</c> object as
    /// a flat string→string map. Only handles single-line string values
    /// (matches the minimal JSON shape we serialise client-side).</summary>
    private static Dictionary<string, string> ExtractJsonStringDict(string json, string field)
    {
        var result = new Dictionary<string, string>();
        var marker = "\"" + field + "\":{";
        int i = json.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return result;
        i += marker.Length;
        int end = i;
        int depth = 1;
        while (end < json.Length && depth > 0)
        {
            var c = json[end];
            if (c == '"')
            {
                end++;
                while (end < json.Length && json[end] != '"')
                {
                    if (json[end] == '\\' && end + 1 < json.Length) end++;
                    end++;
                }
            }
            else if (c == '{') depth++;
            else if (c == '}') depth--;
            if (depth > 0) end++;
        }
        var inner = "{" + json.Substring(i, end - i) + "}";
        // Pull out every "key":"value" pair from the inner blob.
        int p = 1;
        while (p < inner.Length - 1)
        {
            int ks = inner.IndexOf('"', p);
            if (ks < 0) break;
            int ke = ks + 1;
            while (ke < inner.Length && inner[ke] != '"')
            {
                if (inner[ke] == '\\' && ke + 1 < inner.Length) ke++;
                ke++;
            }
            if (ke >= inner.Length) break;
            var key = inner.Substring(ks + 1, ke - ks - 1);
            int colon = inner.IndexOf(':', ke);
            if (colon < 0) break;
            int vs = inner.IndexOf('"', colon);
            if (vs < 0) break;
            int ve = vs + 1;
            var sb = new StringBuilder();
            while (ve < inner.Length && inner[ve] != '"')
            {
                if (inner[ve] == '\\' && ve + 1 < inner.Length)
                {
                    var esc = inner[ve + 1];
                    sb.Append(esc == 'n' ? '\n' : esc == 't' ? '\t' : esc);
                    ve += 2; continue;
                }
                sb.Append(inner[ve]); ve++;
            }
            result[key] = sb.ToString();
            p = ve + 1;
        }
        return result;
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

    // HTML-escape for raw StringBuilder markup (the shell builders below are
    // not Razor, so they don't auto-escape). Server names allow <>&"' (only
    // '|' and control chars are stripped at creation), so any name rendered
    // into the sidebar MUST go through this.
    private static string HtmlEsc(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c == '&') sb.Append("&amp;");
            else if (c == '<') sb.Append("&lt;");
            else if (c == '>') sb.Append("&gt;");
            else if (c == '"') sb.Append("&quot;");
            else if (c == '\'') sb.Append("&#39;");
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // Sidebar space-icon: first letter + a stable hue from the name (mirrors the
    // avatar colouring used in the feed). No Regex / no LINQ (AOT-safe).
    private static string SpaceGlyph(string name)
        => string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant();
    private static int SpaceHue(string name)
    {
        int h = 0;
        foreach (var c in name) h = (h * 31 + c) & 0xffff;
        return h % 360;
    }

    // Per-deploy build stamp: the ms time of the first call after each canister
    // start. A `--mode upgrade` wipes the heap, so this static resets to 0 and
    // gets a fresh value on the next deploy. It is emitted as data-build on the
    // shell root; wasp.js watches it across render swaps and hard-reloads when it
    // changes, so a long-open tab never shows new markup under a stale <head>.
    private static long _buildStamp = 0;
    private static long BuildStamp()
    {
        if (_buildStamp == 0) _buildStamp = (long)(Ic0.time() / 1_000_000UL);
        return _buildStamp;
    }

    private static void RegisterShell(IWaspRenderer renderer, string path)
    {
        var batch = renderer.Render(new WaspRenderRequest { Path = path });
        var shell = BuildPage(batch.Html);
        var bytes = Encoding.UTF8.GetBytes(shell);
        IcServer.RegisterStaticAsset(path, bytes, "text/html; charset=utf-8");
        IcCertifiedAssets.Insert(path, bytes);
    }

    // Inline line-icon set (Lucide/Feather geometry, MIT) — one source of
    // truth replacing the old cryptic single-glyph nav (±, ☂, ▦, ▼, ⌬…).
    // Single-quoted SVG attrs so these compose cleanly in normal C# strings;
    // stroke=currentColor inherits link colour + active/hover tints for free.
    internal static string Icon(string name)
    {
        string p = name switch
        {
            "home"    => "<path d='M3 9l9-7 9 7v11a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z'/><polyline points='9 22 9 12 15 12 15 22'/>",
            "counter" => "<circle cx='12' cy='12' r='9'/><line x1='12' y1='8' x2='12' y2='16'/><line x1='8' y1='12' x2='16' y2='12'/>",
            "weather" => "<path d='M18 10h-1.26A8 8 0 1 0 9 20h9a5 5 0 0 0 0-10z'/>",
            "chat"    => "<path d='M21 11.5a8.38 8.38 0 0 1-.9 3.8 8.5 8.5 0 0 1-7.6 4.7 8.38 8.38 0 0 1-3.8-.9L3 21l1.9-5.7a8.38 8.38 0 0 1-.9-3.8 8.5 8.5 0 0 1 4.7-7.6 8.38 8.38 0 0 1 3.8-.9h.5a8.48 8.48 0 0 1 8 8v.5z'/>",
            "place"   => "<rect x='3' y='3' width='7' height='7' rx='1'/><rect x='14' y='3' width='7' height='7' rx='1'/><rect x='14' y='14' width='7' height='7' rx='1'/><rect x='3' y='14' width='7' height='7' rx='1'/>",
            "tetris"  => "<rect x='3' y='3' width='8' height='8' rx='1'/><rect x='13' y='9' width='8' height='8' rx='1'/>",
            "crm"     => "<path d='M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2'/><circle cx='9' cy='7' r='4'/><path d='M23 21v-2a4 4 0 0 0-3-3.87'/><path d='M16 3.13a4 4 0 0 1 0 7.75'/>",
            "login"   => "<path d='M15 3h4a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2h-4'/><polyline points='10 17 15 12 10 7'/><line x1='15' y1='12' x2='3' y2='12'/>",
            _          => "<circle cx='12' cy='12' r='9'/>",
        };
        return "<svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='1.9' stroke-linecap='round' stroke-linejoin='round' width='20' height='20' aria-hidden='true'>" + p + "</svg>";
    }

    // Brand wordmark mark: a 5-bar audio waveform tick — the visual of a buzz / signal.
    internal static string WaveMark() =>
        "<span class=\"sg-brand-mark\"><svg width='22' height='22' viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' aria-hidden='true'><line x1='3' y1='10' x2='3' y2='14'/><line x1='8' y1='5' x2='8' y2='19'/><line x1='12' y1='2.5' x2='12' y2='21.5'/><line x1='16' y1='7' x2='16' y2='17'/><line x1='21' y1='11' x2='21' y2='13'/></svg></span>";

    private static string WrapWithSidebar(string currentPath, string innerHtml,
        ServerService servers, ServerKindService kinds, MembershipService membership)
    {
        var normalized = currentPath;
        int q = normalized.IndexOf('?');
        if (q >= 0) normalized = normalized.Substring(0, q);
        if (normalized.Length > 1 && normalized.EndsWith("/")) normalized = normalized.Substring(0, normalized.Length - 1);

        string Active(string p) => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase)
            ? " class=\"active\"" : "";

        // ── Marketing site chrome (the public front door at "/") ──
        // Full-width top-nav + footer, NO app sidebar — a real business site.
        // The app routes (/chat etc.) keep the sidebar shell below.
        if (normalized == "/")
        {
            var m = new StringBuilder();
            m.Append("<div class=\"sg\" data-build=\"").Append(BuildStamp()).Append("\">");
            m.Append("<header class=\"sg-nav\">");
            m.Append("<a class=\"sg-brand\" href=\"/\">").Append(WaveMark()).Append("<span>WASP <em>Bzzz</em></span></a>");
            m.Append("<nav class=\"sg-nav-links\"><a href=\"#features\">Features</a><a href=\"#security\">Security</a><a href=\"#chain\">On-chain</a><a href=\"#verify\">Verify</a></nav>");
            m.Append("<div class=\"sg-nav-actions\">");
            m.Append("<a class=\"sg-nav-open\" href=\"/chat\">Open app →</a>");
            m.Append("<button type=\"button\" class=\"sg-btn sg-btn-primary sg-nav-cta ii-signin\" data-ii-signin><span class=\"ii-mark\">∞</span> Sign in</button>");
            m.Append("<div class=\"ii-card sg-nav-card\" data-ii-card hidden><span class=\"ii-avatar\" data-ii-avatar>?</span><span class=\"ii-meta\"><span class=\"ii-name\" data-ii-name>—</span><button type=\"button\" class=\"ii-signout\" data-ii-signout>Sign out</button></span></div>");
            m.Append("</div></header>");
            m.Append("<main class=\"sg-main\">").Append(innerHtml).Append("</main>");
            m.Append("<footer class=\"sg-footer\">");
            m.Append("<div class=\"sg-foot-top\">");
            m.Append("<div class=\"sg-foot-brand\"><a class=\"sg-brand\" href=\"/\">").Append(WaveMark()).Append("<span>WASP <em>Bzzz</em></span></a><p class=\"sg-foot-tag\">An on-chain community platform. No backend to trust — every message is a signed transaction inside one canister.</p></div>");
            m.Append("<div class=\"sg-foot-cols\">");
            m.Append("<div class=\"sg-foot-col\"><h4>Product</h4><a href=\"/chat\">Open app</a><a href=\"#features\">Features</a><a href=\"#richmsg\">Messages</a></div>");
            m.Append("<div class=\"sg-foot-col\"><h4>On-chain</h4><a href=\"#chain\">Why on-chain</a><a href=\"#security\">Security</a><a href=\"/stable\">Stable memory</a></div>");
            m.Append("<div class=\"sg-foot-col\"><h4>Verify</h4><a href=\"#verify\">Verify it yourself</a><a href=\"/stable\">Certified state</a></div>");
            m.Append("</div></div>");
            m.Append("<div class=\"sg-foot-bottom\"><span>Served from a single canister on the Internet Computer · ").Append(AdminService.CanisterIdText()).Append("</span><span>free · no email · no password</span></div>");
            m.Append("</footer>");
            m.Append("</div>");
            return m.ToString();
        }

        var sb = new StringBuilder();
        sb.Append("<div class=\"page\" data-build=\"").Append(BuildStamp()).Append("\">");
        sb.Append("<aside class=\"sidebar\">");
        sb.Append("<a class=\"brand\" href=\"/\">");
        sb.Append("<span class=\"brand-text\" style=\"display:inline-flex;align-items:center;gap:0.5rem;color:#5B8CFF\">").Append(WaveMark()).Append("</span><span class=\"brand-text\">WASP <span style=\"color:#5B8CFF;font-family:ui-monospace,monospace;font-weight:600\">Bzzz</span></span>");
        sb.Append("</a>");
        // ── Spaces nav: the app's servers grouped by kind (Servers / Forums /
        // Feeds) + a Direct-messages entry. Replaces the old Home/Chat links.
        // Pure Blazor SSR — the list is baked into the certified shell at deploy
        // and refreshes within ~5s via the /_wasp/render poll (the sidebar lives
        // inside #wasp-root). The per-group "+ New" buttons are super-admin-only
        // (revealed by the signed client, same gate as the old create-server +),
        // and the unread dot is toggled by the existing wasp.js bridge.
        string queryStr = q >= 0 ? currentPath.Substring(q + 1) : "";
        int activeS = 0; bool dmActive = false;
        foreach (var part in queryStr.Split('&'))
        {
            if (part.StartsWith("s=", StringComparison.Ordinal)) int.TryParse(part.Substring(2), out activeS);
            else if (part == "dm=1") dmActive = true;
        }
        var spaceServers = servers.ListServers();
        void Group(string heading, ServerKind kind, string routeBase, bool sectionActive, string kindKey, string newLabel)
        {
            sb.Append("<div class=\"spaces-grp\">");
            sb.Append("<div class=\"spaces-h\">").Append(heading).Append("</div>");
            int shown = 0;
            foreach (var srv in spaceServers)
            {
                if (kinds.KindOf(srv.Id) != kind) continue;
                shown++;
                var active = sectionActive && !dmActive && srv.Id == activeS;
                sb.Append("<a class=\"space-item").Append(active ? " active" : "").Append("\" data-server-id=\"").Append(srv.Id).Append("\" href=\"").Append(routeBase).Append("?s=").Append(srv.Id).Append("\">");
                sb.Append("<span class=\"space-ic\" style=\"background:hsl(").Append(SpaceHue(srv.Name)).Append(",46%,42%)\">").Append(HtmlEsc(SpaceGlyph(srv.Name))).Append("</span>");
                sb.Append("<span class=\"space-nm\">").Append(HtmlEsc(srv.Name)).Append("</span>");
                if (membership.IsPrivate(srv.Id)) sb.Append("<span class=\"space-lock\" aria-hidden=\"true\">\U0001F512</span>");
                sb.Append("</a>");
            }
            if (shown == 0) sb.Append("<div class=\"space-empty\">None yet</div>");
            sb.Append("<button type=\"button\" class=\"space-new\" data-new-space=\"").Append(kindKey).Append("\" data-create-server-ui hidden>+ ").Append(newLabel).Append("</button>");
            sb.Append("</div>");
        }
        sb.Append("<nav class=\"spaces\" aria-label=\"Spaces\">");
        Group("Servers", ServerKind.Discussion, "/chat", normalized == "/chat", "discussion", "New server");
        Group("Forums", ServerKind.Forum, "/forum", normalized == "/forum", "forum", "New forum");
        Group("Feeds", ServerKind.Feed, "/feed", normalized == "/feed", "feed", "New feed");
        // Shared create form (hidden; a "+ New X" button sets the kind + reveals it).
        sb.Append("<form class=\"space-create\" data-create-form autocomplete=\"off\" hidden>");
        sb.Append("<input name=\"newServer\" type=\"text\" class=\"space-create-in\" placeholder=\"name\" maxlength=\"20\" data-wasp-keep />");
        sb.Append("<input type=\"hidden\" name=\"newServerKind\" value=\"discussion\" data-wasp-keep />");
        sb.Append("<label class=\"space-create-priv\"><input type=\"checkbox\" name=\"newServerPrivate\" data-wasp-keep /> Private</label>");
        sb.Append("<div class=\"space-create-row\"><button type=\"button\" class=\"space-create-go\" data-create-server>Create</button><button type=\"button\" class=\"space-create-x\" data-create-cancel>Cancel</button></div>");
        sb.Append("</form>");
        sb.Append("</nav>");
        // Direct messages — pinned to the BOTTOM of the nav (outside the scrollable
        // groups), just above the account card.
        sb.Append("<div class=\"spaces-dm\"><a class=\"space-item space-dm").Append(dmActive ? " active" : "").Append("\" href=\"/chat?dm=1\"><span class=\"space-ic space-ic-dm\" aria-hidden=\"true\">✉</span><span class=\"space-nm\">Direct messages</span></a></div>");
        // Internet Identity sign-in — wasp-ii.js swaps these between
        // signed-out / signed-in states on load and on every II event.
        // Server-side we always emit both; CSS hides one based on the
        // [data-ii-state] attribute the JS sets on <body>.
        sb.Append("<div class=\"sidebar-auth\">");
        sb.Append("<button type=\"button\" class=\"ii-signin\" data-ii-signin>");
        sb.Append("<span class=\"ii-mark\">").Append(Icon("login")).Append("</span>Sign in with Internet Identity");
        sb.Append("</button>");
        sb.Append("<div class=\"ii-card\" data-ii-card hidden>");
        sb.Append("<span class=\"ii-avatar\" data-ii-avatar>?</span>");
        sb.Append("<span class=\"ii-meta\">");
        sb.Append("<span class=\"ii-name\" data-ii-name>—</span>");
        sb.Append("<button type=\"button\" class=\"ii-id\" data-ii-principal data-copy-principal title=\"Your principal (on-chain ID) — click to copy\">ID: —</button>");
        sb.Append("<button type=\"button\" class=\"ii-signout\" data-ii-signout>Sign out</button>");
        sb.Append("</span>");
        sb.Append("</div>");
        sb.Append("</div>");
        // Online users — live count of clients heartbeating in the last
        // 30 s. data-online-count is wired by wasp.js (poll + heartbeat
        // every 10 s; updates the innerText reactively).
        sb.Append("<div class=\"sidebar-online\"><span class=\"online-dot\"></span>");
        sb.Append("<span data-online-count>—</span> online</div>");
        sb.Append("<div class=\"sidebar-foot\">on-chain · always</div>");
        sb.Append("</aside>");
        // Chat + Place want the full viewport (no padding, no max-width,
        // internal scroll only). Older browsers don't support :has() so
        // a class on <main> is more portable than a :has(.dc-shell) rule.
        var fullbleed = normalized == "/chat" || normalized == "/place" || normalized == "/stable" || normalized == "/forum" || normalized == "/feed"
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
    <title>WASP Bzzz — chat that lives on-chain</title>
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
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', system-ui, Roboto, Helvetica, Arial, sans-serif;
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
        /* Spaces nav (servers / forums / feeds, grouped + named) — replaces .nav */
        .spaces { display: flex; flex-direction: column; gap: 0.1rem; padding: 0 0.5rem; overflow-y: auto; flex: 1 1 auto; min-height: 0; }
        .spaces::-webkit-scrollbar { width: 0; }
        .spaces-grp { display: flex; flex-direction: column; gap: 1px; margin-bottom: 0.5rem; }
        .spaces-h { font-size: 0.66rem; text-transform: uppercase; letter-spacing: 0.06em; color: #8b95ab; font-weight: 700; padding: 0.45rem 0.85rem 0.2rem; }
        .space-item { display: flex; align-items: center; gap: 0.55rem; color: #c8d0e0; text-decoration: none; padding: 0.32rem 0.6rem; border-radius: 8px; font-size: 0.9rem; font-weight: 500; transition: background 0.12s ease, color 0.12s ease; position: relative; }
        .space-item:hover { background: rgba(255,255,255,0.06); color: #fff; }
        .space-item.active { background: linear-gradient(90deg, rgba(91,141,239,0.25), rgba(177,108,242,0.15)); color: #fff; box-shadow: inset 0 0 0 1px rgba(91,141,239,0.35); }
        .space-ic { flex: 0 0 auto; width: 26px; height: 26px; border-radius: 8px; display: grid; place-items: center; color: #fff; font-weight: 700; font-size: 0.8rem; }
        .space-ic-dm { background: #313338; }
        .space-nm { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; flex: 1 1 auto; }
        .space-lock { font-size: 0.7rem; opacity: 0.7; flex: 0 0 auto; }
        .space-empty { color: #6b7488; font-size: 0.78rem; padding: 0.1rem 0.85rem 0.3rem; }
        .space-new { margin: 0.15rem 0.3rem 0; background: transparent; color: #8b95ab; border: 1px dashed rgba(255,255,255,0.14); border-radius: 8px; padding: 0.3rem 0.5rem; font: inherit; font-size: 0.8rem; cursor: pointer; text-align: left; transition: background 0.12s, color 0.12s, border-color 0.12s; }
        .space-new:hover { background: rgba(91,141,239,0.12); color: #fff; border-color: rgba(91,141,239,0.4); }
        .space-create { display: flex; flex-direction: column; gap: 0.4rem; padding: 0.5rem; margin: 0 0.3rem 0.5rem; background: rgba(0,0,0,0.2); border: 1px solid rgba(255,255,255,0.08); border-radius: 10px; }
        .space-create-in { background: #0e1420; color: #f2f3f5; border: 1px solid rgba(255,255,255,0.1); border-radius: 6px; padding: 0.35rem 0.5rem; font: inherit; font-size: 0.85rem; outline: none; }
        .space-create-in:focus { border-color: rgba(91,141,239,0.6); }
        .space-create-priv { display: flex; align-items: center; gap: 0.4rem; font-size: 0.8rem; color: #c8d0e0; }
        .space-create-row { display: flex; gap: 0.4rem; }
        .space-create-go { flex: 1; background: #5b8def; color: #fff; border: 0; border-radius: 6px; padding: 0.35rem; font: inherit; font-size: 0.82rem; cursor: pointer; }
        .space-create-go:hover { background: #4a7ad8; }
        .space-create-x { background: transparent; color: #8b95ab; border: 1px solid rgba(255,255,255,0.12); border-radius: 6px; padding: 0.35rem 0.6rem; font: inherit; font-size: 0.82rem; cursor: pointer; }
        .spaces-dm { flex: 0 0 auto; padding: 0.35rem 0.5rem 0.1rem; margin-top: 0.2rem; border-top: 1px solid rgba(255,255,255,0.07); }
        .space-item.dc-rail-unread .space-nm::after { content: ''; display: inline-block; width: 7px; height: 7px; border-radius: 50%; background: #f23f42; margin-left: 0.4rem; vertical-align: middle; }
        .sidebar-auth {
            margin-top: auto;
            padding: 0.85rem 1rem 0.4rem;
        }
        .ii-signin {
            display: flex; align-items: center; gap: 0.55rem;
            width: 100%;
            background: rgba(255,255,255,0.05);
            color: #f8fafc; border: 1px solid rgba(255,255,255,0.1);
            border-radius: 8px;
            padding: 0.55rem 0.7rem;
            font: inherit; font-size: 0.85rem; font-weight: 600;
            cursor: pointer;
            transition: background 0.12s ease, border-color 0.12s ease;
            text-align: left;
        }
        .ii-signin:hover {
            background: linear-gradient(90deg, rgba(91,141,239,0.22), rgba(177,108,242,0.18));
            border-color: rgba(91,141,239,0.45);
        }
        .ii-signin:active { transform: translateY(1px); }
        .ii-signin[disabled] { opacity: 0.55; cursor: progress; }
        .ii-signin[hidden] { display: none; }
        .ii-mark {
            display: inline-grid; place-items: center;
            width: 22px; height: 22px; border-radius: 50%;
            background: linear-gradient(135deg, #29abe2 0%, #522785 100%);
            color: #fff; font-size: 0.9rem; font-weight: 700;
        }
        .ii-card {
            display: flex; align-items: center; gap: 0.6rem;
            background: rgba(255,255,255,0.05);
            border: 1px solid rgba(255,255,255,0.08);
            border-radius: 8px;
            padding: 0.5rem 0.65rem;
        }
        .ii-card[hidden] { display: none; }
        .ii-avatar {
            width: 30px; height: 30px; border-radius: 50%;
            display: grid; place-items: center;
            background: #5b8def; color: #fff;
            font-weight: 700; font-size: 0.85rem; text-transform: uppercase;
            flex: 0 0 auto;
        }
        .ii-meta {
            display: flex; flex-direction: column;
            min-width: 0; line-height: 1.2; flex: 1 1 auto;
        }
        .ii-name {
            color: #fff; font-size: 0.88rem; font-weight: 600;
            overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
        }
        .ii-id {
            align-self: flex-start; margin-top: 1px;
            background: transparent; border: 0; padding: 0;
            color: rgba(255,255,255,0.45); font: inherit; font-size: 0.68rem;
            font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
            cursor: pointer; max-width: 100%;
            overflow: hidden; text-overflow: ellipsis; white-space: nowrap; text-align: left;
        }
        .ii-id:hover { color: #5b8cff; }
        .ii-signout {
            align-self: flex-start;
            margin-top: 2px;
            background: transparent; color: rgba(255,255,255,0.55);
            border: 0; padding: 0; font: inherit; font-size: 0.72rem;
            cursor: pointer;
            text-align: left;
        }
        .ii-signout:hover { color: #fff; }

        .sidebar-online {
            padding: 0.4rem 1.5rem 0.4rem;
            color: rgba(255,255,255,0.65); font-size: 0.82rem;
            display: flex; align-items: center; gap: 0.45rem;
        }
        .sidebar-online .online-dot {
            width: 8px; height: 8px; border-radius: 50%;
            background: #22c55e; box-shadow: 0 0 8px rgba(34,197,94,0.7);
            animation: online-pulse 2s ease-in-out infinite;
        }
        @keyframes online-pulse { 0%,100% { opacity: 1; } 50% { opacity: 0.45; } }
        .sidebar-foot {
            padding: 0.2rem 1.5rem 0.8rem;
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
            grid-template-columns: 240px 1fr 220px;
            grid-template-rows: 100%;   /* pin the single row to grid height so children
                                           don't stretch past the viewport */
            height: 100%;
            background: #313338; color: #dcddde;
            font: 15px/1.45 'Inter', system-ui, sans-serif;
        }
        .dc-rail, .dc-rooms, .dc-channel, .dc-members { min-height: 0; }   /* allow shrink-below-content */
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
        .dc-username-locked {
            display: none;
            font-size: 0.9rem; font-weight: 600;
            color: #fff;
            background: linear-gradient(135deg, rgba(91,141,239,0.25), rgba(177,108,242,0.18));
            border-radius: 6px;
            padding: 0.55rem 0.65rem;
            overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
        }
        /* When signed in, the input collapses to the @name badge so the
           composer's username arg comes from the (still-present, hidden)
           input that the bridge populates from localStorage. */
        body[data-ii-state=""signed-in""] .dc-username-card { display: none; }
        body[data-ii-state=""signed-in""] .dc-username-locked { display: block; }
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
        /* Icon must not be the click target — the wasp-bridge click
           guard bails when e.target (the svg/path) differs from the
           handler element and carries no data-wasp-args, which silently
           ate the send click while Enter (a direct button.click()) worked. */
        .dc-send svg, .dc-send svg * { pointer-events: none; }
        .dc-send:hover { background: #4752c4; }
        .dc-send:active { transform: scale(0.96); }
        .dc-send:disabled { background: #4e5058; cursor: not-allowed; opacity: 0.6; }

        /* ── Member rail ─────────────────────────────────────────── */
        .dc-members {
            background: #2b2d31; color: #c9cbd4;
            display: flex; flex-direction: column;
            border-left: 1px solid rgba(0,0,0,0.25);
            overflow-y: auto;
            padding: 0.5rem 0;
        }
        .dc-members::-webkit-scrollbar { width: 10px; }
        .dc-members::-webkit-scrollbar-track { background: transparent; }
        .dc-members::-webkit-scrollbar-thumb {
            background: #1a1b1e; border: 2px solid #2b2d31;
            border-radius: 6px; min-height: 30px;
        }
        .dc-members-section { padding: 0.5rem 0 0.25rem; }
        .dc-members-header {
            color: #949ba4; font-size: 0.72rem; font-weight: 700;
            text-transform: uppercase; letter-spacing: 0.04em;
            padding: 0.4rem 1rem 0.4rem;
        }
        .dc-member {
            display: flex; align-items: center; gap: 0.6rem;
            width: 100%;
            padding: 0.35rem 0.6rem;
            margin: 1px 0.5rem; border-radius: 6px;
            background: transparent; border: 0;
            text-align: left; cursor: pointer;
            color: #b5bac1; font: inherit;
            transition: background 0.1s ease, color 0.1s ease;
        }
        .dc-member:hover { background: rgba(255,255,255,0.05); color: #fff; }
        .dc-member-avatar-wrap { position: relative; flex: 0 0 auto; width: 32px; height: 32px; }
        .dc-member-avatar {
            width: 32px; height: 32px; border-radius: 50%;
            display: grid; place-items: center;
            color: #fff; font-weight: 700; font-size: 0.85rem;
            text-transform: uppercase;
        }
        .dc-member-dot {
            position: absolute; right: -2px; bottom: -2px;
            width: 12px; height: 12px; border-radius: 50%;
            background: #23a55a; box-shadow: 0 0 0 2px #2b2d31;
        }
        .dc-member-name {
            flex: 1 1 auto; overflow: hidden;
            text-overflow: ellipsis; white-space: nowrap;
            font-size: 0.9rem; font-weight: 500;
        }
        .dc-member-offline { opacity: 0.55; }
        .dc-member-offline .dc-member-avatar { filter: grayscale(0.55); }
        .dc-member-offline .dc-member-name { color: #80848e; }

        /* ── @mention pill ───────────────────────────────────────── */
        .dc-mention {
            display: inline-block;
            padding: 0 0.25rem;
            border-radius: 4px;
            font-weight: 600;
            cursor: default;
            transition: filter 0.1s ease;
        }
        .dc-mention:hover { filter: brightness(1.15); }
        /* ── Hashtag pill (#tag) — parallel to mention, but it's an <a> ── */
        .dc-tag {
            display: inline-block; padding: 0 0.25rem; border-radius: 4px;
            font-weight: 600; text-decoration: none; cursor: pointer;
            transition: filter 0.1s ease;
        }
        .dc-tag:hover { filter: brightness(1.15); text-decoration: none; }
        /* ── #tag filtered-view banner (sits between header + message list) ── */
        .dc-tagfilter {
            flex: 0 0 auto; display: flex; align-items: center; gap: 0.5rem;
            padding: 0.45rem 1.25rem; background: rgba(88,101,242,0.10);
            border-bottom: 1px solid rgba(0,0,0,0.2); color: #b5bac1; font-size: 0.82rem;
        }
        .dc-tagfilter-label { opacity: 0.85; }
        .dc-tagfilter .dc-tag { font-size: 0.82rem; cursor: default; }
        .dc-tagfilter-clear {
            margin-left: auto; color: #cdd0d6; text-decoration: none;
            padding: 0.15rem 0.55rem; border-radius: 6px;
            background: rgba(255,255,255,0.06); border: 1px solid rgba(255,255,255,0.08);
            transition: background 0.12s ease, color 0.12s ease, border-color 0.12s ease;
        }
        .dc-tagfilter-clear:hover { background: rgba(237,66,69,0.18); color: #fff; border-color: rgba(237,66,69,0.4); }

        /* ── Inline markdown ─────────────────────────────────────── */
        .md-b { font-weight: 700; }
        .md-i { font-style: italic; }
        .md-s { text-decoration: line-through; }
        .md-u { text-decoration: underline; }
        .md-s.md-u { text-decoration: underline line-through; }
        .dc-code {
            font-family: 'Consolas', 'SF Mono', 'Menlo', monospace;
            font-size: 0.86em;
            background: #1e1f22; color: #e3e5e8;
            border: 1px solid rgba(0,0,0,0.3);
            border-radius: 4px; padding: 0.05rem 0.3rem;
            white-space: pre-wrap; word-break: break-word;
        }
        .dc-link {
            color: #00a8fc; text-decoration: none; word-break: break-word;
        }
        .dc-link:hover { text-decoration: underline; }

        /* ── Spoiler (click to reveal) ───────────────────────────── */
        .dc-spoiler {
            background: #1b1c1f; color: transparent; border-radius: 4px;
            padding: 0 0.2rem; cursor: pointer; user-select: none;
            transition: color 0.12s ease, background 0.12s ease;
            box-decoration-break: clone; -webkit-box-decoration-break: clone;
        }
        .dc-spoiler:not(.dc-revealed) { filter: blur(0.18rem); }
        .dc-spoiler:hover:not(.dc-revealed) { background: #232428; }
        .dc-spoiler:focus-visible { outline: 2px solid #5865f2; outline-offset: 1px; }
        .dc-spoiler.dc-revealed {
            background: rgba(255,255,255,0.06); color: inherit; cursor: auto;
            user-select: text; filter: none;
        }
        /* ── Fenced code block + blockquote + paragraph ──────────── */
        .dc-codeblock {
            position: relative; margin: 0.35rem 0; border: 1px solid rgba(0,0,0,0.4);
            border-radius: 6px; background: #1e1f22; overflow: hidden;
        }
        .dc-codeblock-lang {
            display: block; font-family: 'Consolas', 'SF Mono', 'Menlo', monospace;
            font-size: 0.68rem; color: #949ba4; background: rgba(255,255,255,0.04);
            padding: 0.15rem 0.6rem; border-bottom: 1px solid rgba(0,0,0,0.4);
            text-transform: lowercase; letter-spacing: 0.02em;
        }
        .dc-codeblock-pre {
            margin: 0; padding: 0.55rem 0.7rem;
            font-family: 'Consolas', 'SF Mono', 'Menlo', monospace;
            font-size: 0.84em; line-height: 1.45; color: #e3e5e8;
            white-space: pre; overflow-x: auto; tab-size: 2;
        }
        .dc-codeblock-pre code { font: inherit; color: inherit; background: none; }
        .dc-quote {
            margin: 0.2rem 0; padding: 0.1rem 0 0.1rem 0.7rem;
            border-left: 4px solid #4e5058; color: #dbdee1;
            white-space: pre-wrap; word-break: break-word;
        }
        .dc-para { white-space: pre-wrap; word-break: break-word; }
        .dc-para + .dc-para, .dc-para + .dc-quote, .dc-quote + .dc-para { margin-top: 0.2rem; }

        /* ── Edited / deleted message states ─────────────────────── */
        .dc-edited { color: #80848e; font-size: 0.68rem; margin-left: 0.3rem; user-select: none; }
        .dc-message-deleted .dc-avatar,
        .dc-message-deleted .dc-username { opacity: 0.6; }
        .dc-text-deleted { color: #949ba4; font-style: italic; font-size: 0.95rem; }

        /* ── Self-mention highlight (you got pinged) ─────────────── */
        .dc-mention-self {
            background: rgba(250,166,26,0.28) !important;
            color: #fff !important;
        }
        .dc-mention-me {
            background: rgba(250,166,26,0.06);
            box-shadow: inset 2px 0 0 #faa61a;
        }

        /* Per-message actions sit inline right after the timestamp
           (see the .dc-actions rule above) — no floating/absolute
           toolbar, so the cluster stays beside the date/time. */
        /* Edit + delete only make sense on your own messages; the bridge
           adds .dc-message-own client-side by matching the local name. */
        .dc-action-own { display: none; }
        .dc-message-own .dc-action-own { display: inline-flex; }
        .dc-action-danger { color: #f23f43; }
        .dc-action-danger:hover { background: rgba(242,63,67,0.16); color: #ff6b6e; }

        /* ── Confirm-delete popover ──────────────────────────────── */
        .dc-confirm-popover { padding: 0.4rem; min-width: 150px; }
        .dc-confirm-popover[data-open] { display: block; }
        .dc-confirm-del {
            width: 100%; background: #f23f43; color: #fff; border: 0;
            border-radius: 5px; padding: 0.45rem 0.6rem; cursor: pointer;
            font: inherit; font-size: 0.82rem; font-weight: 600;
            transition: background 0.1s ease;
        }
        .dc-confirm-del:hover { background: #d83c3e; }

        /* ── Emoji grid (reaction popover + composer full picker) ── */
        .dc-emoji-grid[data-open] {
            display: grid;
            grid-template-columns: repeat(8, 1fr);
            gap: 1px;
            max-height: 220px; overflow-y: auto;
            padding: 5px;
        }
        .dc-emoji-grid::-webkit-scrollbar { width: 8px; }
        .dc-emoji-grid::-webkit-scrollbar-thumb { background: #1a1b1e; border-radius: 6px; }
        .dc-emoji-picker-wrap { position: relative; display: inline-flex; }
        .dc-emoji-more { font-size: 0.95rem; }
        /* Composer picker opens upward (it lives at the bottom of the view). */
        .dc-emoji-popover-up { top: auto; bottom: calc(100% + 6px); right: auto; left: 0; }

        /* ── Editing badge (mirrors the reply badge) ─────────────── */
        .dc-edit-badge {
            display: none;
            align-items: center; gap: 0.5rem;
            background: #3a3d34; color: #dbdee1;
            border-radius: 8px 8px 0 0;
            padding: 0.4rem 0.7rem; font-size: 0.85rem;
            margin: 0 0 0.2rem; grid-column: 1 / -1;
        }
        .dc-edit-badge.is-active { display: flex; }
        .dc-edit-hint { color: #949ba4; font-size: 0.78rem; }

        /* ── Typing indicator row (fixed height = no layout jump) ── */
        .dc-typing {
            grid-column: 1 / -1;
            height: 1.1rem; line-height: 1.1rem;
            font-size: 0.78rem; color: #b5bac1;
            padding: 0 0.2rem; margin-bottom: -0.2rem;
            overflow: hidden; white-space: nowrap; text-overflow: ellipsis;
            opacity: 0; transition: opacity 0.12s ease;
        }
        .dc-typing.is-active { opacity: 1; }
        .dc-typing::before {
            content: '';
            display: inline-block; width: 0; height: 0;
        }
        .dc-typing.is-active::after {
            content: '…'; animation: dc-typing-pulse 1.2s steps(4, end) infinite;
        }
        @keyframes dc-typing-pulse { 0% { opacity: 0.3; } 50% { opacity: 1; } 100% { opacity: 0.3; } }

        /* ── New-messages divider ───────────────────────────────── */
        .dc-new-divider {
            display: flex; align-items: center; gap: 0.5rem;
            margin: 0.6rem 1rem 0.2rem;
            color: #f23f43; font-size: 0.68rem; font-weight: 700;
            text-transform: uppercase; letter-spacing: 0.04em;
        }
        .dc-new-divider::before { content: ''; flex: 1 1 auto; height: 1px; background: rgba(242,63,67,0.5); }
        .dc-new-divider span {
            flex: 0 0 auto;
            background: rgba(242,63,67,0.12); color: #f23f43;
            padding: 0.05rem 0.45rem; border-radius: 6px;
        }

        /* ── Per-channel unread badge ────────────────────────────── */
        .dc-room { position: relative; }
        .dc-room-unread { color: #fff; font-weight: 700; }
        .dc-room-unread::before {
            content: ''; position: absolute; left: -2px; top: 50%;
            transform: translateY(-50%);
            width: 4px; height: 8px; border-radius: 0 4px 4px 0; background: #fff;
        }
        .dc-unread-badge {
            margin-left: auto; flex: 0 0 auto;
            min-width: 8px; height: 8px; padding: 0; border-radius: 50%;
            background: #f23f43; box-shadow: 0 0 0 2px #2b2d31;
        }
        /* With a count present, grow into a rounded pill with the number. */
        .dc-unread-badge.has-count {
            min-width: 16px; height: 16px; padding: 0 5px; border-radius: 8px;
            display: inline-flex; align-items: center; justify-content: center;
            color: #fff; font-size: 0.72rem; font-weight: 700; line-height: 1;
        }
        /* Aggregate server-rail unread pip (toggled via .dc-rail-unread on the <a>). */
        .dc-rail-unread .dc-rail-icon { position: relative; }
        .dc-rail-unread .dc-rail-icon::after {
            content: ''; position: absolute; right: 0; bottom: 0;
            width: 12px; height: 12px; border-radius: 50%;
            background: #f23f43; border: 3px solid #1e1f22;
        }

        /* ── @mention autocomplete popup ─────────────────────────── */
        .dc-ac {
            position: fixed; z-index: 50;
            background: #2b2d31; border: 1px solid rgba(0,0,0,0.4);
            border-radius: 8px; box-shadow: 0 8px 22px rgba(0,0,0,0.5);
            overflow: hidden; padding: 4px;
        }
        .dc-ac-item {
            padding: 0.4rem 0.6rem; border-radius: 5px;
            color: #b5bac1; font-size: 0.9rem; cursor: pointer;
            white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
        }
        .dc-ac-item:hover, .dc-ac-item.active { background: #4752c4; color: #fff; }

        /* ── Image lightbox ──────────────────────────────────────── */
        .dc-lightbox {
            position: fixed; inset: 0; z-index: 1000;
            background: rgba(0,0,0,0.85);
            display: flex; align-items: center; justify-content: center;
            padding: 2.5rem; cursor: zoom-out;
            animation: dc-fade 0.12s ease;
        }
        .dc-lightbox img {
            max-width: 92vw; max-height: 88vh;
            border-radius: 8px; box-shadow: 0 12px 48px rgba(0,0,0,0.6);
            cursor: default;
        }
        .dc-lightbox-close {
            position: absolute; top: 1.2rem; right: 1.5rem;
            width: 40px; height: 40px; border-radius: 50%;
            background: rgba(255,255,255,0.12); color: #fff; border: 0;
            font-size: 1.5rem; line-height: 1; cursor: pointer;
            transition: background 0.1s ease;
        }
        .dc-lightbox-close:hover { background: rgba(255,255,255,0.22); }
        @keyframes dc-fade { from { opacity: 0; } to { opacity: 1; } }

        /* ── Video embeds: in-message play card + click-to-load lightbox ── */
        .dc-video-card {
            display: inline-flex; align-items: center; gap: 0.6rem; margin-top: 0.35rem;
            max-width: 100%; padding: 0.5rem 0.75rem; border-radius: 10px;
            background: rgba(255,255,255,0.05); border: 1px solid rgba(255,255,255,0.1);
            color: #dbdee1; font: inherit; font-size: 0.9rem; text-align: left; cursor: pointer;
            transition: background 0.12s ease, border-color 0.12s ease;
        }
        .dc-video-card:hover { background: rgba(255,255,255,0.09); border-color: rgba(91,141,239,0.55); }
        .dc-video-card:active { transform: scale(0.99); }
        .dc-video-play {
            flex: 0 0 auto; display: grid; place-items: center;
            width: 32px; height: 32px; border-radius: 50%;
            background: #5865f2; color: #fff; font-size: 0.8rem; padding-left: 2px;
        }
        .dc-video-meta { display: flex; flex-direction: column; min-width: 0; }
        .dc-video-label { font-weight: 700; color: #f2f3f5; }
        .dc-video-url { color: #80848e; font-size: 0.78rem; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
        .dc-video-lightbox .dc-video-frame {
            position: relative; width: min(92vw, 1100px); aspect-ratio: 16 / 9;
        }
        .dc-video-lightbox .dc-video-frame iframe,
        .dc-video-lightbox .dc-video-frame video {
            position: absolute; inset: 0; width: 100%; height: 100%;
            border: 0; border-radius: 10px; background: #000;
        }

        /* ── Drag-to-upload overlay ──────────────────────────────── */
        .dc-channel { position: relative; }
        body.dc-dragging .dc-channel::after {
            content: 'Drop image to upload';
            position: absolute; inset: 48px 12px 12px;
            border: 2px dashed #5865f2; border-radius: 12px;
            background: rgba(88,101,242,0.12);
            display: flex; align-items: center; justify-content: center;
            color: #c7d2fe; font-weight: 700; font-size: 1.1rem;
            pointer-events: none; z-index: 20;
        }

        /* ── Accessibility: visible focus ring (project rule: never
              ship default browser focus outlines OR none at all) ──── */
        .dc-input:focus-visible, .dc-composer-input:focus-visible,
        .dc-action:focus-visible, .dc-emoji-btn:focus-visible,
        .dc-send:focus-visible, .dc-member:focus-visible,
        .dc-react-badge:focus-visible, .dc-react-pick:focus-visible,
        .dc-btn-add:focus-visible, .dc-room:focus-visible,
        .dc-confirm-del:focus-visible, .dc-ac-item:focus-visible {
            outline: 2px solid #5b8def; outline-offset: 1px;
        }

        /* ── Respect reduced-motion ──────────────────────────────── */
        @media (prefers-reduced-motion: reduce) {
            .dc-emoji-btn, .dc-react-badge, .dc-react-pick, .dc-action,
            .dc-send, .dc-room, .dc-member, .dc-btn-add, .dc-typing,
            .dc-lightbox { transition: none !important; animation: none !important; }
            .dc-emoji-btn:active, .dc-react-badge:active, .dc-react-pick:active,
            .dc-action:active, .dc-send:active { transform: none !important; }
        }

        /* ── Public message threads (right panel, replaces members) ── */
        .dc-shell-thread { grid-template-columns: 240px 1fr 380px; }
        .dc-thread { background: #2b2d31; display: flex; flex-direction: column; min-height: 0; border-left: 1px solid rgba(0,0,0,0.3); }
        .dc-thread-header {
            flex: 0 0 auto; height: 48px; display: flex; align-items: center; justify-content: space-between;
            padding: 0 0.5rem 0 1rem; border-bottom: 1px solid rgba(0,0,0,0.3); box-shadow: 0 1px 0 rgba(0,0,0,0.2);
        }
        .dc-thread-title { font-weight: 700; color: #f2f3f5; }
        .dc-thread-close {
            width: 30px; height: 30px; display: grid; place-items: center; border-radius: 6px;
            color: #b5bac1; text-decoration: none; font-size: 1.3rem; line-height: 1;
            transition: background 0.1s, color 0.1s;
        }
        .dc-thread-close:hover { background: rgba(255,255,255,0.08); color: #fff; }
        .dc-thread-scroll { flex: 1 1 auto; min-height: 0; overflow-y: auto; padding: 0.75rem 0.25rem 0.5rem; }
        .dc-thread-parent { padding: 0.25rem 0.85rem 0.5rem; }
        .dc-thread-divider {
            display: flex; align-items: center; gap: 0.5rem; margin: 0.35rem 0.85rem 0.6rem; color: #949ba4; font-size: 0.72rem;
        }
        .dc-thread-divider::before, .dc-thread-divider::after { content: ''; flex: 1; height: 1px; background: rgba(255,255,255,0.08); }
        .dc-thread .dc-message { grid-template-columns: 40px 1fr; padding: 0.15rem 0.85rem; }
        .dc-thread .dc-avatar { width: 30px; height: 30px; font-size: 0.85rem; }
        .dc-thread-composer { padding: 0.25rem 0.75rem 0.85rem; }
        /* 💬 reply-count badge under a threaded message */
        .dc-thread-badge {
            display: inline-flex; align-items: center; gap: 0.3rem; margin-top: 0.25rem;
            padding: 0.15rem 0.5rem; border-radius: 8px; cursor: pointer; width: fit-content;
            background: rgba(88,101,242,0.12); border: 1px solid rgba(88,101,242,0.35);
            color: #c7d2fe; font-size: 0.78rem; font-weight: 600; text-decoration: none;
            transition: background 0.1s, border-color 0.1s;
        }
        .dc-thread-badge:hover { background: rgba(88,101,242,0.24); border-color: rgba(88,101,242,0.6); }
        .dc-thread-badge.active { background: rgba(88,101,242,0.3); }
        .dc-thread-badge-count { font-variant-numeric: tabular-nums; }
        .dc-thread-badge-label { color: #8b9bf4; }
        .dc-action-active { background: rgba(88,101,242,0.25); color: #fff; }

        /* ── Servers rail + DMs (added) ───────────────────────────── */
        /* Server rail */
        .dc-rail{background:#1e1f22;display:flex;flex-direction:column;align-items:center;gap:.4rem;padding:.65rem 0;overflow-y:auto;}
        .dc-rail::-webkit-scrollbar{width:0;}
        .dc-rail-server{position:relative;width:48px;height:48px;display:grid;place-items:center;text-decoration:none;flex:0 0 auto;}
        .dc-rail-icon{width:48px;height:48px;border-radius:50%;display:grid;place-items:center;color:#fff;font-weight:700;font-size:1.2rem;background:#313338;transition:border-radius .15s,background .15s;}
        .dc-rail-server:hover .dc-rail-icon,.dc-rail-server.active .dc-rail-icon{border-radius:16px;}
        .dc-rail-server.active .dc-rail-icon{background:#5865f2;}
        .dc-rail-pill{position:absolute;left:-.65rem;top:50%;width:4px;height:8px;border-radius:0 4px 4px 0;background:#fff;transform:translateY(-50%) scaleY(.3);opacity:0;transition:opacity .15s,transform .15s;}
        .dc-rail-server:hover .dc-rail-pill{opacity:1;transform:translateY(-50%) scaleY(.6);}
        .dc-rail-server.active .dc-rail-pill{opacity:1;transform:translateY(-50%) scaleY(1);}
        .dc-rail-sep{width:32px;height:2px;border-radius:1px;background:#2b2d31;margin:.15rem 0;}
        .dc-rail-add{display:contents;}
        .dc-rail-add-input{width:44px;background:#1e1f22;color:#fff;border:1px solid rgba(255,255,255,.06);border-radius:8px;font:inherit;font-size:.62rem;text-align:center;padding:.3rem .1rem;outline:none;margin-top:.1rem;}
        .dc-rail-add-input:focus{border-color:rgba(88,101,242,.6);}
        .dc-rail-plus{width:48px;height:48px;border-radius:50%;background:#313338;color:#23a55a;border:0;cursor:pointer;font-size:1.5rem;line-height:1;transition:border-radius .15s,background .15s,color .15s;}
        .dc-rail-plus:hover{border-radius:16px;background:#23a55a;color:#fff;}
        .dc-rail-dm-icon{background:#313338;}
        .dc-rail-dm .dc-rail-dm-lock{position:absolute;right:2px;bottom:2px;font-size:.7rem;display:none;}
        body[data-ii-state=signed-out] .dc-rail-dm{opacity:.55;}
        body[data-ii-state=signed-out] .dc-rail-dm .dc-rail-dm-lock{display:block;}
        /* DM thread list */
        .dc-dm-threads{padding:.25rem .5rem;overflow-y:auto;flex:1 1 auto;min-height:0;}
        .dc-dm-thread{display:flex;align-items:center;gap:.5rem;padding:.45rem .6rem;border-radius:6px;color:#b5bac1;text-decoration:none;font-size:.95rem;font-weight:500;transition:background .1s,color .1s;}
        .dc-dm-thread:hover{background:rgba(255,255,255,.05);color:#fff;}
        .dc-dm-thread-avatar{width:32px;height:32px;border-radius:50%;flex:0 0 auto;display:grid;place-items:center;color:#fff;font-weight:700;font-size:.85rem;}
        .dc-dm-thread-name{overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}
        .dc-dm-threads-empty{color:#80848e;font-size:.82rem;padding:.8rem .6rem;line-height:1.4;}
        /* DM gate */
        .dc-dm-gate{padding:1rem;color:#b5bac1;}
        .dc-dm-gate-text{font-size:.85rem;line-height:1.5;margin:0 0 .8rem;}
        .dc-dm-gate-center{flex:1 1 auto;display:flex;flex-direction:column;align-items:center;justify-content:center;text-align:center;gap:.6rem;padding:2rem;}
        .dc-dm-gate-center h2{color:#fff;margin:0;}.dc-dm-gate-center p{color:#b5bac1;max-width:360px;margin:0;}
        .dc-dm-signin{display:inline-flex;align-items:center;gap:.45rem;background:linear-gradient(135deg,#5b8def,#b16cf2);color:#fff;border:0;border-radius:8px;cursor:pointer;padding:.6rem 1rem;font:inherit;font-weight:600;font-size:.9rem;transition:filter .12s,transform .05s;}
        .dc-dm-signin:hover{filter:brightness(1.08);}.dc-dm-signin:active{transform:scale(.97);}
        body[data-ii-state=signed-in] .dc-dm-gate{display:none;}
        .dc-dm-at{color:#80848e;font-weight:600;font-size:1.2rem;}
        .dc-dm-empty-pick{flex:1 1 auto;display:flex;flex-direction:column;align-items:center;justify-content:center;text-align:center;gap:.5rem;color:#b5bac1;padding:2rem;}
        .dc-dm-empty-pick h2{color:#fff;margin:0;}
        /* These DM panes set an explicit display (flex/grid), which beats the
           UA [hidden]{display:none}. The DmClientScript toggles them via the
           hidden attribute, so force hidden to win or they never collapse. */
        .dc-dm-gate[hidden], .dc-dm-threads[hidden], .dc-dm-stream[hidden],
        .dc-dm-composer[hidden], .dc-dm-empty-pick[hidden] { display: none !important; }
        /* Same gotcha for the role-gated create forms: .dc-rail-add is
           display:contents and .dc-room-add is display:grid, both of which beat
           the UA [hidden]{display:none}. Without this !important override the
           signed client's applyVisibility() (el.hidden=true) is a visual no-op
           and the privileged plus-forms leak to every visitor. */
        .dc-rail-add[hidden], .dc-room-add[hidden] { display: none !important; }
        /* Member 'Message' affordance */
        .dc-member-row{display:flex;align-items:center;}
        .dc-member-row .dc-member{flex:1 1 auto;}
        .dc-member-dm{flex:0 0 auto;margin:1px .5rem 1px 0;background:transparent;border:0;cursor:pointer;color:#80848e;font-size:.95rem;line-height:1;padding:.35rem .45rem;border-radius:6px;opacity:0;transition:opacity .1s,background .1s,color .1s;}
        .dc-member-row:hover .dc-member-dm{opacity:1;}
        .dc-member-dm:hover{background:rgba(88,101,242,.18);color:#c7d2fe;}
        body[data-ii-state=signed-out] .dc-member-dm{color:#5a5d63;cursor:not-allowed;}
        body[data-ii-state=signed-out] .dc-member-dm:hover{background:transparent;color:#5a5d63;}


        @media (max-width: 960px) {
            /* Desktop-but-cramped: drop the member rail before forcing
               the full mobile collapse so the channel keeps its width.
               Keep the 56px server rail. */
            .dc-shell { grid-template-columns: 240px 1fr; }
            .dc-members { display: none; }
            /* When a thread is open at this width there's no room for a 4th
               column, so the thread takes over the channel's slot. */
            .dc-shell-thread { grid-template-columns: 240px 1fr; }
            .dc-shell-thread .dc-channel { display: none; }
            .dc-shell-thread .dc-thread { border-left: 0; }
        }

        @media (max-width: 720px) {
            /* A thread, when open, replaces the channel in the single column. */
            .dc-shell-thread .dc-channel { display: none; }

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
            /* Mobile collapses to a single column with rooms as a top
               scroller; the server rail is hidden here (server switching
               is a desktop affordance). */
            .dc-rail { display: none; }
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
            /* Spaces nav collapses to a horizontal scroller of chips; group
               headers + the create UI are hidden on the 56px top bar. */
            .spaces {
                flex: 1 1 auto; flex-direction: row;
                gap: 0; padding: 0; min-height: 0;
                overflow-x: auto; overflow-y: hidden; align-items: stretch;
            }
            .spaces-grp { flex-direction: row; margin-bottom: 0; gap: 0; }
            .spaces-h, .space-new, .space-create, .space-empty, .space-lock, .spaces-dm { display: none; }
            .space-item {
                flex: 0 0 auto; max-width: 84px;
                flex-direction: column; gap: 2px;
                padding: 0.3rem 0.5rem;
                font-size: 0.62rem; font-weight: 600;
                border-radius: 0; text-align: center;
                justify-content: center;
            }
            .space-item.active {
                background: linear-gradient(180deg, rgba(91,141,239,0.18), rgba(177,108,242,0.1));
                box-shadow: inset 0 -2px 0 var(--accent);
            }
            .space-ic { width: 22px; height: 22px; font-size: 0.7rem; }
            .space-nm { max-width: 72px; }
            .sidebar-foot { display: none; }

            main { padding: 1.25rem; }
            main h1 { font-size: 1.4rem; }
            main.fullbleed { height: calc(100dvh - var(--top-nav-h)); }
        }

        /* ── Cmd-K search palette ─────────────────────────────────── */
        .ck-backdrop {
            position: fixed; inset: 0; z-index: 1000;
            background: rgba(7, 9, 14, 0.55); backdrop-filter: blur(2px);
            display: flex; align-items: flex-start; justify-content: center;
            padding-top: 14vh; animation: ck-fade 0.12s ease-out;
        }
        @keyframes ck-fade { from { opacity: 0; } to { opacity: 1; } }
        .ck-palette {
            width: min(560px, 92vw); max-height: 60vh;
            background: var(--bg-elev); border: 1px solid var(--border);
            border-radius: var(--radius); box-shadow: 0 24px 60px rgba(0, 0, 0, 0.55);
            display: flex; flex-direction: column; overflow: hidden;
            animation: ck-rise 0.14s cubic-bezier(0.2, 0.8, 0.2, 1);
        }
        @keyframes ck-rise { from { transform: translateY(-8px); opacity: 0; } to { transform: translateY(0); opacity: 1; } }
        .ck-input-row { display: flex; align-items: center; gap: 0.6rem; padding: 0.85rem 1rem; border-bottom: 1px solid var(--border); }
        .ck-icon { color: var(--text-dim); font-size: 1.15rem; line-height: 1; }
        .ck-input { flex: 1 1 auto; min-width: 0; background: transparent; border: 0; outline: none; color: var(--text); font-family: inherit; font-size: 1rem; }
        .ck-input::placeholder { color: var(--text-dim); }
        .ck-hint { font-size: 0.7rem; color: var(--text-dim); border: 1px solid var(--border); border-radius: 5px; padding: 0.1rem 0.4rem; text-transform: uppercase; letter-spacing: 0.04em; }
        .ck-results { overflow-y: auto; padding: 0.35rem; }
        .ck-empty { padding: 1.4rem 1rem; text-align: center; color: var(--text-dim); font-size: 0.88rem; }
        .ck-hit { display: block; width: 100%; text-align: left; background: transparent; border: 0; cursor: pointer; padding: 0.55rem 0.7rem; border-radius: 8px; color: var(--text); font-family: inherit; }
        .ck-hit:hover, .ck-hit.active { background: linear-gradient(90deg, rgba(91,141,239,0.16), rgba(177,108,242,0.1)); }
        .ck-hit.active { box-shadow: inset 2px 0 0 var(--accent); }
        .ck-hit-top { display: flex; align-items: baseline; gap: 0.5rem; margin-bottom: 0.15rem; }
        .ck-hit-channel { font-size: 0.82rem; font-weight: 600; color: var(--accent); }
        .ck-hit-author { font-size: 0.74rem; color: var(--text-dim); }
        .ck-hit-snippet { font-size: 0.84rem; color: var(--text); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
        /* ── M2-B moderation + B4 private-channel UI (custom-styled; no default inputs) ── */
        .dc-pin-badge { margin-left: 0.35rem; font-size: 0.85rem; opacity: 0.9; }
        .dc-mod-actions { display: inline-flex; gap: 0.15rem; margin-left: 0.25rem; padding-left: 0.35rem; border-left: 1px solid rgba(255,255,255,0.12); }
        .dc-lock-toggle { background: transparent; border: 1px solid rgba(255,255,255,0.12); color: #b5bac1; border-radius: 6px; cursor: pointer; font-size: 0.9rem; padding: 0.1rem 0.45rem; margin-left: 0.5rem; }
        .dc-lock-toggle:hover { background: rgba(255,255,255,0.06); color: #fff; }
        .dc-rail-server { position: relative; }
        .dc-rail-lock { position: absolute; right: 0; bottom: 0; font-size: 0.6rem; filter: drop-shadow(0 1px 1px #000); pointer-events: none; }
        .dc-rail-private { display: inline-flex; align-items: center; gap: 0.1rem; font-size: 0.7rem; color: #b5bac1; cursor: pointer; }
        .dc-rail-private input { accent-color: #5865f2; width: 13px; height: 13px; cursor: pointer; margin: 0; }
        .dc-private-gate { text-align: center; padding: 2rem 1rem 0.5rem; color: #b5bac1; }
        .dc-private-gate h2 { color: #f2f3f5; margin: 0.3rem 0; font-size: 1.05rem; }
        .dc-private-gate .dc-empty-mark { font-size: 2rem; }
        .dc-private-stream { display: flex; flex-direction: column; gap: 0.1rem; padding: 0 0.5rem 1rem; }
        .dc-server-settings { margin: 0.5rem 0.5rem 0; padding: 0.6rem; background: #2b2d31; border: 1px solid rgba(0,0,0,0.25); border-radius: 8px; }
        .dc-settings-title { font-size: 0.72rem; text-transform: uppercase; letter-spacing: 0.04em; color: #949ba4; margin-bottom: 0.45rem; }
        .dc-settings-row { display: flex; align-items: center; gap: 0.4rem; margin-bottom: 0.4rem; font-size: 0.8rem; color: #dbdee1; }
        .dc-settings-vis { flex: 1; }
        .dc-settings-vis strong { color: #f2f3f5; }
        .dc-settings-btn { background: #5865f2; color: #fff; border: none; border-radius: 6px; padding: 0.25rem 0.55rem; font-size: 0.75rem; cursor: pointer; white-space: nowrap; }
        .dc-settings-btn:hover { background: #4752c4; }
        .dc-member-input { flex: 1; min-width: 0; background: #1e1f22; color: #f2f3f5; border: 1px solid rgba(255,255,255,0.08); border-radius: 6px; padding: 0.3rem 0.5rem; font: inherit; font-size: 0.75rem; outline: none; }
        .dc-member-input:focus { border-color: rgba(88,101,242,0.6); }
        .dc-member-list { list-style: none; margin: 0.3rem 0 0; padding: 0; max-height: 160px; overflow-y: auto; }
        .dc-member-row { display: flex; align-items: center; justify-content: space-between; gap: 0.4rem; padding: 0.2rem 0; font-size: 0.78rem; color: #dbdee1; }
        .dc-member-name { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
        .dc-member-remove { background: transparent; border: 1px solid rgba(237,66,69,0.4); color: #f78b8d; border-radius: 5px; padding: 0.1rem 0.4rem; font-size: 0.7rem; cursor: pointer; }
        .dc-settings-danger { margin-top: 0.55rem; padding-top: 0.5rem; border-top: 1px solid rgba(237,66,69,0.18); }
        .dc-settings-del { width: 100%; background: transparent; border: 1px solid rgba(237,66,69,0.45); color: #f78b8d; }
        .dc-settings-del:hover { background: rgba(237,66,69,0.12); }
        .dc-settings-del.armed { background: #ed4245; border-color: #ed4245; color: #fff; animation: dc-del-pulse 0.9s ease-in-out infinite; }
        @keyframes dc-del-pulse { 0%,100% { box-shadow: 0 0 0 0 rgba(237,66,69,0.5); } 50% { box-shadow: 0 0 0 4px rgba(237,66,69,0); } }
        .dc-member-remove:hover { background: rgba(237,66,69,0.18); color: #fff; }
        .dc-member-empty { font-size: 0.75rem; color: #80848e; padding: 0.2rem 0; }
        .dc-edited { color: #80848e; font-size: 0.72rem; }
        /* ── Product landing (Home) ── */
        .lp { max-width: 920px; margin: 0 auto; padding: 2.5rem 1.25rem 3rem; }
        .lp-hero { text-align: center; padding: 1.5rem 0 2.25rem; }
        .lp-badge { display: inline-block; font-size: 0.78rem; color: #c7ccd6; background: rgba(88,101,242,0.14); border: 1px solid rgba(88,101,242,0.3); padding: 0.25rem 0.7rem; border-radius: 999px; margin-bottom: 1rem; }
        .lp-title { font-size: clamp(2.6rem, 7vw, 4.2rem); font-weight: 800; letter-spacing: -0.02em; margin: 0 0 0.4rem; background: linear-gradient(135deg,#fff,#a9b2ff); -webkit-background-clip: text; background-clip: text; color: transparent; }
        .lp-tagline { font-size: clamp(1.05rem, 2.4vw, 1.4rem); color: #c7ccd6; max-width: 640px; margin: 0 auto 1.6rem; line-height: 1.5; }
        .lp-tagline em { color: #f2f3f5; font-style: normal; font-weight: 600; }
        .lp-cta { display: flex; gap: 0.7rem; justify-content: center; flex-wrap: wrap; margin-bottom: 1rem; }
        .lp-btn { display: inline-flex; align-items: center; gap: 0.45rem; padding: 0.7rem 1.3rem; border-radius: 10px; font-size: 0.98rem; font-weight: 600; text-decoration: none; cursor: pointer; border: 1px solid transparent; transition: transform 0.1s ease, background 0.15s ease; }
        .lp-btn:active { transform: translateY(1px); }
        .lp-btn-primary { background: #5865f2; color: #fff; border: none; }
        .lp-btn-primary:hover { background: #4752c4; }
        .lp-btn-ghost { background: rgba(255,255,255,0.05); color: #f2f3f5; border-color: rgba(255,255,255,0.12); }
        .lp-btn-ghost:hover { background: rgba(255,255,255,0.1); }
        .lp-sub { font-size: 0.85rem; color: #949ba4; max-width: 560px; margin: 0 auto; line-height: 1.5; }
        .lp-sub strong { color: #c7ccd6; }
        .lp-features { display: grid; grid-template-columns: repeat(auto-fit, minmax(250px, 1fr)); gap: 1rem; margin-top: 1.5rem; }
        .lp-feat { display: flex; gap: 0.8rem; align-items: flex-start; background: #2b2d31; border: 1px solid rgba(0,0,0,0.25); border-radius: 12px; padding: 1.1rem; text-align: left; }
        .lp-feat-i { font-size: 1.5rem; line-height: 1; flex: 0 0 auto; }
        .lp-feat h3 { margin: 0 0 0.3rem; font-size: 1rem; color: #f2f3f5; }
        .lp-feat p { margin: 0; font-size: 0.86rem; color: #b5bac1; line-height: 1.45; }
        body[data-ii-state=signed-in] [data-ii-when=out] { display: none; }
        body:not([data-ii-state=signed-in]) [data-ii-when=in] { display: none; }
        /* ==== WASP Bzzz marketing — sg design system (editorial-precision + on-chain proof) ==== */
        /* Single accent + one proof-green over near-black, hairlines not shadows. */
        :root {
            --sg-bg: #0A0B0D;
            --sg-bg-1: #0F1116;
            --sg-bg-2: #15181E;
            --sg-bg-3: #1C2128;
            --sg-line: rgba(255,255,255,0.07);
            --sg-line-2: rgba(255,255,255,0.13);
            --sg-text: #EDEFF3;
            --sg-text-2: #A8B0BD;
            --sg-text-3: #6E7787;
            --sg-accent: #5B8CFF;
            --sg-accent-deep: #3D6FE0;
            --sg-proof: #34D399;
            --sg-warn: #F2C94C;
            --sg-glow: rgba(91,140,255,0.22);
            --sg-r: 12px;
            --sg-r-sm: 8px;
            --sg-pill: 999px;
            --sg-mono: ui-monospace, 'SF Mono', 'SFMono-Regular', 'JetBrains Mono', Menlo, Consolas, monospace;
            --sg-mock-shadow: 0 1px 0 rgba(255,255,255,0.04) inset, 0 28px 70px -30px rgba(0,0,0,0.8);
        }
        .sg { min-height: 100vh; background: var(--sg-bg); color: var(--sg-text); font-family: inherit; }
        .sg ::selection { background: rgba(91,140,255,0.30); }
        .sg code, .sg .mono { font-family: var(--sg-mono); }
        .sg a { color: inherit; }

        /* ---- atoms ---- */
        .sg-eyebrow { display: inline-flex; align-items: center; gap: 0.5rem; font-family: var(--sg-mono);
            font-size: 0.72rem; letter-spacing: 0.14em; text-transform: uppercase; color: var(--sg-text-3);
            border: 1px solid var(--sg-line); border-radius: var(--sg-pill); padding: 0.32rem 0.7rem; }
        .sg-eyebrow.plain { border: 0; padding: 0; }
        .sg-dot { width: 6px; height: 6px; border-radius: 50%; background: var(--sg-proof);
            box-shadow: 0 0 0 0 rgba(52,211,153,0.5); animation: sg-pulse 2.4s ease-out infinite; }
        @keyframes sg-pulse { 0% { box-shadow: 0 0 0 0 rgba(52,211,153,0.45); } 70% { box-shadow: 0 0 0 7px rgba(52,211,153,0); } 100% { box-shadow: 0 0 0 0 rgba(52,211,153,0); } }
        .sg-grad { background: linear-gradient(96deg, var(--sg-accent), var(--sg-accent-deep)); -webkit-background-clip: text; background-clip: text; color: transparent; }
        .sg-seal { color: var(--sg-proof); flex: 0 0 auto; }
        .sg-btn { display: inline-flex; align-items: center; gap: 0.5rem; font: inherit; font-weight: 600; font-size: 0.95rem;
            padding: 0.7rem 1.2rem; border-radius: var(--sg-r-sm); cursor: pointer; text-decoration: none;
            border: 1px solid transparent; transition: transform 0.08s ease, background 0.15s ease, border-color 0.15s ease, box-shadow 0.15s ease; }
        .sg-btn:active { transform: translateY(1px); }
        .sg-btn-primary { background: var(--sg-accent); color: #06122B; border-color: var(--sg-accent); }
        .sg-btn-primary:hover { background: #6E9BFF; box-shadow: 0 0 0 4px rgba(91,140,255,0.18); }
        .sg-btn-ghost { background: transparent; color: var(--sg-text); border-color: var(--sg-line-2); }
        .sg-btn-ghost:hover { border-color: var(--sg-text-3); background: rgba(255,255,255,0.03); }
        .sg-btn .ii-mark { font-size: 1.05em; line-height: 1; }
        .sg-chip { display: inline-flex; align-items: center; gap: 0.5rem; font-family: var(--sg-mono); font-size: 0.8rem;
            color: var(--sg-text-2); background: var(--sg-bg-2); border: 1px solid var(--sg-line); border-radius: var(--sg-r-sm);
            padding: 0.45rem 0.7rem; transition: border-color 0.15s; cursor: copy; }
        .sg-chip:hover { border-color: var(--sg-line-2); }
        .sg-chip:active { border-color: var(--sg-proof); }
        .sg-chip b { color: var(--sg-text); font-weight: 600; }

        /* ---- nav ---- */
        .sg-nav { position: sticky; top: 0; z-index: 60; display: flex; align-items: center; gap: 1.2rem;
            padding: 0.85rem clamp(1rem, 5vw, 3rem); background: rgba(10,11,13,0.72);
            -webkit-backdrop-filter: saturate(140%) blur(10px); backdrop-filter: saturate(140%) blur(10px);
            border-bottom: 1px solid var(--sg-line); }
        .sg-brand { display: inline-flex; align-items: center; gap: 0.6rem; text-decoration: none; font-weight: 700;
            font-size: 1.05rem; letter-spacing: -0.01em; color: var(--sg-text); }
        .sg-brand-mark { color: var(--sg-accent); display: inline-flex; }
        .sg-brand em { font-style: normal; color: var(--sg-accent); font-family: var(--sg-mono); font-weight: 600; letter-spacing: 0.02em; }
        .sg-nav-links { display: flex; gap: 1.4rem; margin-left: 0.8rem; flex: 1; }
        .sg-nav-links a { position: relative; text-decoration: none; color: var(--sg-text-2); font-size: 0.9rem; font-weight: 500; padding: 0.2rem 0; }
        .sg-nav-links a::after { content: ''; position: absolute; left: 0; right: 0; bottom: -3px; height: 1.5px; background: var(--sg-accent); transform: scaleX(0); transform-origin: left; transition: transform 0.18s ease; }
        .sg-nav-links a:hover { color: var(--sg-text); }
        .sg-nav-links a:hover::after { transform: scaleX(1); }
        .sg-nav-actions { display: flex; align-items: center; gap: 0.7rem; margin-left: auto; }
        .sg-nav-open { text-decoration: none; color: var(--sg-text-2); font-size: 0.9rem; font-weight: 600; }
        .sg-nav-open:hover { color: var(--sg-text); }
        .sg-nav-cta { padding: 0.5rem 0.95rem; font-size: 0.88rem; }
        .sg-nav-card.ii-card { display: none; }
        body[data-ii-state=signed-in] .sg-nav-card.ii-card { display: inline-flex; }
        body[data-ii-state=signed-in] .sg-nav-cta { display: none; }
        .sg-main { display: block; }

        /* ---- section scaffolding ---- */
        .sg-section { max-width: 1140px; margin: 0 auto; padding: clamp(3.5rem, 8vw, 6.5rem) clamp(1.25rem, 5vw, 3rem); scroll-margin-top: 76px; }
        .sg-hero { scroll-margin-top: 76px; }
        .sg-band { background: var(--sg-bg-1); border-top: 1px solid var(--sg-line); border-bottom: 1px solid var(--sg-line); }
        .sg-shead { max-width: 720px; margin-bottom: 2.4rem; }
        .sg-shead.center { margin-left: auto; margin-right: auto; text-align: center; }
        .sg-h2 { font-size: clamp(1.7rem, 3.6vw, 2.5rem); line-height: 1.1; font-weight: 700; letter-spacing: -0.025em; color: var(--sg-text); margin: 0.7rem 0 0; }
        .sg-sintro { font-size: clamp(1rem, 1.6vw, 1.12rem); color: var(--sg-text-2); line-height: 1.6; margin: 0.9rem 0 0; max-width: 60ch; }

        /* ---- hero ---- */
        .sg-hero { position: relative; max-width: 1240px; margin: 0 auto; overflow: hidden;
            padding: clamp(3.5rem, 9vw, 7rem) clamp(1.25rem, 5vw, 3rem) clamp(2.5rem, 5vw, 4rem); }
        .sg-hero-glow { position: absolute; top: -40px; left: -60px; width: 560px; height: 360px; z-index: 0; pointer-events: none;
            background: radial-gradient(closest-side, var(--sg-glow), transparent 72%); }
        .sg-hero-grid { position: relative; z-index: 1; display: grid; grid-template-columns: minmax(0, 47%) 1fr; gap: clamp(2rem, 5vw, 4.5rem); align-items: center; }
        .sg-h1 { font-size: clamp(2.3rem, 5.3vw, 3.8rem); line-height: 1.05; font-weight: 760; letter-spacing: -0.035em; color: var(--sg-text); margin: 1.2rem 0 0; }
        .sg-lead { font-size: clamp(1.02rem, 1.7vw, 1.18rem); color: var(--sg-text-2); line-height: 1.62; max-width: 52ch; margin: 1.3rem 0 0; }
        .sg-cta-row { display: flex; flex-wrap: wrap; gap: 0.7rem; margin-top: 1.8rem; }
        .sg-hero-note { font-family: var(--sg-mono); font-size: 0.78rem; letter-spacing: 0.04em; color: var(--sg-text-3); margin: 1.2rem 0 0; }
        .sg-hero-mock { position: relative; }
        .sg-dotgrid { position: absolute; inset: -14% -10%; z-index: 0; pointer-events: none;
            background-image: radial-gradient(rgba(255,255,255,0.05) 1px, transparent 1px); background-size: 22px 22px;
            -webkit-mask: radial-gradient(circle at 68% 42%, #000 28%, transparent 76%); mask: radial-gradient(circle at 68% 42%, #000 28%, transparent 76%); }

        /* ---- the faux chat-window mockup (sg-mock) ---- */
        .sg-mock { position: relative; z-index: 1; display: grid; grid-template-columns: 46px 148px 1fr;
            background: var(--sg-bg-2); border: 1px solid var(--sg-line-2); border-radius: var(--sg-r);
            box-shadow: var(--sg-mock-shadow); overflow: hidden; font-size: 13px; }
        .sg-mr { display: flex; flex-direction: column; align-items: center; gap: 0.5rem; padding: 0.7rem 0; background: var(--sg-bg); border-right: 1px solid var(--sg-line); }
        .sg-mr-i { width: 30px; height: 30px; border-radius: 9px; background: var(--sg-bg-3); display: grid; place-items: center; color: var(--sg-text-2); font-weight: 700; font-size: 12px; border: 1px solid var(--sg-line); }
        .sg-mr-i.on { background: var(--sg-accent); color: #06122B; border-color: var(--sg-accent); border-radius: 11px; }
        .sg-chan { padding: 0.6rem 0.55rem; background: var(--sg-bg-1); border-right: 1px solid var(--sg-line); min-width: 0; }
        .sg-chan-h { font-weight: 700; font-size: 12px; color: var(--sg-text); padding: 0.2rem 0.4rem 0.6rem; display: flex; align-items: center; gap: 0.35rem; border-bottom: 1px solid var(--sg-line); margin-bottom: 0.4rem; }
        .sg-ch { display: flex; align-items: center; gap: 0.35rem; padding: 0.32rem 0.45rem; border-radius: 6px; color: var(--sg-text-3); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .sg-ch .h { color: var(--sg-text-3); }
        .sg-ch.on { background: var(--sg-bg-3); color: var(--sg-text); }
        .sg-ch.lock { color: var(--sg-text-3); }
        .sg-ch .lk { margin-left: auto; color: var(--sg-warn); }
        .sg-conv { display: flex; flex-direction: column; min-width: 0; }
        .sg-conv-h { display: flex; align-items: center; gap: 0.5rem; padding: 0.6rem 0.8rem; border-bottom: 1px solid var(--sg-line); font-weight: 700; color: var(--sg-text); }
        .sg-conv-h .h { color: var(--sg-text-3); }
        .sg-cert { margin-left: auto; display: inline-flex; align-items: center; gap: 0.3rem; font-family: var(--sg-mono); font-size: 10px; letter-spacing: 0.05em; color: var(--sg-proof); border: 1px solid rgba(52,211,153,0.35); border-radius: var(--sg-pill); padding: 0.12rem 0.5rem; }
        .sg-pin { display: flex; align-items: center; gap: 0.4rem; padding: 0.4rem 0.8rem; background: var(--sg-bg-3); border-bottom: 1px solid var(--sg-line); color: var(--sg-text-2); font-size: 11.5px; }
        .sg-pin .ic { color: var(--sg-text-3); }
        .sg-msgs { padding: 0.7rem 0.8rem; display: flex; flex-direction: column; gap: 0.7rem; }
        .sg-msg { display: grid; grid-template-columns: 28px 1fr; gap: 0.55rem; align-items: start; }
        .sg-av { width: 28px; height: 28px; border-radius: 50%; display: grid; place-items: center; font-weight: 700; font-size: 11px; color: #06122B; }
        .sg-av.a { background: #7CC4FF; } .sg-av.b { background: #34D399; } .sg-av.c { background: #C4A0FF; } .sg-av.d { background: #F2C94C; }
        .sg-mname { font-weight: 700; color: var(--sg-text); }
        .sg-mname .role { font-family: var(--sg-mono); font-size: 9.5px; letter-spacing: 0.04em; text-transform: uppercase; padding: 0.05rem 0.35rem; border-radius: var(--sg-pill); margin-left: 0.35rem; vertical-align: middle; }
        .role.owner { color: var(--sg-accent); border: 1px solid rgba(91,140,255,0.4); }
        .role.admin { color: var(--sg-proof); border: 1px solid rgba(52,211,153,0.4); }
        .role.mod { color: var(--sg-warn); border: 1px solid rgba(242,201,76,0.4); }
        .sg-mtime { color: var(--sg-text-3); font-size: 10.5px; margin-left: 0.4rem; }
        .sg-mtext { color: var(--sg-text-2); line-height: 1.5; margin: 0.15rem 0 0; }
        .sg-mtext b { color: var(--sg-text); }
        .sg-mention { color: var(--sg-accent); background: rgba(91,140,255,0.13); border-radius: 4px; padding: 0 0.2em; }
        .sg-tag { color: var(--sg-accent); }
        .sg-code { font-family: var(--sg-mono); font-size: 11.5px; background: var(--sg-bg); border: 1px solid var(--sg-line); border-radius: 6px; padding: 0.5rem 0.6rem; margin-top: 0.35rem; color: var(--sg-text-2); overflow: hidden; }
        .sg-code .k { color: #C4A0FF; } .sg-code .s { color: var(--sg-proof); } .sg-code .c { color: var(--sg-text-3); }
        .sg-react-row { display: flex; gap: 0.35rem; margin-top: 0.4rem; }
        .sg-react { display: inline-flex; align-items: center; gap: 0.25rem; font-size: 11px; color: var(--sg-text-2); background: var(--sg-bg-3); border: 1px solid var(--sg-line); border-radius: var(--sg-pill); padding: 0.1rem 0.45rem; }
        .sg-react.on { border-color: rgba(91,140,255,0.5); color: var(--sg-text); }
        .sg-thread { display: inline-flex; align-items: center; gap: 0.3rem; margin-top: 0.4rem; font-size: 11px; color: var(--sg-accent); }
        .sg-reply { margin-top: 0.5rem; margin-left: 0.4rem; padding-left: 0.7rem; position: relative; }
        .sg-reply::before { content: ''; position: absolute; left: 0; top: -0.3rem; bottom: 0.7rem; width: 10px; border-left: 1.5px solid var(--sg-line-2); border-bottom: 1.5px solid var(--sg-line-2); border-bottom-left-radius: 7px; }
        .sg-composer { margin: auto 0.8rem 0.8rem; display: flex; align-items: center; gap: 0.5rem; color: var(--sg-text-3); background: var(--sg-bg); border: 1px solid var(--sg-line); border-radius: var(--sg-r-sm); padding: 0.55rem 0.7rem; }
        .sg-composer .send { margin-left: auto; width: 24px; height: 24px; border-radius: 6px; background: var(--sg-accent); color: #06122B; display: grid; place-items: center; font-weight: 700; }
        .sg-mock.wide { grid-template-columns: 56px 200px 1fr; font-size: 13px; }
        .sg-mock.ghost { opacity: 0.22; filter: blur(2px); }

        /* ---- proof strip ---- */
        .sg-proof-cells { display: grid; grid-template-columns: repeat(4, 1fr); border: 1px solid var(--sg-line); border-radius: var(--sg-r); overflow: hidden; background: var(--sg-bg-2); }
        .sg-fact { padding: 1.3rem 1.2rem; border-left: 1px solid var(--sg-line); }
        .sg-fact:first-child { border-left: 0; }
        .sg-fact-l { display: flex; align-items: center; gap: 0.4rem; font-family: var(--sg-mono); font-size: 0.7rem; letter-spacing: 0.1em; text-transform: uppercase; color: var(--sg-text-3); }
        .sg-fact-v { color: var(--sg-text); font-size: 0.98rem; margin-top: 0.5rem; font-weight: 600; }
        .sg-chip-wrap { display: flex; justify-content: center; margin-top: 1.4rem; }

        /* ---- big visual + callouts ---- */
        .sg-bigvis { margin-top: 2.2rem; position: relative; }
        .sg-callrow { display: flex; flex-wrap: wrap; gap: 0.6rem; margin-top: 1.2rem; }
        .sg-call { display: inline-flex; align-items: center; gap: 0.45rem; font-family: var(--sg-mono); font-size: 0.74rem; color: var(--sg-text-3); border: 1px solid var(--sg-line); border-radius: var(--sg-pill); padding: 0.35rem 0.7rem; }
        .sg-call b { color: var(--sg-text-2); font-weight: 600; }
        .sg-call i { font-style: normal; width: 7px; height: 7px; border-radius: 50%; }

        /* ---- deep-dive alternating ---- */
        .sg-deep { display: grid; grid-template-columns: 1fr 1fr; gap: clamp(2rem, 5vw, 4rem); align-items: center; }
        .sg-deep.reverse .sg-deep-copy { order: 2; }
        .sg-list { list-style: none; padding: 0; margin: 1.3rem 0 0; display: flex; flex-direction: column; gap: 0.8rem; }
        .sg-li { display: flex; gap: 0.6rem; align-items: flex-start; color: var(--sg-text-2); line-height: 1.5; }
        .sg-li b { color: var(--sg-text); font-weight: 600; }
        .sg-panel { background: var(--sg-bg-2); border: 1px solid var(--sg-line); border-radius: var(--sg-r); padding: 1.1rem; box-shadow: var(--sg-mock-shadow); }

        /* roles + moderation mock */
        .sg-members { display: flex; flex-direction: column; gap: 0.2rem; }
        .sg-member { display: flex; align-items: center; gap: 0.6rem; padding: 0.5rem 0.6rem; border-radius: var(--sg-r-sm); }
        .sg-member:hover { background: var(--sg-bg-3); }
        .sg-pres { width: 9px; height: 9px; border-radius: 50%; background: var(--sg-proof); box-shadow: 0 0 0 0 rgba(52,211,153,0.5); animation: sg-pulse 2.6s ease-out infinite; flex: 0 0 auto; }
        .sg-pres.idle { background: var(--sg-warn); animation: none; }
        .sg-pres.off { background: var(--sg-text-3); animation: none; }
        .sg-mname2 { color: var(--sg-text); font-weight: 600; }
        .sg-modmenu { margin-top: 1rem; border: 1px solid var(--sg-line); border-radius: var(--sg-r-sm); overflow: hidden; }
        .sg-modmenu .mi { display: flex; align-items: center; gap: 0.5rem; padding: 0.6rem 0.8rem; color: var(--sg-text-2); border-top: 1px solid var(--sg-line); font-size: 0.9rem; }
        .sg-modmenu .mi:first-child { border-top: 0; }
        .sg-modmenu .mi.warn { color: var(--sg-warn); }
        .sg-lockban { margin-top: 0.9rem; display: flex; align-items: center; gap: 0.5rem; font-size: 0.85rem; color: var(--sg-warn); background: rgba(242,201,76,0.08); border: 1px solid rgba(242,201,76,0.3); border-radius: var(--sg-r-sm); padding: 0.55rem 0.75rem; }

        /* rich messages mock */
        .sg-rich { display: flex; flex-direction: column; gap: 0.8rem; }
        .sg-spoiler { background: var(--sg-bg-3); border-radius: 5px; padding: 0 0.3em; color: var(--sg-text); filter: blur(5px); transition: filter 0.18s ease; cursor: pointer; }
        .sg-spoiler:hover { filter: blur(0); }
        .sg-imgtile { height: 90px; border-radius: var(--sg-r-sm); background: linear-gradient(135deg, #2A3550, #3D2A50 60%, #1C3540); border: 1px solid var(--sg-line); display: grid; place-items: center; color: rgba(255,255,255,0.5); font-size: 0.78rem; }
        .sg-video { position: relative; height: 120px; border-radius: var(--sg-r-sm); background: linear-gradient(135deg, #15181E, #232a36); border: 1px solid var(--sg-line); display: grid; place-items: center; overflow: hidden; }
        .sg-play { width: 44px; height: 44px; border-radius: 50%; background: rgba(255,255,255,0.12); border: 1px solid var(--sg-line-2); display: grid; place-items: center; color: #fff; transition: transform 0.15s ease, background 0.15s ease; }
        .sg-video:hover .sg-play { transform: scale(1.12); background: rgba(91,140,255,0.4); }

        /* privacy public-vs-member */
        .sg-privacy { display: grid; grid-template-columns: 1fr 1fr; gap: 1rem; margin-top: 0.4rem; }
        .sg-priv { position: relative; background: var(--sg-bg-2); border: 1px solid var(--sg-line); border-radius: var(--sg-r); padding: 0.9rem; overflow: hidden; }
        .sg-priv-tag { font-family: var(--sg-mono); font-size: 0.68rem; letter-spacing: 0.08em; text-transform: uppercase; display: flex; align-items: center; gap: 0.4rem; }
        .sg-priv.pub .sg-priv-tag { color: var(--sg-warn); }
        .sg-priv.mem .sg-priv-tag { color: var(--sg-proof); }
        .sg-priv-rows { margin-top: 0.7rem; display: flex; flex-direction: column; gap: 0.55rem; }
        .sg-priv-line { height: 10px; border-radius: 5px; background: var(--sg-bg-3); }
        .sg-priv-line.w1 { width: 80%; } .sg-priv-line.w2 { width: 95%; } .sg-priv-line.w3 { width: 60%; } .sg-priv-line.w4 { width: 88%; }
        .sg-priv.pub .sg-priv-rows { filter: blur(5px); -webkit-filter: blur(5px); opacity: 0.7; }
        .sg-priv.mem .sg-priv-real { display: flex; flex-direction: column; gap: 0.6rem; margin-top: 0.7rem; }
        .sg-priv-seal { position: absolute; top: 0.7rem; right: 0.8rem; }
        .sg-priv-foot { margin-top: 0.7rem; font-size: 0.74rem; color: var(--sg-text-3); }

        /* ---- spec table ---- */
        .sg-table { width: 100%; border-collapse: collapse; margin-top: 0.5rem; }
        .sg-table th, .sg-table td { text-align: left; padding: 0.95rem 1rem; border-bottom: 1px solid var(--sg-line); vertical-align: top; }
        .sg-table thead th { font-family: var(--sg-mono); font-size: 0.72rem; letter-spacing: 0.08em; text-transform: uppercase; color: var(--sg-text-3); font-weight: 600; }
        .sg-table thead th.chain { color: var(--sg-proof); }
        .sg-table tbody th { font-weight: 600; color: var(--sg-text); width: 32%; }
        .sg-table td.saas { color: var(--sg-text-3); }
        .sg-table td.chain { color: var(--sg-text); }
        .sg-table td.chain .sg-seal { vertical-align: -2px; margin-right: 0.4rem; }
        .sg-table tr:last-child th, .sg-table tr:last-child td { border-bottom: 0; }

        /* ---- verify terminal ---- */
        .sg-term { background: var(--sg-bg-2); border: 1px solid var(--sg-line-2); border-radius: var(--sg-r); overflow: hidden; box-shadow: var(--sg-mock-shadow); max-width: 760px; }
        .sg-term-bar { display: flex; align-items: center; gap: 0.45rem; padding: 0.6rem 0.85rem; border-bottom: 1px solid var(--sg-line); background: var(--sg-bg-1); }
        .sg-tdot { width: 11px; height: 11px; border-radius: 50%; }
        .sg-tdot.r { background: #F2675C; } .sg-tdot.y { background: var(--sg-warn); } .sg-tdot.g { background: var(--sg-proof); }
        .sg-term-title { margin-left: 0.4rem; font-family: var(--sg-mono); font-size: 0.74rem; color: var(--sg-text-3); }
        .sg-term-body { display: grid; grid-template-columns: 2.4rem 1fr; font-family: var(--sg-mono); font-size: 0.82rem; line-height: 1.85; padding: 0.8rem 0; }
        .sg-term-num { text-align: right; padding-right: 0.9rem; color: var(--sg-text-3); border-right: 1px solid var(--sg-line); -webkit-user-select: none; user-select: none; }
        .sg-term-code { padding: 0 1rem; color: var(--sg-text-2); overflow-x: auto; }
        .sg-term-code .p { color: var(--sg-proof); } .sg-term-code .a { color: var(--sg-accent); } .sg-term-code .c { color: var(--sg-text-3); }
        .sg-term-code b { color: var(--sg-text); font-weight: 600; }
        .sg-caret { display: inline-block; width: 8px; color: var(--sg-proof); animation: sg-blink 1.1s steps(1) infinite; }
        @keyframes sg-blink { 50% { opacity: 0; } }
        .sg-term-link { color: var(--sg-accent); text-decoration: none; border-bottom: 1px solid rgba(91,140,255,0.4); }
        .sg-term-link:hover { border-bottom-color: var(--sg-accent); }

        /* ---- feature index ---- */
        .sg-features { display: grid; grid-template-columns: repeat(3, 1fr); border: 1px solid var(--sg-line); border-radius: var(--sg-r); overflow: hidden; }
        .sg-feat { position: relative; padding: 1.3rem; border-left: 1px solid var(--sg-line); border-top: 1px solid var(--sg-line); transition: background 0.15s; }
        .sg-feat:hover { background: var(--sg-bg-1); }
        .sg-feat-ico { color: var(--sg-accent); display: inline-flex; }
        .sg-feat-name { font-weight: 600; color: var(--sg-text); margin-top: 0.7rem; display: flex; align-items: center; gap: 0.5rem; }
        .sg-feat-desc { font-size: 0.85rem; color: var(--sg-text-3); margin-top: 0.3rem; line-height: 1.45; }
        .sg-feat-seal { position: absolute; top: 1rem; right: 1rem; color: var(--sg-proof); opacity: 0; transition: opacity 0.18s; }
        .sg-feat:hover .sg-feat-seal { opacity: 1; }
        .sg-kbd { font-family: var(--sg-mono); font-size: 0.74rem; color: var(--sg-text-2); background: var(--sg-bg-3); border: 1px solid var(--sg-line-2); border-bottom-width: 2px; border-radius: 5px; padding: 0.05rem 0.4rem; }

        /* ---- final CTA ---- */
        .sg-final { position: relative; text-align: center; padding: clamp(4rem, 9vw, 7rem) clamp(1.25rem, 5vw, 3rem); overflow: hidden; }
        .sg-final-grid { position: absolute; inset: 0; z-index: 0; pointer-events: none;
            background-image: radial-gradient(rgba(255,255,255,0.05) 1px, transparent 1px); background-size: 24px 24px;
            -webkit-mask: radial-gradient(circle at 50% 50%, #000 8%, transparent 60%); mask: radial-gradient(circle at 50% 50%, #000 8%, transparent 60%); }
        .sg-final-glow { position: absolute; left: 50%; top: 50%; width: 700px; height: 420px; transform: translate(-50%, -50%); z-index: 0; pointer-events: none;
            background: radial-gradient(closest-side, var(--sg-glow), transparent 72%); }
        .sg-watermark { position: absolute; left: 50%; top: 46%; transform: translate(-50%, -50%); z-index: 0; color: rgba(52,211,153,0.05); pointer-events: none; }
        .sg-final-in { position: relative; z-index: 1; max-width: 720px; margin: 0 auto; }
        .sg-final .sg-h2 { font-size: clamp(2rem, 5vw, 3.2rem); }
        .sg-final .sg-cta-row { justify-content: center; }
        .sg-final-note { font-family: var(--sg-mono); font-size: 0.78rem; letter-spacing: 0.05em; color: var(--sg-text-3); margin-top: 1.3rem; }
        .sg-ghost-mock { position: relative; max-width: 520px; margin: 2.4rem auto 0; }

        /* ---- footer ---- */
        .sg-footer { position: relative; border-top: 1px solid var(--sg-line); background: var(--sg-bg-1); padding: 2.6rem clamp(1.25rem, 5vw, 3rem) 1.6rem; }
        .sg-footer::before { content: ''; position: absolute; top: -1px; left: 0; right: 0; height: 1px; background: linear-gradient(90deg, var(--sg-accent), transparent 60%); }
        .sg-foot-top { display: flex; flex-wrap: wrap; gap: 2rem; justify-content: space-between; max-width: 1140px; margin: 0 auto; }
        .sg-foot-brand { max-width: 260px; }
        .sg-foot-tag { color: var(--sg-text-3); font-size: 0.86rem; line-height: 1.5; margin-top: 0.7rem; }
        .sg-foot-cols { display: flex; gap: 3rem; flex-wrap: wrap; }
        .sg-foot-col h4 { font-family: var(--sg-mono); font-size: 0.7rem; letter-spacing: 0.1em; text-transform: uppercase; color: var(--sg-text-3); margin: 0 0 0.7rem; font-weight: 600; }
        .sg-foot-col a { display: block; color: var(--sg-text-2); text-decoration: none; font-size: 0.9rem; padding: 0.2rem 0; }
        .sg-foot-col a:hover { color: var(--sg-text); }
        .sg-foot-bottom { max-width: 1140px; margin: 1.8rem auto 0; padding-top: 1.3rem; border-top: 1px solid var(--sg-line); display: flex; flex-wrap: wrap; gap: 0.6rem; align-items: center; justify-content: space-between; font-family: var(--sg-mono); font-size: 0.74rem; color: var(--sg-text-3); }

        /* ---- scroll reveal (progressive enhancement only; visible by default) ---- */
        @supports (animation-timeline: view()) {
            @media (prefers-reduced-motion: no-preference) {
                .sg-reveal { opacity: 0; transform: translateY(16px); animation: sg-rise linear both; animation-timeline: view(); animation-range: entry 0% cover 22%; }
                .sg-fact { opacity: 0; transform: translateX(-14px); animation: sg-slide linear both; animation-timeline: view(); animation-range: entry 0% cover 26%; }
            }
        }
        @keyframes sg-rise { to { opacity: 1; transform: translateY(0); } }
        @keyframes sg-slide { to { opacity: 1; transform: translateX(0); } }

        /* ---- responsive ---- */
        @media (max-width: 980px) {
            .sg-features { grid-template-columns: repeat(2, 1fr); }
        }
        @media (max-width: 860px) {
            .sg-nav-links { display: none; }
            .sg-hero-grid, .sg-deep { grid-template-columns: 1fr; }
            .sg-deep.reverse .sg-deep-copy { order: 0; }
            .sg-hero-mock, .sg-deep-visual { max-width: 540px; }
            .sg-proof-cells { grid-template-columns: repeat(2, 1fr); }
            .sg-fact:nth-child(2) { border-left: 0; }
        }
        @media (max-width: 720px) {
            .sg-privacy { grid-template-columns: 1fr; }
            .sg-features { grid-template-columns: 1fr; }
            .sg-foot-cols { gap: 2rem; }
            .sg-table thead { display: none; }
            .sg-table, .sg-table tbody, .sg-table tr, .sg-table th, .sg-table td { display: block; width: auto; }
            .sg-table tr { border: 1px solid var(--sg-line); border-radius: var(--sg-r-sm); margin-bottom: 0.8rem; padding: 0.4rem 0.6rem; }
            .sg-table th, .sg-table td { border-bottom: 0; padding: 0.4rem 0; }
            .sg-table tbody th { width: auto; }
        }
        @media (max-width: 520px) {
            .sg-proof-cells { grid-template-columns: 1fr; }
            .sg-fact { border-left: 0; border-top: 1px solid var(--sg-line); }
            .sg-fact:first-child { border-top: 0; }
            .sg-nav-open { display: none; }
        }
        @media (prefers-reduced-motion: reduce) {
            .sg-dot, .sg-pres, .sg-caret { animation: none !important; }
            .sg-reveal, .sg-fact { opacity: 1 !important; transform: none !important; animation: none !important; }
        }
        /* ==== F1 Forum kind (fr-*) + server-kind picker (dc-kind*) ==== */
        .dc-kind-pick { display: flex; gap: 3px; margin: 4px 0; justify-content: center; }
        .dc-kindbtn { width: 26px; height: 26px; border-radius: 7px; border: 1px solid var(--sg-line, rgba(255,255,255,0.12)); background: var(--sg-bg-2, #15181e); color: var(--sg-text-2, #a8b0bd); font-size: 13px; line-height: 1; cursor: pointer; display: grid; place-items: center; transition: border-color .12s, background .12s; }
        .dc-kindbtn:hover { border-color: var(--sg-text-3, #6e7787); }
        .dc-kindbtn.on { border-color: var(--sg-accent, #5b8cff); background: rgba(91,140,255,0.16); color: var(--sg-text, #edeff3); }

        .fr { height: 100%; overflow-y: auto; background: var(--sg-bg, #0a0b0d); color: var(--sg-text, #edeff3); font-family: inherit; }
        .fr a { color: inherit; text-decoration: none; }
        .fr-bar { position: sticky; top: 0; z-index: 5; display: flex; align-items: center; gap: 0.5rem; padding: 0.7rem clamp(1rem, 4vw, 2rem); background: rgba(10,11,13,0.78); -webkit-backdrop-filter: blur(8px); backdrop-filter: blur(8px); border-bottom: 1px solid var(--sg-line, rgba(255,255,255,0.08)); font-size: 0.9rem; }
        .fr-bar-kind { font-family: var(--sg-mono, ui-monospace, monospace); font-size: 0.72rem; letter-spacing: 0.12em; text-transform: uppercase; color: var(--sg-accent, #5b8cff); }
        .fr-bar-sep { color: var(--sg-text-3, #6e7787); }
        .fr-back { color: var(--sg-text-2, #a8b0bd); }
        .fr-back:hover { color: var(--sg-text, #edeff3); }
        .fr-cat-name { color: var(--sg-text, #edeff3); font-weight: 600; }
        .fr-app-link { margin-left: auto; color: var(--sg-text-3, #6e7787); font-size: 0.82rem; }
        .fr-app-link:hover { color: var(--sg-text-2, #a8b0bd); }

        .fr-cat-head { display: flex; align-items: flex-start; gap: 1rem; max-width: 880px; margin: 0 auto; padding: clamp(1.4rem, 4vw, 2.4rem) clamp(1rem, 4vw, 2rem) 0.5rem; }
        .fr-cat-head h1 { font-size: clamp(1.5rem, 3.4vw, 2.1rem); font-weight: 700; letter-spacing: -0.02em; margin: 0; }
        .fr-cat-sub { color: var(--sg-text-3, #6e7787); font-size: 0.9rem; margin: 0.3rem 0 0; }
        .fr-newtopic { margin-left: auto; flex: 0 0 auto; }

        .fr-sorts { display: flex; gap: 0.3rem; max-width: 880px; margin: 0.8rem auto 0; padding: 0 clamp(1rem, 4vw, 2rem); }
        .fr-sort { font-size: 0.85rem; color: var(--sg-text-3, #6e7787); padding: 0.3rem 0.7rem; border-radius: 999px; border: 1px solid transparent; }
        .fr-sort:hover { color: var(--sg-text, #edeff3); }
        .fr-sort.on { color: var(--sg-text, #edeff3); border-color: var(--sg-line-2, rgba(255,255,255,0.13)); background: var(--sg-bg-2, #15181e); }

        .fr-newform { max-width: 880px; margin: 1rem auto 0; padding: 1rem; background: var(--sg-bg-2, #15181e); border: 1px solid var(--sg-line, rgba(255,255,255,0.08)); border-radius: 12px; }
        .fr-newform-title, .fr-newform-body, .fr-reply-input { width: 100%; box-sizing: border-box; background: var(--sg-bg, #0a0b0d); color: var(--sg-text, #edeff3); border: 1px solid var(--sg-line-2, rgba(255,255,255,0.13)); border-radius: 8px; padding: 0.6rem 0.7rem; font: inherit; font-size: 0.95rem; resize: vertical; }
        .fr-newform-title:focus, .fr-newform-body:focus, .fr-reply-input:focus { outline: none; border-color: var(--sg-accent, #5b8cff); box-shadow: 0 0 0 3px rgba(91,140,255,0.16); }
        .fr-newform-title { margin-bottom: 0.6rem; }
        .fr-newform-actions { display: flex; gap: 0.5rem; margin-top: 0.7rem; }

        .fr-btn { display: inline-flex; align-items: center; gap: 0.4rem; font: inherit; font-weight: 600; font-size: 0.9rem; padding: 0.55rem 1rem; border-radius: 8px; cursor: pointer; border: 1px solid transparent; transition: background .12s, border-color .12s, transform .08s; }
        .fr-btn:active { transform: translateY(1px); }
        .fr-btn-primary { background: var(--sg-accent, #5b8cff); color: #06122b; }
        .fr-btn-primary:hover { background: #6e9bff; }
        .fr-btn-ghost { background: transparent; color: var(--sg-text, #edeff3); border-color: var(--sg-line-2, rgba(255,255,255,0.13)); }
        .fr-btn-ghost:hover { border-color: var(--sg-text-3, #6e7787); }

        .fr-list { list-style: none; max-width: 880px; margin: 0.8rem auto 2rem; padding: 0 clamp(1rem, 4vw, 2rem); }
        .fr-row { display: flex; align-items: center; gap: 1rem; padding: 0.85rem 0.4rem; border-bottom: 1px solid var(--sg-line, rgba(255,255,255,0.07)); }
        .fr-row:hover { background: var(--sg-bg-1, #0f1116); }
        .fr-row-main { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 0.2rem; }
        .fr-row-title { font-weight: 600; color: var(--sg-text, #edeff3); display: flex; align-items: center; gap: 0.4rem; }
        .fr-row-meta { font-size: 0.8rem; color: var(--sg-text-3, #6e7787); }
        .fr-row-stat { flex: 0 0 auto; width: 64px; text-align: center; color: var(--sg-text-2, #a8b0bd); display: flex; flex-direction: column; }
        .fr-row-stat b { font-size: 0.95rem; color: var(--sg-text, #edeff3); }
        .fr-row-stat small { font-size: 0.68rem; color: var(--sg-text-3, #6e7787); text-transform: uppercase; letter-spacing: 0.04em; }
        .fr-row-when { flex: 0 0 auto; width: 84px; text-align: right; font-size: 0.8rem; color: var(--sg-text-3, #6e7787); }
        .fr-solved-dot, .fr-seal { color: var(--sg-proof, #34d399); }

        .fr-topic { max-width: 820px; margin: 0 auto; padding: clamp(1.2rem, 4vw, 2rem) clamp(1rem, 4vw, 2rem) 3rem; }
        .fr-title { font-size: clamp(1.4rem, 3.2vw, 2rem); font-weight: 700; letter-spacing: -0.02em; margin: 0 0 1.2rem; display: flex; align-items: center; gap: 0.6rem; flex-wrap: wrap; }
        .fr-solved-tag { display: inline-flex; align-items: center; gap: 0.3rem; font-size: 0.78rem; font-weight: 600; color: var(--sg-proof, #34d399); border: 1px solid rgba(52,211,153,0.4); border-radius: 999px; padding: 0.15rem 0.6rem; }
        .fr-lock-tag { font-size: 0.78rem; color: var(--sg-warn, #f2c94c); border: 1px solid rgba(242,201,76,0.4); border-radius: 999px; padding: 0.15rem 0.6rem; }
        .fr-post { padding: 1rem 0; border-bottom: 1px solid var(--sg-line, rgba(255,255,255,0.07)); }
        .fr-op { background: linear-gradient(180deg, rgba(91,140,255,0.05), transparent); border-radius: 10px; padding: 1rem; border: 1px solid var(--sg-line, rgba(255,255,255,0.07)); }
        .fr-answer { background: rgba(52,211,153,0.06); border-left: 3px solid var(--sg-proof, #34d399); border-radius: 8px; padding-left: 0.8rem; }
        .fr-post-head { display: flex; align-items: center; gap: 0.5rem; margin-bottom: 0.5rem; flex-wrap: wrap; }
        .fr-av { width: 30px; height: 30px; border-radius: 50%; display: grid; place-items: center; color: #fff; font-weight: 700; font-size: 13px; flex: 0 0 auto; }
        .fr-author { font-weight: 600; color: var(--sg-text, #edeff3); }
        .fr-op-badge { font-family: var(--sg-mono, monospace); font-size: 0.62rem; letter-spacing: 0.06em; color: var(--sg-accent, #5b8cff); border: 1px solid rgba(91,140,255,0.4); border-radius: 999px; padding: 0.05rem 0.4rem; }
        .fr-answer-tag { display: inline-flex; align-items: center; gap: 0.25rem; font-size: 0.72rem; font-weight: 600; color: var(--sg-proof, #34d399); }
        .fr-time { font-size: 0.78rem; color: var(--sg-text-3, #6e7787); }
        .fr-solve { margin-left: auto; font: inherit; font-size: 0.74rem; color: var(--sg-text-3, #6e7787); background: transparent; border: 1px solid var(--sg-line, rgba(255,255,255,0.1)); border-radius: 6px; padding: 0.2rem 0.5rem; cursor: pointer; }
        .fr-solve:hover { color: var(--sg-proof, #34d399); border-color: rgba(52,211,153,0.4); }
        .fr-body { color: var(--sg-text-2, #a8b0bd); line-height: 1.6; word-wrap: break-word; overflow-wrap: anywhere; }
        .fr-body code { font-family: var(--sg-mono, monospace); background: var(--sg-bg-2, #15181e); border: 1px solid var(--sg-line, rgba(255,255,255,0.08)); border-radius: 5px; padding: 0.05em 0.35em; font-size: 0.88em; }
        .fr-body strong { color: var(--sg-text, #edeff3); }
        .fr-body a { color: var(--sg-accent, #5b8cff); text-decoration: underline; }
        .fr-react { margin-top: 0.5rem; display: flex; gap: 0.35rem; flex-wrap: wrap; }
        .fr-react-pill { font-size: 0.78rem; color: var(--sg-text-2, #a8b0bd); background: var(--sg-bg-2, #15181e); border: 1px solid var(--sg-line, rgba(255,255,255,0.08)); border-radius: 999px; padding: 0.05rem 0.45rem; }
        .fr-replies-h { font-family: var(--sg-mono, monospace); font-size: 0.72rem; letter-spacing: 0.08em; text-transform: uppercase; color: var(--sg-text-3, #6e7787); margin: 1.2rem 0 0.4rem; }
        .fr-reply-box { margin-top: 1.4rem; }
        .fr-reply-input { margin-bottom: 0.5rem; }
        .fr-locked-note, .fr-signin-note { color: var(--sg-text-3, #6e7787); font-size: 0.88rem; margin-top: 1.2rem; }
        .fr-empty { max-width: 820px; margin: 3rem auto; text-align: center; color: var(--sg-text-3, #6e7787); padding: 0 1rem; }
        .fr-empty h1 { color: var(--sg-text, #edeff3); }
        .fr-empty a { color: var(--sg-accent, #5b8cff); }
        @media (max-width: 640px) {
            .fr-row-stat:nth-child(3) { display: none; }
            .fr-cat-head { flex-wrap: wrap; }
        }
        /* ==== F2 Feed kind (fd-*) — X-style timeline ==== */
        .fd { height: 100%; overflow-y: auto; background: var(--sg-bg, #0a0b0d); color: var(--sg-text, #edeff3); font-family: inherit; }
        .fd a { color: inherit; text-decoration: none; }
        .fd-bar { position: sticky; top: 0; z-index: 5; display: flex; align-items: center; gap: 0.5rem; padding: 0.7rem clamp(1rem, 4vw, 2rem); background: rgba(10,11,13,0.78); -webkit-backdrop-filter: blur(8px); backdrop-filter: blur(8px); border-bottom: 1px solid var(--sg-line, rgba(255,255,255,0.08)); font-size: 0.9rem; }
        .fd-bar-kind { font-family: var(--sg-mono, ui-monospace, monospace); font-size: 0.72rem; letter-spacing: 0.12em; text-transform: uppercase; color: var(--sg-accent, #5b8cff); }
        .fd-bar-sep { color: var(--sg-text-3, #6e7787); }
        .fd-back, .fd-cat-name { color: var(--sg-text-2, #a8b0bd); }
        .fd-cat-name { color: var(--sg-text, #edeff3); font-weight: 600; }
        .fd-app-link { margin-left: auto; color: var(--sg-text-3, #6e7787); font-size: 0.82rem; }
        .fd-app-link:hover { color: var(--sg-text-2, #a8b0bd); }
        .fd-stream { max-width: 600px; margin: 0 auto; padding-bottom: 3rem; }
        .fd-head { display: flex; align-items: flex-start; gap: 1rem; padding: clamp(1.2rem, 4vw, 2rem) 1rem 1rem; }
        .fd-head h1 { font-size: clamp(1.4rem, 3.2vw, 1.9rem); font-weight: 700; letter-spacing: -0.02em; margin: 0; }
        .fd-sub { color: var(--sg-text-3, #6e7787); font-size: 0.88rem; margin: 0.3rem 0 0; }
        .fd-follow { margin-left: auto; flex: 0 0 auto; }
        .fd-compose { display: flex; gap: 0.6rem; align-items: flex-end; padding: 0.8rem 1rem; border-bottom: 8px solid var(--sg-bg-1, #0f1116); border-top: 1px solid var(--sg-line, rgba(255,255,255,0.08)); }
        .fd-input { flex: 1; box-sizing: border-box; background: var(--sg-bg, #0a0b0d); color: var(--sg-text, #edeff3); border: 1px solid var(--sg-line-2, rgba(255,255,255,0.13)); border-radius: 10px; padding: 0.6rem 0.7rem; font: inherit; font-size: 0.98rem; resize: vertical; }
        .fd-input:focus { outline: none; border-color: var(--sg-accent, #5b8cff); box-shadow: 0 0 0 3px rgba(91,140,255,0.16); }
        .fd-btn { display: inline-flex; align-items: center; gap: 0.4rem; font: inherit; font-weight: 600; font-size: 0.9rem; padding: 0.55rem 1.1rem; border-radius: 999px; cursor: pointer; border: 1px solid transparent; transition: background .12s, border-color .12s, transform .08s; }
        .fd-btn:active { transform: translateY(1px); }
        .fd-btn-primary { background: var(--sg-accent, #5b8cff); color: #06122b; }
        .fd-btn-primary:hover { background: #6e9bff; }
        .fd-btn-ghost { background: transparent; color: var(--sg-text, #edeff3); border-color: var(--sg-line-2, rgba(255,255,255,0.13)); }
        .fd-btn-ghost:hover { border-color: var(--sg-text-3, #6e7787); }
        .fd-srctag { padding: 0.6rem 1rem 0; font-family: var(--sg-mono, monospace); font-size: 0.72rem; letter-spacing: 0.04em; }
        .fd-srctag a { color: var(--sg-accent, #5b8cff); }
        .fd-post { display: flex; gap: 0.7rem; padding: 0.9rem 1rem; border-bottom: 1px solid var(--sg-line, rgba(255,255,255,0.07)); }
        .fd-post:hover { background: var(--sg-bg-1, #0f1116); }
        .fd-post-big { background: var(--sg-bg-1, #0f1116); }
        .fd-av { width: 40px; height: 40px; border-radius: 50%; display: grid; place-items: center; color: #fff; font-weight: 700; font-size: 15px; flex: 0 0 auto; }
        .fd-post-main { flex: 1; min-width: 0; }
        .fd-post-head { display: flex; align-items: baseline; gap: 0.4rem; }
        .fd-author { font-weight: 700; color: var(--sg-text, #edeff3); }
        .fd-time { font-size: 0.8rem; color: var(--sg-text-3, #6e7787); }
        .fd-body { color: var(--sg-text, #edeff3); line-height: 1.5; margin-top: 0.2rem; word-wrap: break-word; overflow-wrap: anywhere; }
        .fd-post-big .fd-body { font-size: 1.15rem; }
        .fd-body code { font-family: var(--sg-mono, monospace); background: var(--sg-bg-2, #15181e); border: 1px solid var(--sg-line, rgba(255,255,255,0.08)); border-radius: 5px; padding: 0.05em 0.35em; font-size: 0.88em; }
        .fd-body strong { font-weight: 700; }
        .fd-actions { display: flex; gap: 1.4rem; margin-top: 0.6rem; }
        .fd-act { display: inline-flex; align-items: center; gap: 0.35rem; font: inherit; font-size: 0.82rem; color: var(--sg-text-3, #6e7787); background: transparent; border: none; cursor: pointer; padding: 0; transition: color .12s; }
        .fd-act:hover { color: var(--sg-accent, #5b8cff); }
        .fd-replies-h { font-family: var(--sg-mono, monospace); font-size: 0.72rem; letter-spacing: 0.08em; text-transform: uppercase; color: var(--sg-text-3, #6e7787); padding: 0.9rem 1rem 0.3rem; border-top: 1px solid var(--sg-line, rgba(255,255,255,0.07)); }
        .fd-reply-box { display: flex; gap: 0.6rem; align-items: flex-end; padding: 0.8rem 1rem; border-bottom: 1px solid var(--sg-line, rgba(255,255,255,0.07)); }
        .fd-signin-note { color: var(--sg-text-3, #6e7787); font-size: 0.88rem; padding: 0.9rem 1rem; }
        .fd-empty { text-align: center; color: var(--sg-text-3, #6e7787); padding: 2.5rem 1rem; }
        .fd-empty h1 { color: var(--sg-text, #edeff3); }
        .fd-empty a, .fd-empty strong { color: var(--sg-accent, #5b8cff); }
        /* F2 following-home tabs */
        .fd-tabs { display: flex; border-bottom: 1px solid var(--sg-line, rgba(255,255,255,0.08)); }
        .fd-tab { flex: 1; font: inherit; font-weight: 600; font-size: 0.92rem; color: var(--sg-text-3, #6e7787); background: transparent; border: none; padding: 0.9rem 0; cursor: pointer; position: relative; transition: color .12s, background .12s; }
        .fd-tab:hover { color: var(--sg-text, #edeff3); background: rgba(255,255,255,0.03); }
        .fd-tab.on { color: var(--sg-text, #edeff3); }
        .fd-tab.on::after { content: ''; position: absolute; left: 50%; transform: translateX(-50%); bottom: -1px; width: 56px; height: 3px; border-radius: 3px; background: var(--sg-accent, #5b8cff); }
        .fd-srcinline { color: var(--sg-text-3, #6e7787); font-size: 0.82rem; font-weight: 500; }
        .fd-srcinline:hover { color: var(--sg-accent, #5b8cff); }
        .fd-following-stream { min-height: 40px; }
        /* F2-A user-follow button + repost tag */
        .fd-followbtn { margin-left: auto; font: inherit; font-size: 0.76rem; font-weight: 600; color: var(--sg-accent, #5b8cff); background: transparent; border: 1px solid var(--sg-line-2, rgba(255,255,255,0.13)); border-radius: 999px; padding: 0.16rem 0.7rem; cursor: pointer; transition: border-color .12s, color .12s; }
        .fd-followbtn:hover { border-color: var(--sg-accent, #5b8cff); }
        .fd-followbtn.on { color: var(--sg-text-3, #6e7787); }
        .fd-repost-by { font-size: 0.76rem; color: var(--sg-text-3, #6e7787); padding: 0.5rem 1rem 0 3.4rem; }
        /* voting + load-more (forum/feed) */
        .fr-postfoot { display: flex; align-items: center; gap: 0.9rem; margin-top: 0.5rem; flex-wrap: wrap; }
        .fr-votes { display: inline-flex; align-items: center; gap: 0.3rem; }
        .fr-vote { font: inherit; font-size: 0.8rem; line-height: 1; color: var(--sg-text-3, #6e7787); background: transparent; border: 1px solid var(--sg-line, rgba(255,255,255,0.1)); border-radius: 6px; padding: 0.16rem 0.42rem; cursor: pointer; transition: color .12s, border-color .12s; }
        .fr-vote:hover { color: var(--sg-accent, #5b8cff); border-color: var(--sg-accent, #5b8cff); }
        .fr-vote-score { font-size: 0.85rem; font-weight: 700; color: var(--sg-text-2, #a8b0bd); min-width: 1.4em; text-align: center; }
        .fr-vote-score.pos { color: var(--sg-proof, #34d399); }
        .fr-vote-score.neg { color: #F2675C; }
        .fr-loadmore, .fd-loadmore { display: block; text-align: center; margin: 1rem auto; padding: 0.6rem 1rem; color: var(--sg-accent, #5b8cff); font: inherit; font-weight: 600; font-size: 0.9rem; background: transparent; border: 1px solid var(--sg-line-2, rgba(255,255,255,0.13)); border-radius: 8px; cursor: pointer; max-width: 320px; text-decoration: none; }
        .fr-loadmore:hover, .fd-loadmore:hover { background: rgba(91,140,255,0.08); border-color: var(--sg-accent, #5b8cff); }
        /* forum tags + tag-filter banner + chat load-older */
        .fr-tagfilter { max-width: 880px; margin: 0.6rem auto 0; padding: 0 clamp(1rem,4vw,2rem); display: flex; align-items: center; gap: 0.5rem; font-size: 0.85rem; color: var(--sg-text-3, #6e7787); }
        .fr-tagfilter-label { color: var(--sg-text-3, #6e7787); }
        .fr-tagfilter-clear { color: var(--sg-accent, #5b8cff); text-decoration: none; }
        .fr-tagfilter-clear:hover { text-decoration: underline; }
        .fr-row-tags { display: flex; gap: 0.3rem; flex-wrap: wrap; align-items: center; max-width: 240px; }
        .fr-tag { font-size: 0.72rem; font-weight: 600; border-radius: 999px; padding: 0.08rem 0.5rem; text-decoration: none; white-space: nowrap; line-height: 1.4; }
        .fr-tag:hover { filter: brightness(1.15); }
        .dc-loadolder { display: block; width: max-content; margin: 0.5rem auto 0.8rem; padding: 0.4rem 0.9rem; color: #b5bac1; font-size: 0.84rem; background: transparent; border: 1px solid rgba(255,255,255,0.12); border-radius: 999px; text-decoration: none; }
        .dc-loadolder:hover { color: #fff; border-color: rgba(255,255,255,0.3); background: rgba(255,255,255,0.04); }
    </style>
</head>
<body data-wasp-canister=""" + AdminService.CanisterIdText() + @""">
    <div id=""wasp-root"">" + contentHtml + @"</div>
    <script src=""/_wasp/wasp.js""></script>
    <script type=""module"">" + IiClientScript + @"</script>
    <script type=""module"">" + DmClientScript + @"</script>
    <script type=""module"">" + ServerAdminScript + @"</script>
</body>
</html>";
    }

    // ── Internet Identity client (cosmetic) ────────────────────────────
    // Pulls @dfinity/auth-client from esm.sh (auto-bundles deps for ESM).
    // Stores {principal, displayName} in localStorage and force-fills any
    // <input name=""username""> on the page so the existing form-args path
    // (wasp.js) carries the chosen name into every handler — no canister
    // changes needed beyond the /api/identity/bind + /api/identity/lookup
    // endpoints. Not signed-request auth (anyone can fake the value);
    // see IdentityService for the security rationale.
    private const string IiClientScript = @"
// Pinned via esm.sh ?deps= because unversioned imports in @dfinity/identity
// resolve to @dfinity/candid@3.x where bufFromBufLike was renamed to
// uint8FromBufLike, breaking delegation.ts:14. Pinning candid@2.4.1 keeps
// the legacy export name available.
const II_BUNDLE_URL = '/_wasp/dfinity.js';
const IDENTITY_PROVIDER = 'https://identity.ic0.app';
const LS_PRINCIPAL = 'wasp:ii:principal';
const LS_NAME = 'wasp:ii:name';

// Lazy import: if esm.sh hiccups, the top-level module still evaluates
// so applyState() syncs the signed-in UI from localStorage. The import
// only runs the first time the user clicks sign-in / sign-out.
let _authClientCtor = null;
let authClient = null;
async function getClient() {
  if (!_authClientCtor) {
    const mod = await import(II_BUNDLE_URL);
    _authClientCtor = mod.AuthClient;
  }
  if (!authClient) authClient = await _authClientCtor.create();
  return authClient;
}

function stored() {
  return {
    principal: localStorage.getItem(LS_PRINCIPAL),
    name: localStorage.getItem(LS_NAME),
  };
}

function applyState() {
  const { principal, name } = stored();
  const body = document.body;
  if (principal && name) {
    body.setAttribute('data-ii-state', 'signed-in');
    document.querySelectorAll('[data-ii-signin]').forEach(el => { el.hidden = true; });
    document.querySelectorAll('[data-ii-card]').forEach(el => { el.hidden = false; });
    document.querySelectorAll('[data-ii-name]').forEach(el => { el.textContent = '@' + name; });
    document.querySelectorAll('[data-ii-avatar]').forEach(el => {
      el.textContent = (name[0] || '?').toUpperCase();
      // hue from name so the avatar matches the chat rail tint
      let h = 0; for (const c of name) h = (h * 131 + c.charCodeAt(0)) & 0xFFFFFF;
      el.style.background = 'hsl(' + (h % 360) + ', 50%, 50%)';
    });
    // Principal (on-chain ID): show a truncated form, full value on hover (title)
    // + data-principal for click-to-copy.
    document.querySelectorAll('[data-ii-principal]').forEach(el => {
      const short = principal.length > 14 ? (principal.slice(0, 8) + '…' + principal.slice(-4)) : principal;
      el.textContent = 'ID: ' + short;
      el.setAttribute('title', principal);
      el.setAttribute('data-principal', principal);
    });
    // Force-fill any visible username inputs. Tag them as locked so
    // a wandering user can't accidentally type a different name into
    // the chat sidebar while signed in.
    document.querySelectorAll('input[name=""username""]').forEach(el => {
      if (el.value !== name) el.value = name;
      el.readOnly = true;
      el.setAttribute('data-ii-locked', '1');
    });
  } else {
    body.setAttribute('data-ii-state', 'signed-out');
    document.querySelectorAll('[data-ii-signin]').forEach(el => { el.hidden = false; });
    document.querySelectorAll('[data-ii-card]').forEach(el => { el.hidden = true; });
    document.querySelectorAll('input[name=""username""][data-ii-locked]').forEach(el => {
      el.readOnly = false;
      el.removeAttribute('data-ii-locked');
      el.value = '';
    });
  }
}

async function signIn() {
  const btn = document.querySelector('[data-ii-signin]');
  if (btn) btn.disabled = true;
  try {
    const client = await getClient();
    await new Promise((resolve, reject) => {
      client.login({
        identityProvider: IDENTITY_PROVIDER,
        maxTimeToLive: BigInt(7) * BigInt(24) * BigInt(3600) * BigInt(1_000_000_000),
        onSuccess: resolve,
        onError: reject,
      });
    });
    const principal = client.getIdentity().getPrincipal().toText();
    let name = null;
    try {
      const r = await fetch('/api/identity/lookup?p=' + encodeURIComponent(principal));
      if (r.ok) {
        const data = await r.json();
        if (data.bound && data.name) name = data.name;
      }
    } catch (_) {}
    if (!name) {
      const guess = 'User-' + principal.slice(0, 5);
      const picked = prompt('Pick a display name (used in chat, CRM, etc.):', guess);
      name = (picked && picked.trim()) || guess;
      try {
        // SIGNED bind (free sign-up): binds THIS principal via msg_caller
        // (ServerAdminScript.waspBindName uses the same II session). Falls back
        // to the picked name if the signed client is not ready.
        if (window.waspBindName) name = await window.waspBindName(name);
      } catch (e) { console.warn('[ii] bind failed', e); }
    }
    localStorage.setItem(LS_PRINCIPAL, principal);
    localStorage.setItem(LS_NAME, name);
    applyState();
    // Seed the global online-presence with the new name immediately
    // so the chat member rail picks us up without waiting for the
    // next 10-second heartbeat tick.
    try {
      const onlineId = localStorage.getItem('wasp-online-id') || principal;
      fetch('/api/online-ping?p=' + encodeURIComponent(onlineId)
          + '&n=' + encodeURIComponent(name),
        { method: 'POST', body: '{}', headers: { 'content-type': 'application/json' } });
    } catch (_) {}
  } catch (e) {
    console.warn('[ii] sign-in failed', e);
  } finally {
    if (btn) btn.disabled = false;
  }
}

async function signOut() {
  try {
    const client = await getClient();
    await client.logout();
  } catch (_) {}
  localStorage.removeItem(LS_PRINCIPAL);
  localStorage.removeItem(LS_NAME);
  applyState();
}

document.addEventListener('click', (e) => {
  const t = e.target;
  if (t.closest && t.closest('[data-ii-signin]')) { e.preventDefault(); signIn(); return; }
  if (t.closest && t.closest('[data-ii-signout]')) { e.preventDefault(); signOut(); return; }
  const cp = t.closest && t.closest('[data-copy-principal]');
  if (cp) {
    e.preventDefault();
    const p = cp.getAttribute('data-principal') || '';
    if (!p) return;
    const flash = (msg) => { const prev = cp.textContent; cp.textContent = msg; setTimeout(() => { cp.textContent = prev; }, 1200); };
    if (navigator.clipboard && navigator.clipboard.writeText) navigator.clipboard.writeText(p).then(() => flash('Copied!')).catch(() => flash(p));
    else flash(p);
    return;
  }
});

// Re-apply on every SPA-style DOM swap (wasp.js replaces #wasp-root on
// nav). MutationObserver is the lightest reliable hook.
const root = document.getElementById('wasp-root');
if (root) {
  let pending = null;
  new MutationObserver(() => {
    if (pending) return;
    pending = requestAnimationFrame(() => { pending = null; applyState(); });
  }).observe(root, { childList: true, subtree: true });
}
applyState();
";

    private const string DmClientScript = @"
import { Actor, HttpAgent } from '/_wasp/dfinity.js';
import { AuthClient } from '/_wasp/dfinity.js';
import { IDL } from '/_wasp/dfinity.js';
const canisterId = document.body.getAttribute('data-wasp-canister');
const httpReq = IDL.Record({ method: IDL.Text, url: IDL.Text, headers: IDL.Vec(IDL.Tuple(IDL.Text, IDL.Text)), body: IDL.Vec(IDL.Nat8) });
const httpResp = IDL.Record({ status_code: IDL.Nat16, headers: IDL.Vec(IDL.Tuple(IDL.Text, IDL.Text)), body: IDL.Vec(IDL.Nat8), upgrade: IDL.Opt(IDL.Bool) });
const idlFactory = ({ IDL }) => IDL.Service({ http_request_update: IDL.Func([httpReq], [httpResp], []) });
let authClient, agent, actor, myPrincipal = '2vxsx-fae';
async function ensureAgent() {
  authClient = authClient || await AuthClient.create();
  const identity = authClient.getIdentity();
  myPrincipal = identity.getPrincipal().toText();
  agent = new HttpAgent({ identity, host: 'https://icp0.io' });
  actor = Actor.createActor(idlFactory, { agent, canisterId });
}
// ALWAYS update calls (signed). A query call would land anonymous.
async function signedCall(method, url, jsonBody) {
  const body = jsonBody ? Array.from(new TextEncoder().encode(jsonBody)) : [];
  const resp = await actor.http_request_update({ method, url, headers: jsonBody ? [['content-type','application/json']] : [], body });
  return { status: Number(resp.status_code), text: new TextDecoder().decode(new Uint8Array(resp.body)) };
}
function signedIn() { return !!localStorage.getItem('wasp:ii:principal'); }
function qparam(k) { return new URLSearchParams(location.search).get(k); }
// SPA navigation: synthesize an internal-link click so wasp.js's anchor
// interceptor routes it through /_wasp/render (which honours the query),
// instead of a full page load that hits the query-stripped /chat snapshot.
function spaNav(p){ const a=document.createElement('a'); a.href=p; document.body.appendChild(a); a.click(); a.remove(); }
function esc(s){ const d=document.createElement('div'); d.textContent=s||''; return d.innerHTML; }
function hue(n){ let h=0; for(const c of (n||'?')) h=(h*131+c.charCodeAt(0))&0xFFFFFF; return h%360; }
function msgRow(m){
  const h = hue(m.from);
  return '<div class=""dc-message""><div class=""dc-avatar"" style=""background:hsl('+h+',50%,50%)"">'+esc(((m.from||'?')[0]||'?').toUpperCase())+'</div>'+
    '<div class=""dc-message-body""><div class=""dc-message-head""><span class=""dc-username"" style=""color:hsl('+h+',60%,65%)"">'+esc(m.from)+'</span></div>'+
    '<div class=""dc-text"">'+esc(m.text)+'</div></div></div>';
}
async function loadThreads(){ const r=await signedCall('POST','/api/dm/threads','{}'); if(r.status!==200) return []; try{return JSON.parse(r.text).threads||[];}catch{return [];} }
async function loadMessages(peer){ const r=await signedCall('POST','/api/dm/read',JSON.stringify({peer})); if(r.status!==200) return []; try{return JSON.parse(r.text).messages||[];}catch{return [];} }
async function sendDm(peer,text){ return signedCall('POST','/api/dm/send',JSON.stringify({peer,text})); }
// names are public on the member rail; only thread bodies are private.
async function principalForName(name){ try{ const r=await fetch('/api/identity/lookup-name?n='+encodeURIComponent(name)); if(r.ok){ const j=await r.json(); if(j.bound) return j.principal; } }catch(_){} return null; }
async function renderDmSurface(){
  if (location.pathname !== '/chat' || qparam('dm') !== '1') return;
  const gate=document.querySelector('[data-dm-gate]'), gateC=document.querySelector('[data-dm-gate-center]');
  const threadsEl=document.querySelector('[data-dm-threads]'), emptyPick=document.querySelector('[data-dm-empty-pick]');
  const stream=document.querySelector('[data-dm-stream]'), composer=document.querySelector('[data-dm-composer]');
  if (!signedIn()){ if(gate)gate.hidden=false; if(gateC)gateC.style.display=''; if(threadsEl)threadsEl.hidden=true; if(stream)stream.hidden=true; if(composer)composer.hidden=true; if(emptyPick)emptyPick.hidden=true; return; }
  await ensureAgent();
  if(gate)gate.hidden=true; if(gateC)gateC.style.display='none'; if(threadsEl)threadsEl.hidden=false;
  const threads=await loadThreads();
  if(threadsEl){ const e='<div class=""dc-dm-threads-empty"">No conversations yet. Open one from a member’s Message button.</div>';
    threadsEl.innerHTML = threads.length ? threads.map(t=>'<a class=""dc-dm-thread"" href=""/chat?dm=1&t='+encodeURIComponent(t.peer)+'""><span class=""dc-dm-thread-avatar"" style=""background:hsl('+hue(t.name||t.peer)+',50%,50%)"">'+esc(((t.name||'?')[0]||'?').toUpperCase())+'</span><span class=""dc-dm-thread-name"">'+esc(t.name||t.peer)+'</span></a>').join('') : e; }
  const peer=qparam('t');
  if(!peer){ if(emptyPick)emptyPick.hidden=false; if(stream)stream.hidden=true; if(composer)composer.hidden=true; return; }
  if(emptyPick)emptyPick.hidden=true;
  if(stream){ stream.hidden=false; const msgs=await loadMessages(peer); stream.innerHTML=msgs.map(msgRow).join(''); stream.scrollTop=stream.scrollHeight; }
  if(composer){ composer.hidden=false; const inp=composer.querySelector('[data-dm-peer-input]'); if(inp)inp.value=peer; }
  const nameEl=document.querySelector('[data-dm-peer-name]'); const t=threads.find(x=>x.peer===peer);
  if(nameEl)nameEl.textContent=(t&&t.name)?t.name:peer;
}
document.addEventListener('click', async (e)=>{
  const dmBtn = e.target.closest && e.target.closest('[data-wasp-dm-user]');
  if (dmBtn){ e.preventDefault();
    if(!signedIn()){ document.querySelector('[data-ii-signin]')?.click(); return; }
    const name=dmBtn.getAttribute('data-wasp-dm-user'); dmBtn.disabled=true;
    const p=await principalForName(name); dmBtn.disabled=false;
    if(!p){ dmBtn.title=name+' has not signed in with II — can’t DM'; return; }
    spaNav('/chat?dm=1&t='+encodeURIComponent(p)); return; }
  const send = e.target.closest && e.target.closest('[data-dm-send]');
  if (send){ e.preventDefault();
    const composer=send.closest('[data-dm-composer]'); const ta=composer.querySelector('[data-dm-input]');
    const peer=composer.querySelector('[data-dm-peer-input]').value; const text=(ta.value||'').trim();
    if(!peer||!text) return; send.disabled=true; await ensureAgent();
    const r=await sendDm(peer,text); send.disabled=false;
    if(r.status===200){ ta.value=''; await renderDmSurface(); } return; }
});
const root=document.getElementById('wasp-root');
if(root){ let pend=null; new MutationObserver(()=>{ if(pend)return; pend=requestAnimationFrame(()=>{pend=null; renderDmSurface();}); }).observe(root,{childList:true,subtree:true}); }
renderDmSurface();
// Deep-link / full-load recovery: a fresh load of /chat?dm=1 is served as
// the query-stripped channel snapshot (no DM DOM). Re-render via SPA (which
// honours the query) so bookmarks/shared links and the back button land on
// the DM surface rather than #general.
if (location.pathname==='/chat' && qparam('dm')==='1' && !document.querySelector('[data-dm-pane]')) {
  spaNav(location.pathname + location.search);
}
";

    // M2 signed server-actions client. Mirrors DmClientScript's signed
    // @dfinity/agent http_request_update transport, but ENV-AWARE: on a
    // *.localhost / 127.0.0.1 replica it points the agent at the page origin
    // and fetchRootKey()s (mainnet uses the hardcoded icp0.io host + the
    // baked-in root key). Issues the role-gated create/grant calls so the
    // canister sees a real msg_caller, and gates the create-server/-channel
    // UI on /api/server/myrole.
    private const string ServerAdminScript = @"
import { Actor, HttpAgent } from '/_wasp/dfinity.js';
import { AuthClient } from '/_wasp/dfinity.js';
import { IDL } from '/_wasp/dfinity.js';
const canisterId = document.body.getAttribute('data-wasp-canister');
const httpReq = IDL.Record({ method: IDL.Text, url: IDL.Text, headers: IDL.Vec(IDL.Tuple(IDL.Text, IDL.Text)), body: IDL.Vec(IDL.Nat8) });
const httpResp = IDL.Record({ status_code: IDL.Nat16, headers: IDL.Vec(IDL.Tuple(IDL.Text, IDL.Text)), body: IDL.Vec(IDL.Nat8), upgrade: IDL.Opt(IDL.Bool) });
const idlFactory = ({ IDL }) => IDL.Service({ http_request_update: IDL.Func([httpReq], [httpResp], []) });
const isLocal = /(^|\.)localhost$/.test(location.hostname) || location.hostname === '127.0.0.1';
const HOST = isLocal ? location.origin : 'https://icp0.io';
let authClient, agent, actor;
async function ensureAgent() {
  authClient = authClient || await AuthClient.create();
  const identity = authClient.getIdentity();
  agent = new HttpAgent({ identity, host: HOST });
  if (isLocal) { try { await agent.fetchRootKey(); } catch (_) {} }
  actor = Actor.createActor(idlFactory, { agent, canisterId });
}
async function signedCall(method, url, jsonBody) {
  const body = jsonBody ? Array.from(new TextEncoder().encode(jsonBody)) : [];
  const resp = await actor.http_request_update({ method, url, headers: jsonBody ? [['content-type','application/json']] : [], body });
  return { status: Number(resp.status_code), text: new TextDecoder().decode(new Uint8Array(resp.body)) };
}
function signedIn() { return !!localStorage.getItem('wasp:ii:principal'); }
function qp(k) { return new URLSearchParams(location.search).get(k); }
function activeServerId() { const n = parseInt(qp('s') || '0', 10); return isNaN(n) ? 0 : n; }
// Signed sign-up: bind the caller's OWN principal to a display name. Exposed for
// the II sign-in flow (IiClientScript) which holds the same II session.
window.waspBindName = async function (name) {
  try { await ensureAgent(); const r = await signedCall('POST', '/api/account/bind', JSON.stringify({ name: name }));
    if (r.status === 200) { try { return JSON.parse(r.text).name || name; } catch (_) {} } }
  catch (_) {}
  return name;
};
function spaNav(p) { const a = document.createElement('a'); a.href = p; document.body.appendChild(a); a.click(); a.remove(); }
function errText(r) { try { return JSON.parse(r.text).error || ('error ' + r.status); } catch { return 'error ' + r.status; } }
function toast(msg) {
  let t = document.getElementById('wasp-toast');
  if (!t) { t = document.createElement('div'); t.id = 'wasp-toast';
    t.style.cssText = 'position:fixed;bottom:20px;left:50%;transform:translateX(-50%);background:#1e1f22;color:#f2f3f5;border:1px solid rgba(255,255,255,0.12);padding:10px 16px;border-radius:8px;font-size:0.85rem;z-index:9999;box-shadow:0 6px 24px rgba(0,0,0,0.45);opacity:0;transition:opacity 0.18s ease;pointer-events:none;max-width:80vw';
    document.body.appendChild(t); }
  t.textContent = msg; t.style.opacity = '1';
  clearTimeout(t._h); t._h = setTimeout(() => { t.style.opacity = '0'; }, 3200);
}
let lastSid = -999, lastInfo = null;
let lastPrivRoom = -1, lastPrivAt = 0, lastPrivThread = -1, lastPrivThreadAt = 0, lastSettingsSid = -1;
let lastPFSid = -1, lastPFTid = -1, lastPFAt = 0, lastPFeedSid = -1, lastPFeedAt = 0;
let lastFeedHomeAt = 0, feedTabChoice = null, feedHomeLimit = 60;
let createFormOpen = false;
function applyVisibility(info, sid) {
  // Spaces sidebar has one 'New' button per group — reveal them ALL for super.
  const showCreate = !!(info && info.isSuperAdmin);
  document.querySelectorAll('[data-create-server-ui]').forEach(el => { el.hidden = !showCreate; });
  // Re-assert the create-form open state: the reactivity poll re-emits the form
  // hidden from SSR every swap, so without this it would snap shut mid-typing.
  const cf = document.querySelector('[data-create-form]');
  if (cf) cf.hidden = !(showCreate && createFormOpen);
  const ch = document.querySelector('[data-create-channel-ui]');
  const canCh = info && (info.isSuperAdmin || info.role === 'admin' || info.role === 'owner');
  if (ch) ch.hidden = !(canCh && sid >= 1);
  // Moderator+ (or super) reveals per-message + channel mod controls. The server
  // re-checks per-room on every mod call, so this is a convenience reveal only.
  const canMod = info && (info.isSuperAdmin || info.role === 'moderator' || info.role === 'admin' || info.role === 'owner');
  document.querySelectorAll('[data-mod-controls]').forEach(el => { el.hidden = !canMod; });
  // B4: owner/admin (or super) reveals the server-settings gear + membership UI.
  const canMembers = !!(info && info.canManageMembers);
  document.querySelectorAll('[data-manage-ui]').forEach(el => { el.hidden = !(canMembers && sid >= 1); });
}
async function refreshRoleUi() {
  // The spaces sidebar (with the super-only New buttons) shows on every app
  // route, so the role check must run on chat/forum/feed (not just /chat).
  const _p = location.pathname;
  if (_p !== '/chat' && _p !== '/forum' && _p !== '/feed') return;
  if (!signedIn()) { applyVisibility(null, 0); lastSid = -999; lastInfo = null; return; }
  const sid = activeServerId();
  if (sid === lastSid && lastInfo) { applyVisibility(lastInfo, sid); return; }
  applyVisibility(null, sid);
  try {
    await ensureAgent();
    const r = await signedCall('POST', '/api/server/myrole', JSON.stringify({ serverId: sid }));
    if (r.status !== 200) return;
    const info = JSON.parse(r.text);
    lastSid = sid; lastInfo = info; applyVisibility(info, sid);
  } catch (_) {}
}
async function createServer() {
  if (!signedIn()) { const b = document.querySelector('[data-ii-signin]'); if (b) b.click(); return; }
  const inp = document.querySelector('[name=newServer]'); const name = ((inp && inp.value) || '').trim();
  if (!name) return;
  const pc = document.querySelector('[name=newServerPrivate]'); const priv = !!(pc && pc.checked);
  const kc = document.querySelector('[name=newServerKind]'); const kind = (kc && kc.value) || 'discussion';
  try {
    await ensureAgent();
    const r = await signedCall('POST', '/api/servers', JSON.stringify({ name, private: String(priv), kind: kind }));
    if (r.status === 200) {
      if (inp) inp.value = ''; let j = {}; try { j = JSON.parse(r.text); } catch (_) {} lastSid = -999; createFormOpen = false;
      const id = j.id || ''; const k = j.kind || kind;
      if (k === 'forum') spaNav('/forum?s=' + id);
      else if (k === 'feed') spaNav('/feed?s=' + id);
      else spaNav('/chat?s=' + id);
    }
    else toast(errText(r));
  } catch (_) { toast('network error'); }
}
async function createChannel() {
  if (!signedIn()) { const b = document.querySelector('[data-ii-signin]'); if (b) b.click(); return; }
  const inp = document.querySelector('[name=newRoom]'); const name = ((inp && inp.value) || '').trim();
  if (!name) return;
  const sid = activeServerId();
  try {
    await ensureAgent();
    const r = await signedCall('POST', '/api/server-channels', JSON.stringify({ serverId: sid, name }));
    if (r.status === 200) { if (inp) inp.value = ''; let j = {}; try { j = JSON.parse(r.text); } catch (_) {} spaNav('/chat?s=' + sid + '&r=' + encodeURIComponent(j.channelName || name)); }
    else toast(errText(r));
  } catch (_) { toast('network error'); }
}
document.addEventListener('click', (e) => {
  const nsp = e.target.closest && e.target.closest('[data-new-space]');
  if (nsp) {
    e.preventDefault(); e.stopPropagation();
    const kc = document.querySelector('[name=newServerKind]'); if (kc) kc.value = nsp.getAttribute('data-new-space') || 'discussion';
    createFormOpen = true;
    const form = document.querySelector('[data-create-form]');
    if (form) { form.hidden = false; const inp = form.querySelector('[name=newServer]'); if (inp) inp.focus(); }
    return;
  }
  if (e.target.closest && e.target.closest('[data-create-cancel]')) { e.preventDefault(); e.stopPropagation(); createFormOpen = false; const f = document.querySelector('[data-create-form]'); if (f) f.hidden = true; return; }
  if (e.target.closest && e.target.closest('[data-create-server]')) { e.preventDefault(); createServer(); return; }
  if (e.target.closest && e.target.closest('[data-create-channel]')) { e.preventDefault(); createChannel(); return; }
});
document.addEventListener('keydown', (e) => {
  if (e.key !== 'Enter') return;
  const t = e.target;
  if (t && t.name === 'newServer') { e.preventDefault(); createServer(); }
  else if (t && t.name === 'newRoom') { e.preventDefault(); createChannel(); }
});

// ── M2-B signed chat writes: post / edit / delete (sign-in required) ──
function mainComposer() { const b = document.querySelector('[data-chat-send]'); return b ? b.closest('form') : null; }
function fieldVal(form, n) { const el = form.querySelector('[name=' + n + ']'); return el ? (el.value || '') : ''; }
async function chatSend() {
  if (!signedIn()) { const s = document.querySelector('[data-ii-signin]'); if (s) s.click(); return; }
  const form = mainComposer(); if (!form) return;
  const ta = form.querySelector('textarea[name=text]');
  const text = ((ta && ta.value) || '').trim();
  const editId = fieldVal(form, 'editMsgId');
  try {
    await ensureAgent();
    let r;
    if (editId && editId !== '0') {
      if (!text) return;
      r = await signedCall('POST', '/api/chat/edit', JSON.stringify({ msgId: Number(editId), text }));
    } else {
      const imageData = fieldVal(form, 'imageData');
      if (!text && !imageData) return;
      const payload = { roomId: Number(fieldVal(form, 'roomId') || '0'), text, username: (document.querySelector('[name=username]') || {}).value || '' };
      const replyTo = fieldVal(form, 'replyTo');
      if (replyTo && replyTo !== '0') payload.replyTo = Number(replyTo);
      if (imageData) payload.imageData = imageData;
      r = await signedCall('POST', '/api/chat/post', JSON.stringify(payload));
    }
    if (r.status === 200) {
      if (ta) { ta.value = ''; ta.dispatchEvent(new Event('input', { bubbles: true })); }
      ['imageData', 'replyTo', 'editMsgId'].forEach(n => { const el = form.querySelector('[name=' + n + ']'); if (el) el.value = ''; });
      ['[data-wasp-cancel-reply]', '[data-wasp-cancel-edit]', '[data-wasp-image-clear]'].forEach(sel => { const b = document.querySelector(sel); if (b && b.offsetParent !== null) b.click(); });
    } else { toast(errText(r)); }
  } catch (_) { toast('network error'); }
}
async function chatDelete(id) {
  if (!signedIn()) { const s = document.querySelector('[data-ii-signin]'); if (s) s.click(); return; }
  try {
    await ensureAgent();
    const r = await signedCall('POST', '/api/chat/delete', JSON.stringify({ msgId: Number(id) }));
    if (r.status !== 200) toast(errText(r));
  } catch (_) { toast('network error'); }
}
async function chatReact(id, emoji) {
  if (!signedIn()) { const s = document.querySelector('[data-ii-signin]'); if (s) s.click(); return; }
  try { await ensureAgent(); const r = await signedCall('POST', '/api/chat/react', JSON.stringify({ msgId: Number(id), emoji: emoji })); if (r.status !== 200) toast(errText(r)); }
  catch (_) { toast('network error'); }
}
async function threadSend(btn) {
  if (!signedIn()) { const s = document.querySelector('[data-ii-signin]'); if (s) s.click(); return; }
  const form = btn.closest('form'); if (!form) return;
  const pe = form.querySelector('[name=threadParent]'); const ta = form.querySelector('textarea[name=text]');
  const parent = pe ? Number(pe.value || '0') : 0; const text = ((ta && ta.value) || '').trim();
  if (!parent || !text) return;
  try {
    await ensureAgent();
    const r = await signedCall('POST', '/api/thread/reply', JSON.stringify({ parentMsgId: parent, text: text }));
    if (r.status === 200) { if (ta) { ta.value = ''; ta.dispatchEvent(new Event('input', { bubbles: true })); } } else toast(errText(r));
  } catch (_) { toast('network error'); }
}
async function modCall(url, payload) {
  if (!signedIn()) { const s = document.querySelector('[data-ii-signin]'); if (s) s.click(); return; }
  try { await ensureAgent(); const r = await signedCall('POST', url, JSON.stringify(payload)); if (r.status !== 200) toast(errText(r)); }
  catch (_) { toast('network error'); }
}
async function deleteServer(sid) {
  if (!signedIn()) { const s = document.querySelector('[data-ii-signin]'); if (s) s.click(); return; }
  try {
    await ensureAgent();
    const r = await signedCall('POST', '/api/server/delete', JSON.stringify({ serverId: Number(sid) }));
    if (r.status !== 200) { toast(errText(r)); return; }
    toast('Server deleted'); lastSid = -999; lastSettingsSid = -1; location.href = '/chat';
  } catch (_) { toast('network error'); }
}
function modDelete(id) { return modCall('/api/chat/mod/delete', { msgId: Number(id) }); }
function modPin(btn) { const cur = btn.getAttribute('data-pinned') === 'true'; return modCall('/api/chat/mod/pin', { msgId: Number(btn.getAttribute('data-mod-pin')), pinned: String(!cur) }); }
function modLock(btn) { const cur = btn.getAttribute('data-locked') === 'true'; return modCall('/api/chat/mod/lock', { roomId: Number(btn.getAttribute('data-mod-lock')), locked: String(!cur) }); }
function modMute(btn) { return modCall('/api/chat/mod/mute', { serverId: Number(btn.getAttribute('data-mute-server') || activeServerId()), principal: btn.getAttribute('data-mod-mute'), durationMs: 3600000 }); }

// ── F1 forum: new topic / reply / mark-accepted-answer (all signed) ──
async function forumNewTopic(serverId, title, text) {
  if (!signedIn()) { const s = document.querySelector('[data-ii-signin]'); if (s) s.click(); return; }
  if (!title || !text) { toast('title and body required'); return; }
  try {
    await ensureAgent();
    const uname = (document.querySelector('[name=username]') || {}).value || '';
    const r = await signedCall('POST', '/api/forum/topic', JSON.stringify({ serverId: Number(serverId), title: title, text: text, username: uname }));
    if (r.status === 200) { let j = {}; try { j = JSON.parse(r.text); } catch (_) {} spaNav('/forum?s=' + Number(serverId) + '&t=' + (j.topicId || '')); }
    else toast(errText(r));
  } catch (_) { toast('network error'); }
}
async function forumReply(roomId, text) {
  if (!signedIn()) { const s = document.querySelector('[data-ii-signin]'); if (s) s.click(); return; }
  if (!text) return;
  try {
    await ensureAgent();
    const uname = (document.querySelector('[name=username]') || {}).value || '';
    const r = await signedCall('POST', '/api/chat/post', JSON.stringify({ roomId: Number(roomId), text: text, username: uname }));
    if (r.status === 200) spaNav(location.pathname + location.search);
    else toast(errText(r));
  } catch (_) { toast('network error'); }
}
async function forumSolve(roomId, msgId, on) {
  if (!signedIn()) { const s = document.querySelector('[data-ii-signin]'); if (s) s.click(); return; }
  try {
    await ensureAgent();
    const r = await signedCall('POST', '/api/forum/solve', JSON.stringify({ roomId: Number(roomId), msgId: on ? Number(msgId) : 0 }));
    if (r.status === 200) spaNav(location.pathname + location.search);
    else toast(errText(r));
  } catch (_) { toast('network error'); }
}
async function voteForum(btn) {
  if (!signedIn()) { const s = document.querySelector('[data-ii-signin]'); if (s) s.click(); return; }
  try {
    await ensureAgent();
    const r = await signedCall('POST', '/api/forum/vote', JSON.stringify({ msgId: Number(btn.getAttribute('data-msg')), dir: btn.getAttribute('data-dir') }));
    if (r.status === 200) spaNav(location.pathname + location.search); else toast(errText(r));
  } catch (_) { toast('network error'); }
}
document.addEventListener('click', (e) => {
  if (!e.target.closest) return;
  const fv = e.target.closest('[data-forum-vote]');
  if (fv) { e.preventDefault(); voteForum(fv); return; }
  const nt = e.target.closest('[data-forum-new]');
  if (nt) { e.preventDefault(); const f = document.querySelector('[data-forum-newform]'); if (f) { f.hidden = false; const ti = f.querySelector('[data-forum-new-title]'); if (ti) ti.focus(); } return; }
  const nc = e.target.closest('[data-forum-new-cancel]');
  if (nc) { e.preventDefault(); const f = document.querySelector('[data-forum-newform]'); if (f) f.hidden = true; return; }
  const ns = e.target.closest('[data-forum-new-submit]');
  if (ns) { e.preventDefault(); const f = document.querySelector('[data-forum-newform]'); const ti = f && f.querySelector('[data-forum-new-title]'); const bo = f && f.querySelector('[data-forum-new-body]'); forumNewTopic(ns.getAttribute('data-server'), ((ti && ti.value) || '').trim(), ((bo && bo.value) || '').trim()); return; }
  const rb = e.target.closest('[data-forum-reply]');
  if (rb) { e.preventDefault(); const room = rb.getAttribute('data-room'); const ta = document.querySelector('[data-forum-reply-text=""' + room + '""]'); forumReply(room, ((ta && ta.value) || '').trim()); return; }
  const sv = e.target.closest('[data-forum-solve]');
  if (sv) { e.preventDefault(); forumSolve(sv.getAttribute('data-room'), sv.getAttribute('data-msg'), sv.getAttribute('data-on') === '1'); return; }
});

// ── F2 feed: post / reply / like / repost / follow (all signed) ──
async function feedPost(roomId, text) {
  if (!signedIn()) { const s = document.querySelector('[data-ii-signin]'); if (s) s.click(); return; }
  if (!text) return;
  try {
    await ensureAgent();
    const uname = (document.querySelector('[name=username]') || {}).value || '';
    const r = await signedCall('POST', '/api/chat/post', JSON.stringify({ roomId: Number(roomId), text: text, username: uname }));
    if (r.status === 200) spaNav(location.pathname + location.search); else toast(errText(r));
  } catch (_) { toast('network error'); }
}
async function feedReply(roomId, replyTo, text) {
  if (!signedIn()) { const s = document.querySelector('[data-ii-signin]'); if (s) s.click(); return; }
  if (!text) return;
  try {
    await ensureAgent();
    const uname = (document.querySelector('[name=username]') || {}).value || '';
    const r = await signedCall('POST', '/api/chat/post', JSON.stringify({ roomId: Number(roomId), replyTo: Number(replyTo), text: text, username: uname }));
    if (r.status === 200) spaNav(location.pathname + location.search); else toast(errText(r));
  } catch (_) { toast('network error'); }
}
async function feedRepost(btn) {
  if (!signedIn()) { const s = document.querySelector('[data-ii-signin]'); if (s) s.click(); return; }
  const on = btn.getAttribute('data-on') === '1';
  try {
    await ensureAgent();
    const r = await signedCall('POST', '/api/feed/repost', JSON.stringify({ msgId: Number(btn.getAttribute('data-msg')), on: String(on) }));
    if (r.status === 200) spaNav(location.pathname + location.search); else toast(errText(r));
  } catch (_) { toast('network error'); }
}
async function feedFollow(btn) {
  if (!signedIn()) { const s = document.querySelector('[data-ii-signin]'); if (s) s.click(); return; }
  const on = btn.getAttribute('data-on') === '1';
  try {
    await ensureAgent();
    const r = await signedCall('POST', '/api/feed/follow', JSON.stringify({ serverId: Number(btn.getAttribute('data-server')), on: String(on) }));
    if (r.status === 200) spaNav(location.pathname + location.search); else toast(errText(r));
  } catch (_) { toast('network error'); }
}
// Personalized FOLLOWING home — signed read, client-rendered (anonymous SSR can't
// know the viewer), mirroring renderPrivateChannel. privEsc/privHue are hoisted fns.
function feedPostRow(p) {
  const a = p.author || 'anon'; const h = privHue(a); const init = ((a[0] || '?')).toUpperCase();
  const src = p.srcName ? ' <a class=""fd-srcinline"" href=""/feed?s=' + p.srcId + '"">' + privEsc(p.srcName) + '</a>' : '';
  const rb = p.repostedBy ? '<div class=""fd-repost-by"">🔁 Reposted by ' + privEsc(p.repostedBy) + '…</div>' : '';
  const fbtn = p.authorPrincipal ? '<button type=""button"" class=""fd-followbtn' + (p.following ? ' on' : '') + '"" data-user-follow data-principal=""' + privEsc(p.authorPrincipal) + '"" data-on=""' + (p.following ? '0' : '1') + '"">' + (p.following ? 'Following' : 'Follow') + '</button>' : '';
  return rb + '<div class=""fd-post"">' +
    '<span class=""fd-av"" style=""background:hsl(' + h + ',55%,45%)"">' + privEsc(init) + '</span>' +
    '<div class=""fd-post-main"">' +
    '<div class=""fd-post-head""><span class=""fd-author"">' + privEsc(a) + '</span>' + src + fbtn + '</div>' +
    '<div class=""fd-body"">' + (p.html || '') + '</div>' +
    '<div class=""fd-actions"">' +
    '<a class=""fd-act"" href=""/feed?s=' + p.srcId + '&p=' + p.id + '"">💬 ' + (p.replies || 0) + '</a>' +
    '<button type=""button"" class=""fd-act"" data-feed-repost data-msg=""' + p.id + '"" data-on=""' + (p.reposted ? '0' : '1') + '"">🔁 ' + (p.reposts || 0) + '</button>' +
    '<button type=""button"" class=""fd-act"" data-feed-like data-msg=""' + p.id + '"">❤ ' + (p.likes || 0) + '</button>' +
    '</div></div></div>';
}
async function userFollow(btn) {
  if (!signedIn()) { const s = document.querySelector('[data-ii-signin]'); if (s) s.click(); return; }
  const who = btn.getAttribute('data-principal'); if (!who) return;
  const on = btn.getAttribute('data-on') === '1';
  try {
    await ensureAgent();
    const r = await signedCall('POST', '/api/user/follow', JSON.stringify({ principal: who, on: String(on) }));
    if (r.status === 200) spaNav(location.pathname + location.search); else toast(errText(r));
  } catch (_) { toast('network error'); }
}
async function renderFeedHome() {
  const wrap = document.querySelector('[data-feed-following-stream]'); if (!wrap) return;
  if (!signedIn()) { wrap.innerHTML = '<div class=""fd-empty""><p>Sign in to see posts from feeds you follow.</p></div>'; return; }
  // Throttle re-fetches, but always fetch when the stream is empty (a reactivity-poll
  // DOM swap re-renders the empty SSR container, which we must repopulate immediately).
  const t = Date.now(); if (wrap.innerHTML !== '' && t - lastFeedHomeAt < 2500) return; lastFeedHomeAt = t;
  try {
    await ensureAgent();
    const r = await signedCall('POST', '/api/feed/home', JSON.stringify({ limit: feedHomeLimit }));
    if (r.status !== 200) { wrap.innerHTML = '<div class=""fd-empty""><p>Could not load your feed.</p></div>'; return; }
    let j = {}; try { j = JSON.parse(r.text); } catch (_) {}
    const posts = j.posts || [];
    if (!posts.length) { wrap.innerHTML = '<div class=""fd-empty""><p>You do not follow any feeds yet — open a feed and tap Follow, or follow people.</p></div>'; return; }
    let html = posts.map(feedPostRow).join('');
    if (posts.length >= feedHomeLimit) html += '<a class=""fd-loadmore"" data-feed-home-more>Load more</a>';
    wrap.innerHTML = html;
  } catch (_) { wrap.innerHTML = '<div class=""fd-empty""><p>Network error.</p></div>'; }
}
function applyFeedTab(which) {
  const fo = document.querySelector('[data-feed-following]'); const al = document.querySelector('[data-feed-all]');
  if (!fo || !al) return;
  const on = which === 'following';
  fo.hidden = !on; al.hidden = on;
  const tabs = document.querySelectorAll('[data-feed-tab]');
  for (let i = 0; i < tabs.length; i++) tabs[i].classList.toggle('on', tabs[i].getAttribute('data-feed-tab') === which);
  if (on) renderFeedHome();
}
function feedTab(which) { feedTabChoice = which; applyFeedTab(which); }   // user click — sticky choice
function feedHomeTick() {
  const fo = document.querySelector('[data-feed-following]');
  if (!fo) { feedTabChoice = null; return; }               // left /feed home — reset
  if (feedTabChoice === null) feedTabChoice = signedIn() ? 'following' : 'all';   // default
  applyFeedTab(feedTabChoice);   // re-assert after every reactivity-poll DOM swap (SSR resets to All feeds)
}
document.addEventListener('click', (e) => {
  if (!e.target.closest) return;
  const ftab = e.target.closest('[data-feed-tab]');
  if (ftab) { e.preventDefault(); feedTab(ftab.getAttribute('data-feed-tab')); return; }
  const fhm = e.target.closest('[data-feed-home-more]');
  if (fhm) { e.preventDefault(); feedHomeLimit += 60; lastFeedHomeAt = 0; renderFeedHome(); return; }
  const fp = e.target.closest('[data-feed-post]');
  if (fp) { e.preventDefault(); const room = fp.getAttribute('data-room'); const ta = document.querySelector('[data-feed-post-text=""' + room + '""]'); feedPost(room, ((ta && ta.value) || '').trim()); return; }
  const fr = e.target.closest('[data-feed-reply]');
  if (fr) { e.preventDefault(); const room = fr.getAttribute('data-room'); const rt = fr.getAttribute('data-replyto'); const ta = document.querySelector('[data-feed-reply-text=""' + rt + '""]'); feedReply(room, rt, ((ta && ta.value) || '').trim()); return; }
  const fl = e.target.closest('[data-feed-like]');
  if (fl) { e.preventDefault(); chatReact(fl.getAttribute('data-msg'), '❤️'); return; }
  const frp = e.target.closest('[data-feed-repost]');
  if (frp) { e.preventDefault(); feedRepost(frp); return; }
  const ff = e.target.closest('[data-feed-follow]');
  if (ff) { e.preventDefault(); feedFollow(ff); return; }
  const ufb = e.target.closest('[data-user-follow]');
  if (ufb) { e.preventDefault(); userFollow(ufb); return; }
});

// ── B4: private-channel signed READ + membership management ──
function privEsc(s) { const d = document.createElement('div'); d.textContent = s || ''; return d.innerHTML; }
function privHue(n) { let h = 0; for (const c of (n || '?')) h = (h * 131 + c.charCodeAt(0)) & 0xFFFFFF; return h % 360; }
function privMsgRow(m) {
  if (m.deleted) return '<div class=""dc-message dc-message-deleted""><div class=""dc-message-body""><div class=""dc-text""><em>message deleted</em></div></div></div>';
  const h = privHue(m.author);
  return '<div class=""dc-message""><div class=""dc-avatar"" style=""background:hsl(' + h + ',50%,50%)"">' + privEsc(((m.author || '?')[0] || '?').toUpperCase()) + '</div>' +
    '<div class=""dc-message-body""><div class=""dc-message-head""><span class=""dc-username"" style=""color:hsl(' + h + ',60%,65%)"">' + privEsc(m.author) + '</span></div>' +
    '<div class=""dc-text"">' + privEsc(m.text) + (m.edited ? ' <span class=""dc-edited"">(edited)</span>' : '') + '</div></div></div>';
}
async function renderPrivateChannel() {
  const pane = document.querySelector('[data-private-pane]'); if (!pane) return;
  const stream = document.querySelector('[data-private-stream]'); const gateMsg = document.querySelector('[data-private-gate-msg]');
  const roomId = Number(pane.getAttribute('data-room-id') || '0');
  if (!signedIn()) { if (gateMsg) gateMsg.textContent = 'Members only — sign in with Internet Identity to view.'; return; }
  // Throttle: the MutationObserver fires on every DOM swap; re-fetch at most every
  // ~2.5s per room so we get near-live updates without spamming signed calls.
  const t = Date.now(); if (roomId === lastPrivRoom && t - lastPrivAt < 2500) return; lastPrivRoom = roomId; lastPrivAt = t;
  try {
    await ensureAgent();
    const r = await signedCall('POST', '/api/chat/private-read', JSON.stringify({ roomId: roomId }));
    if (r.status === 200) {
      let j = {}; try { j = JSON.parse(r.text); } catch (_) {} const msgs = j.messages || [];
      if (stream) { stream.innerHTML = msgs.map(privMsgRow).join(''); stream.scrollTop = stream.scrollHeight; }
      if (gateMsg) gateMsg.textContent = msgs.length ? '' : 'No messages yet — you can post below.';
    } else if (r.status === 403) { if (gateMsg) gateMsg.textContent = 'You are not a member of this private server.'; }
    else if (gateMsg) { gateMsg.textContent = 'Could not load this channel.'; }
  } catch (_) { if (gateMsg) gateMsg.textContent = 'Network error.'; }
}
async function renderPrivateThread() {
  const pane = document.querySelector('[data-private-thread-pane]'); if (!pane) return;
  const stream = document.querySelector('[data-private-thread-stream]'); const msg = document.querySelector('[data-private-thread-msg]');
  const parent = Number(pane.getAttribute('data-thread-parent') || '0');
  if (!signedIn()) { if (msg) msg.textContent = 'Members only — sign in with Internet Identity to view.'; return; }
  const t = Date.now(); if (parent === lastPrivThread && t - lastPrivThreadAt < 2500) return; lastPrivThread = parent; lastPrivThreadAt = t;
  try {
    await ensureAgent();
    const r = await signedCall('POST', '/api/thread/read', JSON.stringify({ parentMsgId: parent }));
    if (r.status === 200) {
      let j = {}; try { j = JSON.parse(r.text); } catch (_) {}
      const reps = j.replies || [];
      let html = j.parent ? privMsgRow(j.parent) : '';
      html += '<div class=""dc-thread-divider""><span>' + reps.length + (reps.length === 1 ? ' reply' : ' replies') + '</span></div>';
      html += reps.map(privMsgRow).join('');
      if (stream) stream.innerHTML = html;
      if (msg) msg.textContent = '';
    } else if (r.status === 403) { if (msg) msg.textContent = 'You are not a member of this private server.'; }
    else if (msg) { msg.textContent = 'Could not load this thread.'; }
  } catch (_) { if (msg) msg.textContent = 'Network error.'; }
}
async function setVisibility(sid, makePrivate) { return modCall('/api/server/visibility', { serverId: Number(sid), private: String(!!makePrivate) }); }
async function addMember(sid, principal) { if (!principal) return; return modCall('/api/server/member/add', { serverId: Number(sid), principal: principal }); }
async function removeMember(sid, principal) { return modCall('/api/server/member/remove', { serverId: Number(sid), principal: principal }); }
async function renderSettings() {
  const panel = document.querySelector('[data-server-settings]'); if (!panel) return;
  const sid = activeServerId(); if (sid < 1 || !signedIn()) return;
  // Fetch the roster once per server (observer fires often); the mutate handlers
  // reset lastSettingsSid to force a refresh after add/remove/visibility.
  if (sid === lastSettingsSid) return; lastSettingsSid = sid;
  try {
    await ensureAgent();
    const r = await signedCall('POST', '/api/server/members', JSON.stringify({ serverId: sid }));
    if (r.status !== 200) return; let j = {}; try { j = JSON.parse(r.text); } catch (_) { return; }
    const vis = document.querySelector('[data-visibility-state]'); if (vis) vis.textContent = j.private ? 'Private' : 'Public';
    const tog = document.querySelector('[data-visibility-toggle]'); if (tog) { tog.setAttribute('data-private', j.private ? 'true' : 'false'); tog.textContent = j.private ? 'Make public' : 'Make private'; }
    const list = document.querySelector('[data-member-list]');
    if (list) {
      const ms = j.members || [];
      list.innerHTML = ms.length
        ? ms.map(function (m) { return '<li class=""dc-member-row""><span class=""dc-member-name"">' + privEsc(m.name || m.principal) + '</span><button type=""button"" class=""dc-member-remove"" data-remove-member=""' + privEsc(m.principal) + '"">remove</button></li>'; }).join('')
        : '<li class=""dc-member-empty"">No explicit members yet.</li>';
    }
  } catch (_) {}
}
document.addEventListener('click', (e) => {
  const vt = e.target.closest && e.target.closest('[data-visibility-toggle]');
  if (vt) { e.preventDefault(); e.stopPropagation(); setVisibility(activeServerId(), vt.getAttribute('data-private') !== 'true').then(() => { lastSid = -999; lastSettingsSid = -1; lastPrivRoom = -1; refreshRoleUi(); renderSettings(); }); return; }
  const dsv = e.target.closest && e.target.closest('[data-delete-server]');
  if (dsv) {
    e.preventDefault(); e.stopPropagation();
    // Two-click arm (no native confirm dialog): first click arms for 4s, second deletes.
    if (dsv.getAttribute('data-armed') !== '1') {
      dsv.setAttribute('data-armed', '1'); dsv.classList.add('armed');
      dsv.textContent = 'Click again to delete — erases all channels';
      setTimeout(function () { dsv.setAttribute('data-armed', '0'); dsv.classList.remove('armed'); dsv.textContent = 'Delete this server'; }, 4000);
      return;
    }
    deleteServer(activeServerId()); return;
  }
  const amb = e.target.closest && e.target.closest('[data-add-member-btn]');
  if (amb) { e.preventDefault(); e.stopPropagation(); const inp = document.querySelector('[name=memberPrincipal]'); const p = inp ? (inp.value || '').trim() : ''; if (p) addMember(activeServerId(), p).then(() => { if (inp) inp.value = ''; lastSettingsSid = -1; renderSettings(); }); return; }
  const rmb = e.target.closest && e.target.closest('[data-remove-member]');
  if (rmb) { e.preventDefault(); e.stopPropagation(); removeMember(activeServerId(), rmb.getAttribute('data-remove-member')).then(() => { lastSettingsSid = -1; renderSettings(); }); return; }
  const mkm = e.target.closest && e.target.closest('[data-make-member]');
  if (mkm) { e.preventDefault(); e.stopPropagation(); addMember(activeServerId(), mkm.getAttribute('data-make-member')).then(() => { lastSettingsSid = -1; renderSettings(); }); return; }
  const sb = e.target.closest && e.target.closest('[data-chat-send]');
  if (sb) { e.preventDefault(); e.stopPropagation(); chatSend(); return; }
  const db = e.target.closest && e.target.closest('[data-chat-delete]');
  if (db) { e.preventDefault(); e.stopPropagation(); chatDelete(db.getAttribute('data-chat-delete')); return; }
  // React: NO stopPropagation so wasp.js's outside-click popover-close still fires after a pick.
  const rb = e.target.closest && e.target.closest('[data-chat-react]');
  if (rb) { e.preventDefault(); chatReact(rb.getAttribute('data-chat-react'), rb.getAttribute('data-react-emoji')); return; }
  const tb = e.target.closest && e.target.closest('[data-thread-send]');
  if (tb) { e.preventDefault(); e.stopPropagation(); threadSend(tb); return; }
  const mdb = e.target.closest && e.target.closest('[data-mod-delete]');
  if (mdb) { e.preventDefault(); e.stopPropagation(); modDelete(mdb.getAttribute('data-mod-delete')); return; }
  const mpb = e.target.closest && e.target.closest('[data-mod-pin]');
  if (mpb) { e.preventDefault(); e.stopPropagation(); modPin(mpb); return; }
  const mlb = e.target.closest && e.target.closest('[data-mod-lock]');
  if (mlb) { e.preventDefault(); e.stopPropagation(); modLock(mlb); return; }
  const mmb = e.target.closest && e.target.closest('[data-mod-mute]');
  if (mmb) { e.preventDefault(); e.stopPropagation(); modMute(mmb); return; }
});
document.addEventListener('keydown', (e) => {
  if (e.key !== 'Enter' || e.shiftKey || e.ctrlKey || e.metaKey || e.altKey) return;
  const ta = e.target;
  if (ta && ta.tagName === 'TEXTAREA' && ta.name === 'text') {
    const form = ta.closest('form');
    if (form && form.querySelector('[data-chat-send]')) { e.preventDefault(); chatSend(); }
    else if (form && form.querySelector('[data-thread-send]')) { e.preventDefault(); threadSend(form.querySelector('[data-thread-send]')); }
  }
});

// PRIVATE forum/feed signed-read render (members only) — mirrors renderPrivateChannel.
async function renderPrivateForum() {
  const pane = document.querySelector('[data-private-forum]'); if (!pane) return;
  const stream = document.querySelector('[data-private-forum-stream]'); const msg = document.querySelector('[data-private-forum-msg]');
  const sid = Number(pane.getAttribute('data-server') || '0'); const tid = Number(pane.getAttribute('data-topic') || '0');
  if (!signedIn()) { if (msg) msg.textContent = 'Members only — sign in with Internet Identity to view.'; return; }
  const t = Date.now(); if (sid === lastPFSid && tid === lastPFTid && t - lastPFAt < 2500) return; lastPFSid = sid; lastPFTid = tid; lastPFAt = t;
  try {
    await ensureAgent();
    const r = await signedCall('POST', '/api/forum/read', JSON.stringify({ serverId: sid, roomId: tid }));
    if (r.status === 403) { if (msg) msg.textContent = 'You are not a member of this private forum.'; if (stream) stream.innerHTML = ''; return; }
    if (r.status !== 200) { if (msg) msg.textContent = 'Could not load.'; return; }
    let j = {}; try { j = JSON.parse(r.text); } catch (_) {}
    if (msg) msg.textContent = '';
    let html = '';
    if (j.mode === 'topic') {
      html += '<h1 class=""fr-title"">' + privEsc(j.title) + (j.locked ? ' <span class=""fr-lock-tag"">🔒 Locked</span>' : '') + '</h1>';
      (j.posts || []).forEach(function (p) {
        html += '<div class=""fr-post' + (p.isAnswer ? ' fr-answer' : '') + '"">'
          + '<div class=""fr-post-head""><span class=""fr-author"">' + privEsc(p.author) + '</span>'
          + (p.op ? ' <span class=""fr-op-badge"">OP</span>' : '') + (p.isAnswer ? ' <span class=""fr-answer-tag"">✓ Accepted</span>' : '')
          + '<button type=""button"" class=""fr-solve"" data-forum-solve data-room=""' + tid + '"" data-msg=""' + p.id + '"" data-on=""' + (p.isAnswer ? '0' : '1') + '"">' + (p.isAnswer ? 'Unmark' : '✓ Mark answer') + '</button></div>'
          + '<div class=""fr-body"">' + (p.html || '') + '</div>'
          + '<div class=""fr-postfoot""><span class=""fr-votes""><button type=""button"" class=""fr-vote"" data-forum-vote data-msg=""' + p.id + '"" data-dir=""up"">▲</button><span class=""fr-vote-score"">' + (p.score || 0) + '</span><button type=""button"" class=""fr-vote"" data-forum-vote data-msg=""' + p.id + '"" data-dir=""down"">▼</button></span></div>'
          + '</div>';
      });
      if (!j.locked) html += '<div class=""fr-reply-box""><textarea class=""fr-reply-input"" data-forum-reply-text=""' + tid + '"" rows=""3"" placeholder=""Write a reply…""></textarea><button type=""button"" class=""fr-btn fr-btn-primary"" data-forum-reply data-room=""' + tid + '"">Reply</button></div>';
    } else {
      const tops = j.topics || [];
      html += '<div class=""fr-cat-head""><div><h1>' + privEsc(j.name) + '</h1><p class=""fr-cat-sub"">' + tops.length + ' topic' + (tops.length === 1 ? '' : 's') + ' · private forum</p></div><button type=""button"" class=""fr-btn fr-btn-primary"" data-forum-new data-server=""' + sid + '"">+ New topic</button></div>';
      html += '<div class=""fr-newform"" data-forum-newform hidden><input class=""fr-newform-title"" data-forum-new-title placeholder=""Topic title"" maxlength=""160"" /><textarea class=""fr-newform-body"" data-forum-new-body rows=""4"" placeholder=""Write your post…""></textarea><div class=""fr-newform-actions""><button type=""button"" class=""fr-btn fr-btn-primary"" data-forum-new-submit data-server=""' + sid + '"">Create topic</button><button type=""button"" class=""fr-btn fr-btn-ghost"" data-forum-new-cancel>Cancel</button></div></div>';
      if (!tops.length) html += '<div class=""fr-empty""><p>No topics yet. Be the first.</p></div>';
      else {
        html += '<ul class=""fr-list"">';
        tops.forEach(function (tp) {
          html += '<li class=""fr-row""><a class=""fr-row-main"" href=""/forum?s=' + sid + '&t=' + tp.roomId + '""><span class=""fr-row-title"">' + privEsc(tp.title) + (tp.solved ? ' ✓' : '') + '</span><span class=""fr-row-meta"">by ' + privEsc(tp.author) + '</span></a><span class=""fr-row-stat""><b>' + (tp.replies || 0) + '</b><small>replies</small></span></li>';
        });
        html += '</ul>';
      }
    }
    if (stream) stream.innerHTML = html;
  } catch (_) { if (msg) msg.textContent = 'Network error.'; }
}
async function renderPrivateFeed() {
  const pane = document.querySelector('[data-private-feed]'); if (!pane) return;
  const stream = document.querySelector('[data-private-feed-stream]'); const msg = document.querySelector('[data-private-feed-msg]');
  const sid = Number(pane.getAttribute('data-server') || '0');
  if (!signedIn()) { if (msg) msg.textContent = 'Members only — sign in with Internet Identity to view.'; return; }
  const t = Date.now(); if (sid === lastPFeedSid && t - lastPFeedAt < 2500) return; lastPFeedSid = sid; lastPFeedAt = t;
  try {
    await ensureAgent();
    const r = await signedCall('POST', '/api/feed/read', JSON.stringify({ serverId: sid }));
    if (r.status === 403) { if (msg) msg.textContent = 'You are not a member of this private feed.'; if (stream) stream.innerHTML = ''; return; }
    if (r.status !== 200) { if (msg) msg.textContent = 'Could not load.'; return; }
    let j = {}; try { j = JSON.parse(r.text); } catch (_) {}
    if (msg) msg.textContent = '';
    const posts = j.posts || []; const roomId = j.roomId || 0;
    let html = '';
    if (roomId) html += '<div class=""fd-compose""><textarea class=""fd-input"" data-feed-post-text=""' + roomId + '"" rows=""2"" placeholder=""Post to this feed…""></textarea><button type=""button"" class=""fd-btn fd-btn-primary"" data-feed-post data-room=""' + roomId + '"">Post</button></div>';
    html += posts.length ? posts.map(feedPostRow).join('') : '<div class=""fd-empty""><p>No posts yet.</p></div>';
    if (stream) stream.innerHTML = html;
  } catch (_) { if (msg) msg.textContent = 'Network error.'; }
}

const root = document.getElementById('wasp-root');
if (root) { let pend = null; new MutationObserver(() => { if (pend) return; pend = requestAnimationFrame(() => { pend = null; refreshRoleUi(); renderPrivateChannel(); renderPrivateThread(); renderSettings(); feedHomeTick(); renderPrivateForum(); renderPrivateFeed(); }); }).observe(root, { childList: true, subtree: true }); }
refreshRoleUi();
renderPrivateChannel();
renderPrivateThread();
renderSettings();
feedHomeTick();
renderPrivateForum();
renderPrivateFeed();
// Deep-link/full-load recovery: a fresh load of /chat?s=<private> is served as the
// query-stripped snapshot; SPA-re-render so the private pane/settings paint correctly.
if (location.pathname === '/chat' && (qp('s') || qp('settings')) && !document.querySelector('[data-private-pane]') && !document.querySelector('[data-server-settings]')) {
  spaNav(location.pathname + location.search);
}
// Forum/feed deep-links: the full-page GET is query-agnostic (renders the default
// shell), so a hard-load of /forum?s=.. or /feed?s=.. must SPA-re-render to paint the
// right category/topic/timeline (and, for private servers, the members-only pane).
if ((location.pathname === '/forum' || location.pathname === '/feed') && location.search && location.search.length > 1) {
  spaNav(location.pathname + location.search);
}
";
}
