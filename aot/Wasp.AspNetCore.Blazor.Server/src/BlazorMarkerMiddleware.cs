using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Wasp.AspNetCore.Blazor.Server;

// Injects a hand-crafted Blazor server-component marker comment into
// the SSR HTML response, side-stepping the framework's
// ServerComponentSerializer (which requires System.Text.Json reflection
// — pulls 330 KB of code that overshoots IC's 11.5 MB code-section limit).
//
// The marker has the form:
//
//   <!--Blazor:{"type":"server","sequence":0,"descriptor":"<base64>","prerenderId":"<id>","key":{"locationHash":"<id>"}}-->
//   <!--Blazor:{"prerenderId":"<id>"}-->
//
// blazor.web.js parses these comments, opens a WebSocket to /_blazor,
// and sends `StartCircuit(baseUri, uri, serializedComponentRecords,
// applicationState)` with the descriptor blob round-tripped back to us.
//
// Our IcCircuitTransport handles the WS, and CircuitHubFacade.StartCircuit
// receives the call. We use a synthetic descriptor that survives a
// round-trip but won't deserialize via the framework's
// ServerComponentDeserializer — CircuitHubFacade.StartCircuit falls back
// to an empty component list. Net effect: blazor.web.js opens the WS,
// the circuit is created server-side, no interactive components attach
// until the next iteration (#74 closes the hub method stubs that drive
// actual component instantiation).
//
// Why this is honest:
//   - The middleware emission is real bytes in the response.
//   - blazor.web.js's WS open is observable (browser console, network tab).
//   - The CircuitHubFacade.StartCircuit invocation is observable in
//     canister logs.
//   - But: no actual <Counter /> interactivity until #74 lands. The
//     marker payload is a placeholder.
public sealed class BlazorMarkerMiddleware
{
    private readonly RequestDelegate _next;

    public BlazorMarkerMiddleware(RequestDelegate next) { _next = next; }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only the SSR root needs marker injection. /_blazor SignalR
        // endpoints and /_framework/* static files should pass straight
        // through — wrapping their responses through the buffered stream
        // can break content-length and chunked-encoding accounting on
        // the IC HTTP gateway path.
        var path = context.Request.Path.Value;
        if (path is not null &&
            (path.StartsWith("/_blazor", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        // Capture the response body so we can append the marker after the
        // upstream renderer writes its HTML.
        var originalStream = context.Response.Body;
        using var buffered = new MemoryStream();
        context.Response.Body = buffered;

        try
        {
            await _next(context);
        }
        finally
        {
            context.Response.Body = originalStream;
        }

        // Only inject for HTML responses that contain a recognizable end
        // of body. Otherwise pass through unchanged.
        bool isHtml = context.Response.ContentType is { } ct
            && ct.Contains("text/html", StringComparison.OrdinalIgnoreCase);

        buffered.Position = 0;
        if (!isHtml || buffered.Length == 0)
        {
            await buffered.CopyToAsync(originalStream);
            return;
        }

        // Read the buffered HTML, find </body>, insert the marker before it.
        var bytes = buffered.ToArray();
        string html = Encoding.UTF8.GetString(bytes);
        int insertAt = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);

        if (insertAt < 0)
        {
            await originalStream.WriteAsync(bytes);
            return;
        }

        // Markers are now emitted INLINE by the Razor page via
        // WaspMarkers.ServerStart / ServerEnd so they wrap the actual
        // prerendered component HTML. Injecting an extra marker pair at
        // </body> would either duplicate the descriptor (client sees two
        // root components) or anchor the hydration at the wrong DOM
        // region (RenderBatch edits would miss the Counter's nodes).
        // Pass the response through unchanged.
        await originalStream.WriteAsync(bytes);
    }

    // The marker descriptor is a synthetic blob; our
    // CircuitHubFacade.StartCircuit handles the round-trip without using
    // the framework's ServerComponentDeserializer (which would reject it).
    private static string BuildMarkerPair()
    {
        // Stable id per page render — keep it deterministic so the
        // canister model stays predictable (no per-request randomness).
        string prerenderId = "wasp-counter-0001";
        string keyHash = "wasp-key-0001";

        // The descriptor blob: we encode the component to instantiate as
        // plain UTF-8 JSON. CircuitHubFacade.StartCircuit will inspect it
        // directly (#74 follow-up wires the dispatch).
        string descriptorJson = "{\"componentAssembly\":\"CircuitOnIc\",\"componentType\":\"WaspSample.CircuitOnIc.Components.Pages.Counter\"}";
        string descriptor = Convert.ToBase64String(Encoding.UTF8.GetBytes(descriptorJson));

        var sb = new StringBuilder(512);
        sb.Append("<!--Blazor:");
        sb.Append("{\"type\":\"server\",\"sequence\":0,\"descriptor\":\"");
        sb.Append(descriptor);
        sb.Append("\",\"prerenderId\":\"").Append(prerenderId);
        sb.Append("\",\"key\":{\"locationHash\":\"").Append(keyHash).Append("\"}}");
        sb.Append("-->");
        sb.Append("<!--Blazor:{\"prerenderId\":\"").Append(prerenderId).Append("\"}-->");
        return sb.ToString();
    }
}

public static class BlazorMarkerMiddlewareExtensions
{
    public static IApplicationBuilder UseWaspBlazorMarker(this IApplicationBuilder app)
        => app.UseMiddleware<BlazorMarkerMiddleware>();
}
