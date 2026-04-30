using System;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Wasp.AspNetCore;

// First end-to-end ASP.NET Core canister sample (issue #47).
//
// Pattern mirrors HelloWeb: a [ModuleInitializer] runs at canister_init
// time (NativeAOT _initialize), builds a slim WebApplication, registers
// routes, and starts the host. UseIcCanister() swaps the default Kestrel
// IServer for IcServer (Wasp.AspNetCore), whose [UnmanagedCallersOnly]
// thunks export `canister_query http_request` and
// `canister_update http_request_update` — the IC HTTP gateway entry points.
//
// Each incoming request flows through the full ASP.NET Core middleware
// pipeline: Candid HttpRequest → DefaultHttpContext → app.MapGet handler
// → DefaultHttpContext.Response → Candid HttpResponse → Reply.

namespace WaspSample.AspNetCoreHello;

public static class AspNetCoreHelloCanister
{
    [ModuleInitializer]
    internal static void Init()
    {
        try
        {
            // CreateEmptyBuilder skips all default registrations — no
            // appsettings.json loading, no logging, no metrics. We only
            // need routing + endpoints, which we add ourselves below.
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
            {
                ContentRootPath = "/canister",
                ApplicationName = "AspNetCoreHello",
            });
            builder.Services.AddRoutingCore();
            builder.WebHost.UseIcCanister();

            var app = builder.Build();

            app.MapGet("/", () => "Hello from ASP.NET Core inside an IC canister!");
            app.MapGet("/health", () => "ok");

            app.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Capture the full exception chain so we can diagnose what's
            // blowing up during ASP.NET Core init on the canister.
            var sb = new System.Text.StringBuilder();
            for (var e = ex; e is not null; e = e.InnerException)
            {
                sb.Append(e.GetType().FullName).Append(": ").Append(e.Message);
                if (e.StackTrace is { } st)
                {
                    sb.Append('\n').Append(st);
                }
                if (e.InnerException is not null) sb.Append("\n--- inner ---\n");
            }
            _initFailure = sb.ToString();
            IcServer.InitFailureMessage = _initFailure;
            Wasp.IcCdk.Reply.Print("[init-fail] " + _initFailure);
        }
    }

    internal static string? _initFailure;
    public static string? InitFailure => _initFailure;
}
