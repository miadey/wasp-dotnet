using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wasp.IcCdk;

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
    // Monotonic counter to disambiguate negotiate calls landing in the
    // same Ic0.time() nanosecond. Same scheme as the query-path negotiate
    // handler in BlazorOnIcHostingExtensions so connectionIds look
    // consistent regardless of which path generated them.
    private static long _negotiateSeq;

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
        //
        // ConnectionId scheme: Ic0.time() (ns) + monotonic counter,
        // hex-encoded. Guid.NewGuid is deterministic under wasi-stub
        // (no __wasi_random_get), so two clients hitting negotiate at
        // the same moment would get the same id — unusable. The query-
        // handler path in BlazorOnIcHostingExtensions uses the same
        // scheme so an upgraded POST yields a consistent id format.
        endpoints.MapPost(pattern + "/negotiate", async (HttpContext ctx) =>
        {
            // ConnectionId scheme: 64-bit Ic0.time() nanoseconds, hex, +
            // a per-call message-counter byte mixed in from the host
            // header hash (cheap entropy that's NOT rolled back by
            // query-context). Two clients negotiating within the same
            // nanosecond are vanishingly rare, but combining the
            // request-derived suffix makes the collision surface
            // empirically zero.
            ulong now = Wasp.IcCdk.Ic0.time();
            ulong seq = (ulong)System.Threading.Interlocked.Increment(ref _negotiateSeq);
            // Mix in arg-data hash so even if Interlocked rolls back
            // (it shouldn't in update context but defensive) we still
            // get uniqueness from the request URL / headers.
            ulong argHash = 0;
            foreach (var h in ctx.Request.Headers)
            {
                if (h.Key.Equals("host", StringComparison.OrdinalIgnoreCase) ||
                    h.Key.Equals("user-agent", StringComparison.OrdinalIgnoreCase) ||
                    h.Key.Equals("traceparent", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var v in h.Value)
                    {
                        if (v is null) continue;
                        foreach (var c in v) argHash = (argHash * 31) + c;
                    }
                }
            }
            string connectionId =
                now.ToString("x16", System.Globalization.CultureInfo.InvariantCulture)
                + (seq ^ argHash).ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
            Wasp.IcCdk.Reply.Print($"[negotiate] id={connectionId} now={now} seq={seq} argHash={argHash}");

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
            // Lazy-create the connection on first POST so the client
            // can skipNegotiation entirely — saves a ~1.5 s update-call
            // round trip on startup. The client just mints its own id
            // and starts POSTing.
            var conn = registry.GetLongPollingConnection(id)
                       ?? registry.CreateLongPollingConnection(id);

            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            var bytes = ms.ToArray();
            Reply.Print($"[lp] POST id={id} {bytes.Length}B");
            if (bytes.Length > 0)
            {
                try
                {
                    conn.Transport.HandleInbound(bytes);
                }
                catch (Exception ex)
                {
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

            // SignalR Long Polling's `send` helper DISCARDS the POST
            // response body — only the GET _poll loop pipes data into
            // onreceive. So we don't drain the queue here; we let the
            // next GET poll pick up any bytes (handshake ack, render
            // frames) that HandleInbound just enqueued.
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.ContentLength = 0;
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
            // Lazy-create (same as POST path) so skipNegotiation:true
            // works even when the first request is a GET poll.
            var conn = registry.GetLongPollingConnection(id)
                       ?? registry.CreateLongPollingConnection(id);

            // Cross-circuit reactivity is handled synchronously inside
            // each component's event subscription handler (see
            // Counter.razor's OnCounterChanged for the pattern: install
            // the component's RSC, call StateHasChanged via reflection,
            // restore). That writes the render-diff directly to this
            // circuit's outbox during the event-firing call. No RSC
            // queue pumping needed on the poll side, which is good —
            // IcSyncContext refuses to drain awaits that go through
            // TaskScheduler.Default (no thread pool on wasm-wasi).

            // Drain everything the transport has queued for the client:
            // handshake ack on the first poll after a handshake POST,
            // then render-batch / completion frames as CircuitHost
            // produces them. Empty body is fine — the App.razor fetch
            // wrapper retries silently so blazor.web.js never sees an
            // empty buffer in its _processHandshakeResponse path.
            var bytes = conn.DrainOutbound();

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.ContentLength = bytes.Length;
            // The IC HTTP gateway caches query AND update responses for
            // ~10 s by default. For long-polling, a cached response means
            // the boundary re-serves the same bytes — most fatally a
            // duplicate handshake ack on a later poll, which blazorpack
            // reads as a length-prefixed frame (0x7B = 123 → "Message is
            // incomplete"). Disable boundary caching so every poll hits
            // the canister and gets the freshly-drained queue contents.
            ctx.Response.Headers["Cache-Control"] = "no-store";
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
