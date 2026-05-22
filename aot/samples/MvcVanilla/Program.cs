using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wasp.AspNetCore;
using Wasp.IcCdk;
using MvcVanilla.Controllers;

// M4.S9.6 — first attempt at MVC + Razor Views (.cshtml) on canister.
//
// Uses the in-tree extension API (builder.WebHost.UseIcCanister()) since the
// planned one-line builder.UseInternetComputer() has not landed yet in this
// branch.  Razor view rendering on canister is unproven; this sample is the
// canary for ViewEngine PNS / trim issues.

namespace MvcVanilla;

public static class MvcVanillaCanister
{
    // Pin controller + view types so the trimmer doesn't strip reflection
    // targets that MVC discovers via assembly scan + Activator.CreateInstance.
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(HomeController))]
    // #101 trim casualty: DefaultRazorPageFactoryProvider builds a view
    // activator via Expression.Bind on RazorPage{,Base} properties (Path,
    // ViewContext) and RazorPagePropertyActivator sets injected props
    // (ViewData, HtmlEncoder, Html, Component, Json, Url, TempData,
    // ViewBag, Output). With TrimMode=full those setters get stripped —
    // GetProperty returns null → Expression.Bind throws, OR the property
    // exists but its backing field stays null → NRE inside ExecuteAsync.
    // Root the base classes + the helpers the framework injects.
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Microsoft.AspNetCore.Mvc.Razor.RazorPage))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Microsoft.AspNetCore.Mvc.Razor.RazorPageBase))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Microsoft.AspNetCore.Mvc.Razor.RazorPage<>))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Microsoft.AspNetCore.Mvc.ViewFeatures.ViewDataDictionary))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Microsoft.AspNetCore.Mvc.ViewFeatures.ViewDataDictionary<>))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Microsoft.AspNetCore.Mvc.Rendering.IHtmlHelper))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Microsoft.AspNetCore.Mvc.Rendering.IHtmlHelper<>))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Microsoft.AspNetCore.Mvc.IUrlHelper))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Microsoft.AspNetCore.Mvc.IViewComponentHelper))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Microsoft.AspNetCore.Mvc.Rendering.IJsonHelper))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataDictionary))]
    // The Razor compiler auto-injects HeadTagHelper + BodyTagHelper on every
    // <head>/<body> for CSP-nonce support, even without an @addTagHelper
    // directive. AOT needs Cache<HeadTagHelper> and Cache<BodyTagHelper>
    // (generic static caches inside DefaultTagHelperActivator) instantiated
    // — root the helpers themselves so the trimmer pulls in those closures.
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Microsoft.AspNetCore.Mvc.Razor.TagHelpers.HeadTagHelper))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Microsoft.AspNetCore.Mvc.Razor.TagHelpers.BodyTagHelper))]
    [ModuleInitializer]
    internal static void Init()
    {
        try
        {
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
            {
                ContentRootPath = "/canister",
                ApplicationName = "MvcVanilla",
            });

            // DataProtection short-circuit BEFORE any framework call that
            // transitively pulls AddDataProtection (AddMvcCore does).
            builder.Services.UseIcDataProtection();

            // Routing + logging core.
            builder.Services.AddRoutingCore();
            builder.Services.AddLogging();

            // Encoders are required by Razor view rendering / HtmlHelper.
            builder.Services.AddSingleton<System.Text.Encodings.Web.HtmlEncoder>(
                System.Text.Encodings.Web.HtmlEncoder.Default);
            builder.Services.AddSingleton<System.Text.Encodings.Web.JavaScriptEncoder>(
                System.Text.Encodings.Web.JavaScriptEncoder.Default);
            builder.Services.AddSingleton<System.Text.Encodings.Web.UrlEncoder>(
                System.Text.Encodings.Web.UrlEncoder.Default);

            // Minimal MVC stack — manually composed instead of
            // AddControllersWithViews() to skip ApiExplorer, Authorization,
            // CORS, DataAnnotations, FormatterMappings, RazorPages,
            // CacheTagHelper, CookieTempData and the SystemTextJsonInput-
            // Formatter (which trips #103 / #96 PipeReader trim).
            //
            // Trade-off vs stock: no [FromBody] JSON binding, no [Authorize]
            // filters, no antiforgery, no [Bind]/DataAnnotations validation.
            // This sample has none of those.
            builder.Services
                .AddMvcCore()
                .AddViews()
                .AddRazorViewEngine()
                // #101: ApplicationPartManager's default discovery walks
                // Assembly.GetEntryAssembly() — null for NativeAOT shared
                // libraries — and Assembly.Location, which is empty for
                // single-file/AOT. Both return nothing here, so the
                // compiled-views part is never registered and the view
                // engine falls back to filesystem search → 500.
                //
                // Register our assembly explicitly. The same assembly
                // holds both controllers and the precompiled views
                // (MvcRazorCompileOnPublish merges them into the main
                // DLL on .NET 6+).
                .ConfigureApplicationPartManager(apm =>
                {
                    var asm = typeof(MvcVanillaCanister).Assembly;
                    apm.ApplicationParts.Add(new AssemblyPart(asm));
                    apm.ApplicationParts.Add(new CompiledRazorAssemblyPart(asm));
                });

            builder.WebHost.UseIcCanister();

            var app = builder.Build();

            // Endpoint routing — required for MapControllerRoute.
            app.UseRouting();

            // Static file probe — /css/site.css is embedded in the published
            // wwwroot. Until we wire an embedded-resource file provider in
            // Wasp.AspNetCore, this WILL 404. Plain fallback below documents it.
            app.Use(async (ctx, next) =>
            {
                if (ctx.Request.Path == "/css/site.css")
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "text/css";
                    await ctx.Response.WriteAsync("/* MvcVanilla static stub */\nbody { font-family: system-ui; }\n");
                    return;
                }
                await next();
            });

            app.MapControllerRoute(
                name: "default",
                pattern: "{controller=Home}/{action=Index}/{id?}");

            // Plain control endpoint — proves the response pipe works even if
            // Razor view rendering blows up.
            app.MapGet("/_plain", () => "plain text from MvcVanilla canister");

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
