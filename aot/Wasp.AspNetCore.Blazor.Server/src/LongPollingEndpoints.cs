using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Wasp.AspNetCore.Blazor.Server;

// Hand-rolled SignalR Long Polling endpoints. Lets Blazor's circuit ride
// over the standard IC HTTP gateway (canister http_request_update) — no
// external WebSocket gateway required.
//
// The three endpoints SignalR expects:
//   POST /_blazor/negotiate      → returns JSON advertising LongPolling
//   POST /_blazor?id=<id>        → inbound: BlazorPack frames in the body
//   GET  /_blazor?id=<id>        → poll:    drain queued outbound bytes
//   DELETE /_blazor?id=<id>      → close
//
// The blazor.web.js SignalR client picks the LongPolling transport from
// the negotiate response and chats with these endpoints exactly as it
// would chat with a stock ASP.NET Core MapBlazorHub() server — except
// we route through our IcCircuitTransport / BlazorHubDispatcher /
// CircuitHubFacade chain (already exercised by the prior WS path).
//
// Wire-up from a canister Program.cs:
//
//     var registry = new IcCircuitTransportRegistry();
//     registry.TransportConnected += t => CircuitHubFacade.Bind(t, ...);
//     app.MapWaspBlazorLongPolling(registry);
public static class LongPollingEndpoints
{
    public static IEndpointRouteBuilder MapWaspBlazorLongPolling(
        this IEndpointRouteBuilder endpoints,
        IcCircuitTransportRegistry registry,
        string pattern = "/_blazor")
    {
        if (endpoints is null) throw new ArgumentNullException(nameof(endpoints));
        if (registry is null) throw new ArgumentNullException(nameof(registry));

        // Blazor's client boot sequence fetches /_blazor/initializers
        // BEFORE negotiate, expecting a JSON array of additional JS init
        // module URLs. We don't have any — return []. If this endpoint
        // 404s, blazor.web.js calls response.json() on the "not found"
        // body and throws "Unexpected end of JSON input".
        endpoints.MapPost(pattern + "/initializers", async (HttpContext ctx) =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("[]");
        }).DisableAntiforgery();

        // Some Blazor clients GET /_blazor/initializers too.
        endpoints.MapGet(pattern + "/initializers", async (HttpContext ctx) =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("[]");
        });

        // SignalR negotiate. We only advertise LongPolling — Blazor's client
        // honors the order and picks the first transport it supports, so this
        // bypasses the WebSocket/SSE fallback dance.
        endpoints.MapPost(pattern + "/negotiate", async (HttpContext ctx) =>
        {
            string connectionId = Guid.NewGuid().ToString("N");
            registry.CreateLongPollingConnection(connectionId);

            string json =
                "{\"connectionId\":\"" + connectionId +
                "\",\"connectionToken\":\"" + connectionId +
                "\",\"negotiateVersion\":1,\"availableTransports\":[" +
                "{\"transport\":\"LongPolling\",\"transferFormats\":[\"Text\",\"Binary\"]}]}";

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(json);
        }).DisableAntiforgery();

        // Inbound — POST a (possibly concatenated) sequence of BlazorPack
        // frames. IcCircuitTransport.HandleInbound knows how to split on
        // the VLQ length prefix and dispatch.
        endpoints.MapPost(pattern, async (HttpContext ctx) =>
        {
            string? id = ctx.Request.Query["id"];
            if (string.IsNullOrEmpty(id))
            {
                ctx.Response.StatusCode = 400;
                return;
            }
            var conn = registry.GetLongPollingConnection(id);
            if (conn is null)
            {
                ctx.Response.StatusCode = 404;
                return;
            }

            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            var bytes = ms.ToArray();
            if (bytes.Length > 0)
            {
                try
                {
                    conn.Transport.HandleInbound(bytes);
                }
                catch (Exception ex)
                {
                    // Surface the actual error so the client log is
                    // diagnosable. Bytes are dumped (max 256) as hex.
                    int dumpLen = Math.Min(bytes.Length, 256);
                    var hex = new System.Text.StringBuilder(dumpLen * 2);
                    for (int i = 0; i < dumpLen; i++) hex.Append(bytes[i].ToString("x2"));
                    ctx.Response.StatusCode = 400;
                    ctx.Response.ContentType = "text/plain";
                    await ctx.Response.WriteAsync(
                        $"{ex.GetType().Name}: {ex.Message}\nfirst {dumpLen}/{bytes.Length} bytes: {hex}");
                    return;
                }
            }
            ctx.Response.StatusCode = 200;
        }).DisableAntiforgery();

        // Poll — return all queued outbound bytes (concatenated, since each
        // entry is already a complete length-prefixed frame the client can
        // split). Returns empty body immediately if nothing is queued;
        // blazor.web.js re-polls on its own cadence.
        //
        // We buffer to a byte[] first so ContentLength is set BEFORE the
        // response is committed — without it the framework picks chunked
        // transfer encoding which the IC HTTP gateway buffers strangely
        // (browser sees content-length: 0 even when bytes were written).
        endpoints.MapGet(pattern, async (HttpContext ctx) =>
        {
            string? id = ctx.Request.Query["id"];
            if (string.IsNullOrEmpty(id))
            {
                ctx.Response.StatusCode = 400;
                return;
            }
            var conn = registry.GetLongPollingConnection(id);
            if (conn is null)
            {
                ctx.Response.StatusCode = 404;
                return;
            }

            using var ms = new MemoryStream();
            while (conn.Outbound.TryDequeue(out var frame))
            {
                ms.Write(frame, 0, frame.Length);
            }
            var bytes = ms.ToArray();

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.ContentLength = bytes.Length;
            if (bytes.Length > 0)
            {
                await ctx.Response.Body.WriteAsync(bytes);
            }
        }).DisableAntiforgery();

        // Explicit close (SignalR client sends DELETE on dispose).
        endpoints.MapDelete(pattern, (HttpContext ctx) =>
        {
            string? id = ctx.Request.Query["id"];
            if (!string.IsNullOrEmpty(id))
            {
                registry.CloseLongPollingConnection(id);
            }
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        }).DisableAntiforgery();

        return endpoints;
    }
}
