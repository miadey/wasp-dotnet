using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Wasp.Http;
using Wasp.IcCdk;

// Disambiguate the two HttpResponse types that are in scope:
//   Wasp.Http.HttpResponse  — IC Candid wire type
//   Microsoft.AspNetCore.Http.HttpResponse — ASP.NET Core response object
using IcHttpRequest = Wasp.Http.HttpRequest;
using IcHttpResponse = Wasp.Http.HttpResponse;
using AspHttpResponse = Microsoft.AspNetCore.Http.HttpResponse;

namespace Wasp.AspNetCore;

/// <summary>
/// IC canister implementation of <see cref="IServer"/>. Wires the IC HTTP
/// gateway's Candid-encoded <c>http_request</c> / <c>http_request_update</c>
/// exports through the full ASP.NET Core middleware pipeline.
///
/// Register via <see cref="HostingExtensions.UseIcCanister"/>.
/// </summary>
public sealed class IcServer : IServer
{
    // Set by StartAsync; read by the static Dispatch thunks.
    private static IcServer? _instance;

    private IHttpApplication<object>? _app;

    /// <summary>
    /// Optional hook for the consumer canister to surface why module init
    /// failed; if set, IcServer.Dispatch returns it as a 500 body so the
    /// failure is visible via curl instead of opaque trap.
    /// </summary>
    public static string? InitFailureMessage { get; set; }

    /// <inheritdoc />
    public IFeatureCollection Features { get; } = new FeatureCollection();

