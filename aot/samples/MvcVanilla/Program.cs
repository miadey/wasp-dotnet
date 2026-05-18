using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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

            // The stock dotnet new mvc one-liner:
            builder.Services.AddControllersWithViews();

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
