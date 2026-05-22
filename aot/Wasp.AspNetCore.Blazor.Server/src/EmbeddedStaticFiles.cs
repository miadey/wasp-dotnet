using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Wasp.AspNetCore.Blazor.Server;

// Minimal embedded-resource static file server. The canister can't read
// a real filesystem (wasi-stub strips fs syscalls), so static assets
// like blazor.web.js are bundled as embedded resources in the consumer
// assembly.
//
// To keep the trim footprint small we expose exactly one concrete
// route (/_framework/blazor.web.js) rather than a catch-all /{**path}
// matcher — the latter pulls more routing machinery into AOT and
// pushes the code section over IC's 11 MB cap.
//
// Consumer csproj:
//   <EmbeddedResource Include="wwwroot\_framework\blazor.web.js"
//                     LogicalName="wwwroot/_framework/blazor.web.js" />
//
// Consumer Program.cs:
//   app.MapWaspEmbeddedStaticFiles(typeof(Program).Assembly);
public static class EmbeddedStaticFiles
{
    public static IEndpointRouteBuilder MapWaspEmbeddedStaticFiles(
        this IEndpointRouteBuilder endpoints,
        Assembly assembly)
    {
        if (endpoints is null) throw new ArgumentNullException(nameof(endpoints));
        if (assembly is null) throw new ArgumentNullException(nameof(assembly));

        endpoints.MapGet("/_framework/blazor.web.js", async (HttpContext ctx) =>
        {
            using var stream = assembly.GetManifestResourceStream(
                "wwwroot/_framework/blazor.web.js");
            if (stream is null)
            {
                ctx.Response.StatusCode = 404;
                return;
            }
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/javascript";
            ctx.Response.ContentLength = stream.Length;
            await stream.CopyToAsync(ctx.Response.Body);
        });

        return endpoints;
    }

    /// <summary>
    /// Register every Wasp-bundled static asset with
    /// <see cref="Wasp.AspNetCore.IcServer.RegisterStaticAsset"/> so the
    /// IC HTTP gateway serves them directly from a query call (~50 ms)
    /// instead of upgrading to a ~3-second update call. Asset bytes are
    /// read from the consumer assembly's embedded resources at canister
    /// init time.
    ///
    /// Responses are not certified; the canister must be reached via the
    /// `.raw.` subdomain (e.g. <c>{canister-id}.raw.localhost:4944</c>)
    /// where the gateway skips response verification.
    /// </summary>
    public static void RegisterWaspStaticAssets(Assembly assembly)
    {
        if (assembly is null) throw new ArgumentNullException(nameof(assembly));

        var assets = new (string Url, string Resource, string ContentType)[]
        {
            ("/_framework/blazor.web.js", "wwwroot/_framework/blazor.web.js",
                "application/javascript; charset=utf-8"),
        };

        foreach (var (url, resource, contentType) in assets)
        {
            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null) continue;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            Wasp.AspNetCore.IcServer.RegisterStaticAsset(url, ms.ToArray(), contentType);
        }
    }
}
