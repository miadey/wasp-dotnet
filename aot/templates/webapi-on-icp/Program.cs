using System;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Wasp.AspNetCore;
using Wasp.IcCdk;

namespace WebApiOnIcp;

// Source-generated JSON context. AOT trimming removes reflection-based
// serialisation; controllers using it must register the context's
// resolver. Add every type your endpoints serialise here.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WeatherForecast))]
[JsonSerializable(typeof(WeatherForecast[]))]
internal partial class ApiJsonContext : JsonSerializerContext { }

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
                ApplicationName = "WebApiOnIcp",
            });

            builder.UseInternetComputer();          // the one line of IC wiring

            builder.Services.AddControllers()
                .AddJsonOptions(o =>
                {
                    o.JsonSerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default);
                });

            var app = builder.Build();
            app.MapControllers();
            app.RunOnIC();                          // canister-friendly variant of Run()
        }
        catch (Exception ex)
        {
            var msg = ex.GetType().FullName + ": " + ex.Message;
            IcServer.InitFailureMessage = msg;
            Reply.Print("[init-fail] " + msg);
        }
    }
}

public sealed record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
