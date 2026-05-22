using System;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Wasp.AspNetCore;
using Wasp.IcCdk;

namespace MinimalApiOnIcp;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Note))]
[JsonSerializable(typeof(object))]   // RDG asks for typeof(object) at startup
internal partial class JsonCtx : JsonSerializerContext { }

public sealed record Note(string Title, int Priority);

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
                ApplicationName = "MinimalApiOnIcp",
            });

            builder.UseInternetComputer();
            builder.Services.ConfigureHttpJsonOptions(o =>
            {
                o.SerializerOptions.TypeInfoResolverChain.Insert(0, JsonCtx.Default);
            });

            var app = builder.Build();

            // Top-level endpoints — the entire backend for the
            // simplest dapp shape. Add more MapGet / MapPost / MapPut
            // / MapDelete calls here.
            app.MapGet("/", () => "Hello from the IC!");

            app.MapGet("/echo/{msg}", (string msg) => $"You said: {msg}");

            app.MapPost("/note", (Note n) => $"Note '{n.Title}' priority {n.Priority}");

            app.MapGet("/time", () => Results.Json(new
            {
                canister_time_ns = Ic0.time(),
                wasi_utc = DateTime.UtcNow.ToString("O"),
            }));

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
