using System;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Wasp.AspNetCore;
using Wasp.AspNetCore.Blazor.Wasp;
using Wasp.IcCdk;
using BlazorOnIcp.Components.Pages;

namespace BlazorOnIcp;

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
                ApplicationName = "BlazorOnIcp",
            });

            // Singletons that survive across calls. Each render
            // re-instantiates the component, so transient component
            // state belongs in a DI service or stable memory — not in
            // local component fields.
            builder.Services.AddSingleton<CounterService>();

            builder.Services.AddSingleton<IWaspRenderer>(sp =>
            {
                var router = new WaspRouter(sp);
                router.AddRoute<Home>("/");
                router.AddRoute<Counter>("/counter");
                return router;
            });

            builder.UseInternetComputerWasp();

            var app = builder.Build();
            app.UseInternetComputerWasp();
            app.StartAsync().GetAwaiter().GetResult();

            var renderer = app.Services.GetRequiredService<IWaspRenderer>();
            RegisterShell(renderer, "/");
            RegisterShell(renderer, "/counter");
        }
        catch (Exception ex)
        {
            var msg = ex.GetType().FullName + ": " + ex.Message;
            IcServer.InitFailureMessage = msg;
            Reply.Print("[init-fail] " + msg);
        }
    }

    private static void RegisterShell(IWaspRenderer renderer, string path)
    {
        var batch = renderer.Render(new WaspRenderRequest { Path = path });
        var shell =
@"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""utf-8"" />
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"" />
    <title>BlazorOnIcp</title>
</head>
<body>
    <div id=""wasp-root"">" + batch.Html + @"</div>
    <script src=""/_wasp/wasp.js""></script>
</body>
</html>";
        var bytes = Encoding.UTF8.GetBytes(shell);
        IcServer.RegisterStaticAsset(path, bytes, "text/html; charset=utf-8");
        IcCertifiedAssets.Insert(path, bytes);
    }
}