    /// <inheritdoc />
    public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken ct)
        where TContext : notnull
    {
        // IHttpApplication<TContext> is covariant in usage but the static
        // thunks need a concrete non-generic handle. We box it through the
        // object-typed adapter so we don't need to store the open-generic type.
        _app = new HttpApplicationAdapter<TContext>(application);
        _instance = this;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public void Dispose() { }

    // ─── Canister entry-point thunks ─────────────────────────────────────────

    [UnmanagedCallersOnly(EntryPoint = "canister_query__http_request")]
    public static void HttpRequestQuery() => Dispatch(isUpdate: false);

    [UnmanagedCallersOnly(EntryPoint = "canister_update__http_request_update")]
    public static void HttpRequestUpdate() => Dispatch(isUpdate: true);

    // ─── Core dispatch ────────────────────────────────────────────────────────

    private static void Dispatch(bool isUpdate)
    {
        try
        {
            // Always upgrade queries to update (must come before any other
            // path so even error states upgrade — non-certified query
            // responses get rejected by the IC gateway with "response
            // verification error", which surfaces as a 503 to the client).
            //
            // ASP.NET Core's pipeline easily blows past the 5B instruction
            // limit for queries. Per-route certified queries are M5 (#61).
            if (!isUpdate)
            {
                Reply.Bytes(CandidHttp.EncodeResponse(IcHttpResponse.Upgrading()));
                return;
            }

            if (_instance?._app is not { } app)
            {
                // Init failed — surface a 500 with InitFailureMessage if the
                // consumer set one, plus the full exception trace.
                var msg = "IcServer.Dispatch: app not registered.\n\n"
                    + (InitFailureMessage ?? "(no init failure recorded)");
                Reply.Bytes(CandidHttp.EncodeResponse(IcHttpResponse.Text(msg, 500)));
                return;
            }

            // 1. Read and decode the IC HTTP gateway Candid request.
            var arg = MessageContext.ArgData();
            var icReq = CandidHttp.DecodeRequest(arg);

            // 2. Build a DefaultHttpContext populated from the IC request.
            var httpCtx = BuildHttpContext(icReq, isUpdate);

            // 3. Create the ASP.NET Core context, process the request, and pump
            //    the task synchronously through IcSyncContext.
            //
            //    The Func<Task> overload is required: the SyncContext must be
            //    installed BEFORE ProcessRequestAsync starts, otherwise the first
            //    `await` inside the pipeline captures the caller's (null/default)
            //    context and continuations never reach our drain queue.
            var ctx = app.CreateContext(httpCtx.Features);
            Exception? processingException = null;
            try
            {
                IcSyncContext.RunUntilComplete(() => app.ProcessRequestAsync(ctx));
            }
            catch (Exception ex)
            {
                processingException = ex;
            }
            app.DisposeContext(ctx, processingException);
            if (processingException is not null) throw processingException;

            // 4. Translate the ASP.NET Core response back to an IC HttpResponse.
            var icResp = BuildIcResponse(httpCtx.Response);

            // 5. Reply with Candid-encoded HttpResponse.
            Reply.Bytes(CandidHttp.EncodeResponse(icResp));
        }
        catch (Exception ex)
        {
            // Surface failures as a 500 rather than trapping, mirroring
            // WaspHttp.Dispatch (WaspHttp.cs lines 73–108).
            var msg = "Wasp.AspNetCore internal error: " + ex.Message;
            Reply.Print("[wasp.aspnetcore] " + msg);
            try
            {
                Reply.Bytes(CandidHttp.EncodeResponse(IcHttpResponse.Text(msg, 500)));
            }
            catch
            {
                Reply.Trap(msg);
            }
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static DefaultHttpContext BuildHttpContext(IcHttpRequest icReq, bool isUpdate)
    {
        var httpCtx = new DefaultHttpContext();
        var req = httpCtx.Request;

        // Method
        req.Method = icReq.Method;

        // Scheme — IC always proxies over HTTPS from the boundary node perspective.
        req.Scheme = "https";

        // Path + query
        var url = icReq.Url;
        int qPos = url.IndexOf('?');
        if (qPos >= 0)
        {
            req.Path = new PathString(url.Substring(0, qPos));
            req.QueryString = new QueryString("?" + url.Substring(qPos + 1));
        }
        else
        {
            req.Path = new PathString(url);
            req.QueryString = QueryString.Empty;
        }

        // Headers
        foreach (var h in icReq.Headers)
        {
            req.Headers.Append(h.Key, h.Value);
        }

        // Body
        if (icReq.Body.Length > 0)
        {
            req.Body = new MemoryStream(icReq.Body, writable: false);
            req.ContentLength = icReq.Body.Length;
        }
        else
        {
            req.Body = Stream.Null;
            req.ContentLength = 0;
        }

        // Pre-size the response body stream so we can read it back after dispatch.
        httpCtx.Response.Body = new MemoryStream();

        return httpCtx;
    }

    private static IcHttpResponse BuildIcResponse(AspHttpResponse aspResp)
    {
        // Read the response body bytes.
        byte[] body = Array.Empty<byte>();
        if (aspResp.Body is MemoryStream ms)
        {
            body = ms.ToArray();
        }

        // Convert response headers to the IC flat key-value list.
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var kv in aspResp.Headers)
        {
            foreach (var value in kv.Value)
            {
                if (value is not null)
                    headers.Add(new KeyValuePair<string, string>(kv.Key, value));
            }
        }

        // Ensure content-length is present when the body is non-empty and the
        // app didn't set it (mirrors WaspHttp.HttpResponse.Text behaviour).
        if (body.Length > 0 && !aspResp.Headers.ContainsKey("content-length"))
        {
            headers.Add(new KeyValuePair<string, string>(
                "content-length",
                body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return new IcHttpResponse
        {
            StatusCode = (ushort)aspResp.StatusCode,
            Headers = headers,
            Body = body,
            // Do not set Upgrade here; ASP.NET Core apps that want upgrade
            // semantics should signal it via a dedicated middleware.
            // The IC gateway reads it from the Candid response.
            Upgrade = null,
        };
    }

    // ─── Internal adapter ─────────────────────────────────────────────────────

    // Erases the TContext generic so the static Dispatch thunks can hold a
    // single non-generic reference to the application.
    private sealed class HttpApplicationAdapter<TContext> : IHttpApplication<object>
        where TContext : notnull
    {
        private readonly IHttpApplication<TContext> _inner;
        public HttpApplicationAdapter(IHttpApplication<TContext> inner) => _inner = inner;

        public object CreateContext(IFeatureCollection contextFeatures)
            => _inner.CreateContext(contextFeatures)!;

        public Task ProcessRequestAsync(object context)
            => _inner.ProcessRequestAsync((TContext)context);

        public void DisposeContext(object context, Exception? exception)
            => _inner.DisposeContext((TContext)context, exception);
    }
}
