using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Wasp.AspNetCore;
using Wasp.IcCdk;

namespace Wasp.AspNetCore.Blazor.Wasp;

/// <summary>
/// Hosting extensions for the IC-native Blazor render-as-query
/// architecture (gh #118).
///
/// Wires two HTTP endpoints onto the canister:
///   GET  /_wasp/render  → canister_query  (v2-cert pass-through)
///   POST /_wasp/event   → canister_update (state mutations + new render)
///
/// Consumer's Program.cs:
/// <code>
/// builder.Services.AddSingleton&lt;IWaspRenderer, MyRenderer&gt;();
/// builder.UseInternetComputerWasp();
/// var app = builder.Build();
/// app.UseInternetComputerWasp();
/// app.StartAsync().GetAwaiter().GetResult();
/// </code>
/// </summary>
public static class WaspRenderEndpoints
{
    public static WebApplicationBuilder UseInternetComputerWasp(this WebApplicationBuilder builder)
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));
        builder.Services.UseIcDataProtection();
        builder.Services.AddLogging();
        // MapPost("/_wasp/event") needs routing services in the DI graph.
        builder.Services.AddRouting();
        builder.Services.AddSingleton<System.Text.Encodings.Web.HtmlEncoder>(
            System.Text.Encodings.Web.HtmlEncoder.Default);
        builder.Services.AddSingleton<System.Text.Encodings.Web.JavaScriptEncoder>(
            System.Text.Encodings.Web.JavaScriptEncoder.Default);
        builder.Services.AddSingleton<System.Text.Encodings.Web.UrlEncoder>(
            System.Text.Encodings.Web.UrlEncoder.Default);
        builder.WebHost.UseIcCanister();
        return builder;
    }

    public static WebApplication UseInternetComputerWasp(this WebApplication app)
    {
        if (app is null) throw new ArgumentNullException(nameof(app));
        var renderer = app.Services.GetRequiredService<IWaspRenderer>();

        // GET /_wasp/render — canister_query.
        // The renderer is pure: same inputs → same outputs. v2-cert
        // pass-through expression so canonical .icp0.io accepts the
        // response without body certification.
        IcResponseCertV2.RegisterPassThroughPath("/_wasp/render", "GET");
        IcServer.RegisterQueryHandler("/_wasp/render", (req) =>
        {
            if (req.Method != "GET") return null;
            try
            {
                var rr = new WaspRenderRequest
                {
                    Path = ExtractQueryParam(req.Url, "path") ?? "/",
                    QueryString = req.Url.Contains('?') ? req.Url.Substring(req.Url.IndexOf('?') + 1) : null,
                    LastBatchId = GetHeader(req, "if-none-match"),
                };
                var batch = renderer.Render(rr);
                // If the client's lastBatchId matches, return 304 — but
                // canister query responses don't carry status codes
                // directly; emit a tiny 304-equivalent body the client
                // recognizes.
                bool unchanged = !string.IsNullOrEmpty(rr.LastBatchId)
                              && string.Equals(batch.BatchId, rr.LastBatchId, StringComparison.Ordinal);
                var body = unchanged
                    ? System.Text.Encoding.UTF8.GetBytes("{\"unchanged\":true,\"batchId\":\"" + EscapeJson(batch.BatchId) + "\"}")
                    : EncodeBatch(batch);
                return (body, "application/json; charset=utf-8");
            }
            catch (Exception ex)
            {
                Reply.Print("[wasp-render] " + ex.GetType().Name + ": " + ex.Message);
                var err = "{\"error\":\"" + EscapeJson(ex.Message) + "\"}";
                return (System.Text.Encoding.UTF8.GetBytes(err), "application/json; charset=utf-8");
            }
        });

        // POST /_wasp/event — canister_update. Stays on update path
        // because handlers mutate state.
        app.MapPost("/_wasp/event", async (HttpContext ctx) =>
        {
            using var body = new System.IO.MemoryStream();
            await ctx.Request.Body.CopyToAsync(body);
            var bodyBytes = body.ToArray();
            var bodyJson = System.Text.Encoding.UTF8.GetString(bodyBytes);
            var er = new WaspEventRequest
            {
                Path = ExtractJsonString(bodyJson, "path") ?? "/",
                HandlerId = ExtractJsonString(bodyJson, "handlerId") ?? "",
                EventName = ExtractJsonString(bodyJson, "eventName") ?? "click",
                LastBatchId = ExtractJsonString(bodyJson, "lastBatchId"),
                Args = ExtractJsonStringMap(bodyJson, "args"),
            };
            try
            {
                var batch = renderer.DispatchEvent(er);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                var batchBytes = EncodeBatch(batch);
                ctx.Response.ContentLength = batchBytes.Length;
                await ctx.Response.Body.WriteAsync(batchBytes);
            }
            catch (Exception ex)
            {
                Reply.Print("[wasp-event] " + ex.GetType().Name + ": " + ex.Message);
                ctx.Response.StatusCode = 500;
                ctx.Response.ContentType = "application/json";
                var err = System.Text.Encoding.UTF8.GetBytes(
                    "{\"error\":\"" + EscapeJson(ex.Message) + "\"}");
                ctx.Response.ContentLength = err.Length;
                await ctx.Response.Body.WriteAsync(err);
            }
        }).DisableAntiforgery();

        // Bridge JS as a certified static asset on the query path.
        try
        {
            using var jsStream = typeof(WaspRenderEndpoints).Assembly
                .GetManifestResourceStream("Wasp.AspNetCore.Blazor.Wasp.wwwroot.wasp.js");
            if (jsStream is not null)
            {
                using var ms = new System.IO.MemoryStream();
                jsStream.CopyTo(ms);
                IcServer.RegisterStaticAsset(
                    "/_wasp/wasp.js",
                    ms.ToArray(),
                    "application/javascript; charset=utf-8");
            }
        }
        catch (Exception ex)
        {
            Reply.Print("[wasp-render] bridge js register: " + ex.Message);
        }

        return app;
    }

    private static byte[] EncodeBatch(WaspRenderBatch batch)
    {
        var sb = new StringBuilder(batch.Html.Length + 128);
        sb.Append("{\"batchId\":\"");
        sb.Append(EscapeJson(batch.BatchId));
        sb.Append("\",\"anchor\":\"");
        sb.Append(EscapeJson(batch.Anchor));
        sb.Append("\",\"html\":\"");
        sb.Append(EscapeJson(batch.Html));
        sb.Append("\"}");
        return System.Text.Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string EscapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            if (c == '\\') sb.Append("\\\\");
            else if (c == '"') sb.Append("\\\"");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string? ExtractQueryParam(string url, string name)
    {
        int q = url.IndexOf('?');
        if (q < 0) return null;
        var qs = url.Substring(q + 1);
        foreach (var part in qs.Split('&'))
        {
            int eq = part.IndexOf('=');
            if (eq < 0) continue;
            if (string.Equals(part.Substring(0, eq), name, StringComparison.Ordinal))
                return Uri.UnescapeDataString(part.Substring(eq + 1));
        }
        return null;
    }

    private static string? GetHeader(global::Wasp.Http.HttpRequest req, string name)
    {
        foreach (var h in req.Headers)
            if (h.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return h.Value;
        return null;
    }

    /// <summary>
    /// Extract a string→string map nested under "field":{...}. v1
    /// trimmed parser — only supports flat string values, no
    /// escaping beyond what the bridge produces.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ExtractJsonStringMap(string json, string field)
    {
        var dict = new Dictionary<string, string>();
        var marker = "\"" + field + "\":{";
        int i = json.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return dict;
        i += marker.Length;
        // Walk pairs "k":"v" until matching '}'.
        while (i < json.Length && json[i] != '}')
        {
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '"') break;
            i++;
            var keyStart = i;
            while (i < json.Length && json[i] != '"') i++;
            if (i >= json.Length) break;
            var key = json.Substring(keyStart, i - keyStart);
            i++; // closing "
            while (i < json.Length && (json[i] == ':' || char.IsWhiteSpace(json[i]))) i++;
            if (i >= json.Length || json[i] != '"') break;
            i++;
            var valStart = i;
            // Decode value until non-escaped closing quote.
            var sb = new StringBuilder();
            while (i < json.Length)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    char esc = json[i + 1];
                    if (esc == 'n') sb.Append('\n');
                    else if (esc == 'r') sb.Append('\r');
                    else if (esc == 't') sb.Append('\t');
                    else sb.Append(esc);
                    i += 2;
                    continue;
                }
                if (c == '"') break;
                sb.Append(c);
                i++;
            }
            if (i >= json.Length) break;
            dict[key] = sb.ToString();
            i++; // closing "
            while (i < json.Length && (json[i] == ',' || char.IsWhiteSpace(json[i]))) i++;
        }
        return dict;
    }

    /// <summary>
    /// Tiny JSON-string-field extractor. v1 only — replace with
    /// System.Text.Json once we confirm it's in the trim graph cheaply.
    /// </summary>
    private static string? ExtractJsonString(string json, string field)
    {
        var marker = "\"" + field + "\":\"";
        int i = json.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return null;
        i += marker.Length;
        int end = i;
        var sb = new StringBuilder();
        while (end < json.Length)
        {
            char c = json[end];
            if (c == '\\' && end + 1 < json.Length)
            {
                char esc = json[end + 1];
                if (esc == 'n') sb.Append('\n');
                else if (esc == 'r') sb.Append('\r');
                else if (esc == 't') sb.Append('\t');
                else sb.Append(esc);
                end += 2;
                continue;
            }
            if (c == '"') break;
            sb.Append(c);
            end++;
        }
        return sb.ToString();
    }
}
