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
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Components.CodeOnlyHello))]
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
            // a Razor component to a string. StaticHtmlRenderer's ctor reads:
            //   _htmlEncoder = serviceProvider.GetService<HtmlEncoder>() ?? HtmlEncoder.Default;
            //   _javaScriptEncoder = serviceProvider.GetService<JavaScriptEncoder>() ?? JavaScriptEncoder.Default;
            // If neither service resolution nor the .Default static returns
            // a value, the field stays null and Encode() NREs in RenderCore.
            // Bind the ABSTRACT types explicitly so GetService<HtmlEncoder>()
            // succeeds. (AddSingleton(instance) without a type arg binds by
            // runtime type which is the concrete DefaultHtmlEncoder subclass,
            // not the abstract base.)
            builder.Services.AddLogging();
            builder.Services.AddSingleton<System.Text.Encodings.Web.HtmlEncoder>(
                System.Text.Encodings.Web.HtmlEncoder.Default);
            builder.Services.AddSingleton<System.Text.Encodings.Web.JavaScriptEncoder>(
                System.Text.Encodings.Web.JavaScriptEncoder.Default);
            builder.Services.AddSingleton<System.Text.Encodings.Web.UrlEncoder>(
                System.Text.Encodings.Web.UrlEncoder.Default);
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

            // Render <App> via Razor's HtmlRenderer. This works because
            // Wasp.AspNetCore.targets substitutes a Mono.Cecil-rewritten
            // Microsoft.AspNetCore.Components.dll that fixes the
            // NativeAOT-LLVM struct-copy miscompilation in
            // RenderTreeFrameArrayBuilder.Append*. See UNSUPPORTED.md
            // > Razor SSR for the diagnosis.
            RequestDelegate razorHomepage = async ctx =>
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

            app.MapGet("/", razorHomepage);
            app.MapGet("/counter", razorHomepage);

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
