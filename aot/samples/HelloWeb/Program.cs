using System.Runtime.CompilerServices;
using Wasp.Http;
using Wasp.IcCdk;

namespace WaspSample.HelloWeb;

public static class HelloWebCanister
{
    private static readonly StableCell<ulong> _counter = new(memoryId: 0);

    [ModuleInitializer]
    internal static void RegisterRoutes()
    {
        // Phase 2 v0.1: every query upgrades to an update call. This
        // sidesteps the gateway's response-verification requirement
        // (queries need certified data, updates don't). Asset
        // certification is on the Phase 3 list.
        WaspHttp.RequireUpdateForAll();

        WaspHttp.Get("/", _ => HttpResponse.Html(
            "<!doctype html><meta charset=utf-8><title>wasp-dotnet</title>" +
            "<h1>.NET 10 on ICP</h1>" +
            "<p>Try <a href=/hello>/hello</a>, <a href=/count>/count</a>, " +
            "or <a href=/bump>/bump</a>.</p>"));

        WaspHttp.Get("/hello", _ => HttpResponse.Text(
            "Hello from .NET 10 running inside an Internet Computer canister!"));

        WaspHttp.Get("/count", _ => HttpResponse.Json(
            $"{{\"count\":{_counter.Value}}}"));

        // Update-eligible: increments the persistent counter and returns
        // the new value. Reachable on the update pass because we forced
        // upgrade for all queries.
        WaspHttp.Get("/bump", _ =>
        {
            _counter.Value++;
            return HttpResponse.Json($"{{\"count\":{_counter.Value}}}");
        });
    }
}
