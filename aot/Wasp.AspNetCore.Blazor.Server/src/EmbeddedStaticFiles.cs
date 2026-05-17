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
    /// <see cref="Wasp.AspNetCore.IcServer.RegisterCertifiedAsset"/> so
    /// the IC HTTP gateway serves them via the query call path
    /// (~50 ms) instead of upgrading to a ~3-second update call.
    /// Call ONCE at canister init, before <c>app.StartAsync</c>.
    /// </summary>
    public static void CertifyWaspEmbeddedStaticFiles(Assembly assembly)
    {
        if (assembly is null) throw new ArgumentNullException(nameof(assembly));

        // Each pair: (URL path, manifest resource name).
        // Extend this list if you add more <EmbeddedResource> entries to
        // the consumer csproj — every registered asset gets a single
        // SHA256(body) entry in the cert tree.
        var assets = new (string Url, string Resource)[]
        {
            ("/_framework/blazor.web.js", "wwwroot/_framework/blazor.web.js"),
        };

        foreach (var (url, resource) in assets)
        {
            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null) continue;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            Wasp.AspNetCore.IcServer.RegisterCertifiedAsset(url, ms.ToArray());
        }

        Wasp.AspNetCore.IcServer.CommitCertifiedAssets();
    }
}
