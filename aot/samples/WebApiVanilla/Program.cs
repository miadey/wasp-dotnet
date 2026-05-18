using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.DependencyInjection;
using Wasp.AspNetCore;
using Wasp.IcCdk;

// M4.S9.5 — first WebAPI / controllers canister.
//
// The vision: stock `dotnet new webapi` template + ONE line
// (builder.UseInternetComputer()) + RunOnIC() in place of app.Run()
// deploys to a canister.
//
// What's stock template code:
//   builder.Services.AddControllers();
//   var app = builder.Build();
//   app.MapControllers();
//
// What's Wasp glue:
//   var builder = WebApplication.CreateEmptyBuilder(...);    // ← canister
//                                                              ApplicationName
//   builder.UseInternetComputer();                            // ← the one line
//   app.RunOnIC();                                            // ← canister-friendly
//
// The [ModuleInitializer] is canister-specific (replaces the Main entry
// point that doesn't exist on wasm32-wasi).

namespace WaspSample.WebApiVanilla;

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
                ApplicationName = "WebApiVanilla",
            });

            builder.UseInternetComputer();          // ← THE ONE LINE
            builder.Services.AddControllers()
                .AddJsonOptions(o =>
                {
                    // gh #95 (M4.S9.5a) — register the source-gen JSON
                    // context so the STJ output formatter doesn't need
                    // the reflection-based resolver (trimmed under AOT).
                    o.JsonSerializerOptions.TypeInfoResolverChain.Insert(
                        0, WeatherJsonContext.Default);
                });

            // gh #96 (M4.S9.5b) — swap the framework input formatter
            // (which routes through the trimmed
            // JsonSerializer.DeserializeAsync(PipeReader, ...)) for a
            // Wasp-supplied formatter using the Stream overload.
            builder.Services.PostConfigure<MvcOptions>(opts =>
            {
                var stj = opts.InputFormatters
                    .OfType<SystemTextJsonInputFormatter>()
                    .FirstOrDefault();
                if (stj is not null) opts.InputFormatters.Remove(stj);

                opts.InputFormatters.Insert(0, new WaspJsonInputFormatter(
                    WeatherJsonContext.Default.Options));
            });

            var app = builder.Build();
            app.MapControllers();

            app.RunOnIC();                          // ← canister-friendly app.Run()
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
