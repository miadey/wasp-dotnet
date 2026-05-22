using System;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Wasp.AspNetCore;
using Wasp.IcCdk;

namespace MvcOnIcp;

public static class Program
{
    [ModuleInitializer]
    internal static void Init()
    {
        try
        {
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
            {
                ContentRootPath = "/canister",
                ApplicationName = "MvcOnIcp",
            });

            builder.UseInternetComputer();
            builder.Services.AddControllersWithViews();

            var app = builder.Build();
            app.MapStaticAssets();
            app.MapControllerRoute(
                name: "default",
                pattern: "{controller=Home}/{action=Index}/{id?}");
            app.RunOnIC();
        }
        catch (Exception ex)
        {
            var msg = ex.GetType().FullName + ": " + ex.Message;
            IcServer.InitFailureMessage = msg;
            Reply.Print("[init-fail] " + msg);
        }
    }
}
