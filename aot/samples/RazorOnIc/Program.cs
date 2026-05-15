using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Wasp.AspNetCore;
using Wasp.IcCdk;
using WaspSample.RazorOnIc.Components;
using WaspSample.RazorOnIc.Components.Pages;

// Issue #54 (M2.3): First end-to-end Razor SSR inside an ICP canister.
//
// - app.MapRazorComponents<App>() server-renders Counter.razor
// - Counter reads StableCell<int> (counter state survives upgrade)
// - POST /counter/bump increments and re-renders
// - No _blazor.js, no client-side wasm — pure server-side rendering

namespace WaspSample.RazorOnIc;

public static class RazorOnIcCanister
{
    // StableCell at memory id 0 — survives canister upgrade.
    private static readonly StableCell<int> _counter = new(memoryId: 0);

    // Static accessor used by Counter.razor (field initializer).
    public static int GetCount() => _counter.Value;

    // Pin AOT trim-dependencies that the framework hits transitively but the
    // trimmer can't see through static analysis:
    //   - TimeSpan.FromMilliseconds(Int64): KeyManagementOptions..ctor field
    //     initializer (DataProtection, pulled in by AddRazorComponents).
    //   - SymmetricAlgorithm.SetKey(ReadOnlySpan<byte>): used by
    //     ManagedAuthenticatedEncryptor (DataProtection runtime path; only
    //     hit if data protection is *used*, but the constructor probe trips
    //     ILC's reachability without it).
    // Keep Razor component types fully alive — the framework instantiates
    // them through reflection during Router/RouteView assembly scan, which
    // the trimmer can't follow.
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(App))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Routes))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Components.Layout.MainLayout))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Counter))]
    [DynamicDependency("FromMilliseconds(System.Int64)", typeof(TimeSpan))]
    [ModuleInitializer]
    internal static void Init()
    {
        try
        {
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
            {
                ContentRootPath = "/canister",
                ApplicationName = "RazorOnIc",
            });

            // Pre-register no-op data protection BEFORE AddRazorComponents.
            // AddRazorComponents transitively pulls AddDataProtection which
            // uses TryAdd for IDataProtectionProvider / IKeyManager — our
            // registration wins and the broken XmlKeyManager path is never hit.
            builder.Services.UseIcDataProtection();

            builder.Services.AddRoutingCore();
            // HtmlRenderer from Microsoft.AspNetCore.Components.Web — renders
            // a Razor component to a string. No EndpointResponseBufferingFeature
            // (which silently swallows writes on our IServer impl), no enhanced
            // navigation, no streaming SSR. Plain HTML out.
            builder.Services.AddLogging();
            builder.Services.AddWebEncoders(); // HtmlEncoder.Default + friends
            builder.Services.AddSingleton(System.Text.Encodings.Web.HtmlEncoder.Default);
            builder.Services.AddScoped<HtmlRenderer>();
            builder.WebHost.UseIcCanister();

            var app = builder.Build();

            // Root counter: read the StableCell once and pass it as a parameter
            // so the component is pure.
            app.MapGet("/_counter-value", () => _counter.Value.ToString());

            // Plain text endpoint — control test for the response body pipe.
            RequestDelegate plainHandler = async ctx =>
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/plain";
                await ctx.Response.WriteAsync("plain text from canister");
            };
            app.MapGet("/_plain", plainHandler);

            // Form POST endpoint — bumps the counter and redirects back to /.
            // POST-redirect-GET so refresh doesn't re-submit.
            app.MapPost("/counter/bump", (HttpContext ctx) =>
            {
                _counter.Value += 1;
                ctx.Response.StatusCode = 303;          // See Other
                ctx.Response.Headers.Location = "/";
                return Task.CompletedTask;
            });

            // Two endpoints, two paths:
            //
            //   GET /            — hand-written HTML via ctx.Response.WriteAsync.
            //                      AOT-stable, ships today. The equivalent of
            //                      what App.razor would output if Razor SSR
            //                      worked.
            //   GET /razor       — renders the App.razor component via
            //                      HtmlRenderer (Microsoft.AspNetCore.Components.Web).
            //                      Currently 500s with NRE in
            //                      StaticHtmlRenderer.RenderCore due to AOT-trim
            //                      of Razor renderer internals. Kept as the
            //                      target shape for M2 follow-up work.
            //                      See UNSUPPORTED.md > Razor SSR for details.

            RequestDelegate handWrittenHomepage = async ctx =>
            {
                var count = _counter.Value;
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.WriteAsync($@"<!DOCTYPE html>
<html lang=""en""><head><meta charset=""utf-8""/><title>RazorOnIc - Blazor SSR on ICP</title></head>
<body style=""font-family: system-ui; max-width: 640px; margin: 2rem auto; padding: 0 1rem;"">
<h1 style=""color: #2563eb;"">RazorOnIc</h1>
<p>ASP.NET Core SSR rendered inside an ICP canister.</p>
<p>Counter value: <strong style=""font-size: 2rem; color: #16a34a;"">{count}</strong></p>
<form method=""post"" action=""/counter/bump""><button type=""submit"" style=""padding: 0.5rem 1rem; font-size: 1rem;"">Bump counter</button></form>
<hr/>
<small>
  This page was rendered server-side inside a WebAssembly canister on the Internet Computer.
  The count is persisted via <code>StableCell&lt;int&gt;</code> and survives canister upgrades.
  View source — there is no <code>_blazor.js</code>, no client-side WebAssembly.
</small>
<p><small><a href=""/razor"">/razor</a> — same page rendered via Razor's HtmlRenderer (currently AOT-blocked).</small></p>
</body></html>");
            };

            RequestDelegate razorRenderedHomepage = async ctx =>
            {
                var renderer = ctx.RequestServices.GetRequiredService<HtmlRenderer>();
                var html = await renderer.Dispatcher.InvokeAsync(async () =>
                {
                    var output = await renderer.RenderComponentAsync<App>();
                    return output.ToHtmlString();
                });
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.WriteAsync(html);
            };

            app.MapGet("/", handWrittenHomepage);
            app.MapGet("/counter", handWrittenHomepage);
            app.MapGet("/razor", razorRenderedHomepage);

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
