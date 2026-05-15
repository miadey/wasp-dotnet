using System;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Wasp.AspNetCore;
using Wasp.IcCdk;

// Issue #51 deliverable: prove the full ASP.NET Core middleware pipeline runs
// inside a canister. Exercises three middlewares (logging probe, UseRouting,
// UseAuthorization), endpoint grouping via MapGroup, scoped DI (IGreeter
// resolved per-request), and JSON body POST.
//
// Authentication note: ASP.NET Core's `AddAuthentication()` transitively
// registers DataProtection (XmlKeyManager) whose KeyManagementOptions..ctor()
// dies under the wasm32-wasi trimmer (TimeSpan.FromMilliseconds(Int64)
// removed). To prove the AuthorizationMiddleware in isolation, we skip the
// full auth stack and decode the cookie via a custom middleware that sets
// ctx.User directly. AuthorizationMiddleware falls back to ctx.User when the
// policy has no AuthenticationSchemes, so this is semantically equivalent.

namespace WaspSample.AspNetCoreApi;

public sealed record Note(string Title, int Priority);

public interface IGreeter
{
    string Greet(string who);
    int InstanceId { get; }
}

public sealed class Greeter : IGreeter
{
    private static int _nextId;
    public Greeter() { InstanceId = System.Threading.Interlocked.Increment(ref _nextId); }
    public int InstanceId { get; }
    public string Greet(string who) => $"hello {who} (greeter#{InstanceId})";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Note))]
[JsonSerializable(typeof(object))]
internal partial class ApiJsonContext : JsonSerializerContext
{
}

// Custom AuthorizationMiddleware result handler: sets 401/403 directly instead
// of calling context.ChallengeAsync, which requires IAuthenticationService
// (and therefore the full Authentication stack with DataProtection).
internal sealed class IcAuthMiddlewareResultHandler : IAuthorizationMiddlewareResultHandler
{
    public async Task HandleAsync(
        RequestDelegate next, HttpContext context,
        AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (result.Forbidden) { context.Response.StatusCode = 403; return; }
        if (result.Challenged) { context.Response.StatusCode = 401; return; }
        await next(context);
    }
}

public static class AspNetCoreApiCanister
{
    [ModuleInitializer]
    internal static void Init()
    {
        try
        {
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
            {
                ContentRootPath = "/canister",
                ApplicationName = "AspNetCoreApi",
            });

            // Routing core (slim variant; no link generation).
            builder.Services.AddRoutingCore();

            // JSON: source-gen context only (no reflection resolver under AOT).
            builder.Services.ConfigureHttpJsonOptions(o =>
            {
                o.SerializerOptions.TypeInfoResolver = ApiJsonContext.Default;
            });

            // Authorization (no Authentication stack — see file-header note).
            // Default policy: must have an authenticated identity. The cookie
            // middleware below populates ctx.User.
            builder.Services.AddAuthorizationBuilder()
                .SetDefaultPolicy(new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .Build());

            // Replace the default IAuthorizationMiddlewareResultHandler so 401/403
            // are written directly, bypassing IAuthenticationService.ChallengeAsync.
            builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler,
                IcAuthMiddlewareResultHandler>();

            // Scoped DI service — proves the per-request scope is real.
            builder.Services.AddScoped<IGreeter, Greeter>();

            builder.WebHost.UseIcCanister();

            var app = builder.Build();

            // Middleware #1 — request logging probe (surfaces in dfx canister logs).
            app.Use(async (ctx, next) =>
            {
                Reply.Print($"[mw:log] {ctx.Request.Method} {ctx.Request.Path}");
                await next();
                Reply.Print($"[mw:log] status={ctx.Response.StatusCode}");
            });

            // Custom cookie-auth middleware — drops in for the full Authentication
            // stack. Reads `Cookie: user=<name>` and sets ctx.User. Must run
            // BEFORE UseAuthorization so the policy evaluator sees the identity.
            app.Use(async (ctx, next) =>
            {
                var user = ctx.Request.Cookies["user"];
                if (!string.IsNullOrEmpty(user))
                {
                    var claims = new[] { new Claim(ClaimTypes.Name, user) };
                    var identity = new ClaimsIdentity(claims, authenticationType: "Cookie");
                    ctx.User = new ClaimsPrincipal(identity);
                }
                await next();
            });

            // Middleware #2 — UseRouting (built-in; matches the endpoint).
            app.UseRouting();

            // Probe after routing.
            app.Use(async (ctx, next) =>
            {
                var ep = ctx.GetEndpoint();
                Reply.Print($"[mw:post-routing] endpoint={ep?.DisplayName ?? "(none)"}");
                await next();
            });

            // Middleware #3 — UseAuthorization (built-in; evaluates the policy
            // against ctx.User and 401s anonymous calls to RequireAuthorization
            // endpoints).
            app.UseAuthorization();

            app.Use(async (ctx, next) =>
            {
                var authenticated = ctx.User?.Identity?.IsAuthenticated ?? false;
                Reply.Print($"[mw:post-authz] authenticated={authenticated} user={ctx.User?.Identity?.Name ?? "(none)"}");
                await next();
            });

            // MapGroup("/api") with scoped DI consumption.
            var api = app.MapGroup("/api");

            api.MapGet("/echo/{msg}", (string msg, IGreeter g) => g.Greet(msg));

            // Proves scope: two resolves from the same request share the same
            // instance id; resolves from different requests differ.
            api.MapGet("/scope", (IGreeter a, IGreeter b) =>
                $"same-scope={a.InstanceId == b.InstanceId} ids={a.InstanceId},{b.InstanceId}");

            // POST /api/save — manual body read (per UNSUPPORTED.md, the typed
            // RDG body binding path is AOT-trim-broken).
            // Use the RequestDelegate-typed Map overload — bypasses the
            // RDG (RequestDelegateGenerator) entirely, which in some
            // configurations trims the IResult-execution path so the body
            // never reaches the response stream (status set but content empty).
            // Writing directly to ctx.Response is AOT-stable.
            RequestDelegate saveHandler = async ctx =>
            {
                using var sr = new System.IO.StreamReader(ctx.Request.Body);
                var json = await sr.ReadToEndAsync();
                var note = JsonSerializer.Deserialize(json, ApiJsonContext.Default.Note);
                if (note is null)
                {
                    ctx.Response.StatusCode = 400;
                    ctx.Response.ContentType = "text/plain; charset=utf-8";
                    await ctx.Response.WriteAsync("invalid json");
                    return;
                }
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                await ctx.Response.WriteAsync($"saved '{note.Title}' priority={note.Priority}");
            };
            api.MapPost("/save", saveHandler);

            // Protected endpoint — requires ctx.User.Identity.IsAuthenticated.
            api.MapGet("/secret", (HttpContext ctx) =>
                $"hello {ctx.User.Identity?.Name}, the secret is 42")
               .RequireAuthorization();

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
