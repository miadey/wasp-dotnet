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

    /// <summary>
    /// path → (body, content-type). When the consumer registers paths
    /// here at init, the query path serves them directly without
    /// upgrading — turns a ~3 s update-call round trip into a ~50 ms
    /// query. Responses are NOT certified, so the client must reach the
    /// canister via the IC HTTP gateway's `.raw.` subdomain
    /// (e.g. http://&lt;canister-id&gt;.raw.localhost:4944/) which skips
    /// response verification.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (byte[] Body, string ContentType)> _staticAssets =
        new(StringComparer.Ordinal);

    public static void RegisterStaticAsset(string path, byte[] body, string contentType = "application/octet-stream")
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        if (body is null) throw new ArgumentNullException(nameof(body));
        _staticAssets[path] = (body, contentType);
    }

    /// <summary>
    /// Synthesize a GET request through the ASP.NET pipeline now,
    /// capture the response, and register it as a static asset. Use
    /// for paths whose body doesn't change per request — most useful
    /// for the SSR HTML shell so the initial page load lands in
    /// ~100 ms instead of ~1500 ms (the update-call upgrade).
    ///
    /// Call AFTER <c>app.StartAsync</c> at canister init.
    /// </summary>
    public static void RegisterRenderedPath(string path)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        if (_instance?._app is not { } app)
        {
            Reply.Print($"[icserver] RegisterRenderedPath({path}) skipped — app not started yet");
            return;
        }

        var icReq = new IcHttpRequest
        {
            Method = "GET",
            Url = path,
            Headers = new[]
            {
                new KeyValuePair<string, string>("host", "wasp-canister"),
                new KeyValuePair<string, string>("user-agent", "wasp-prerender"),
            },
            Body = Array.Empty<byte>(),
        };

        var httpCtx = BuildHttpContext(icReq, isUpdate: true);
        var aspCtx = app.CreateContext(httpCtx.Features);
        Exception? pipelineException = null;
        try
        {
            IcSyncContext.RunUntilComplete(() => app.ProcessRequestAsync(aspCtx));
        }
        catch (Exception ex)
        {
            pipelineException = ex;
        }
        app.DisposeContext(aspCtx, pipelineException);

        if (pipelineException is not null)
        {
            Reply.Print($"[icserver] RegisterRenderedPath({path}) FAILED: {pipelineException.GetType().Name}: {pipelineException.Message}");
            return;
        }

        var icResp = BuildIcResponse(httpCtx, httpCtx.Response);
        if (icResp.StatusCode != 200)
        {
            Reply.Print($"[icserver] RegisterRenderedPath({path}) skipped — status {icResp.StatusCode}");
            return;
        }

        // Try to keep the response's own content-type header if the
        // pipeline set one; fall back to text/html for / and the
        // default for everything else.
        string contentType = "text/html; charset=utf-8";
        foreach (var h in icResp.Headers)
        {
            if (string.Equals(h.Key, "content-type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = h.Value;
                break;
            }
        }

        _staticAssets[path] = (icResp.Body, contentType);
        Reply.Print($"[icserver] RegisterRenderedPath({path}) OK — {icResp.Body.Length} bytes registered as static");
    }

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
            // Query fast path: GET requests for registered static assets
            // are served directly as a query response — no upgrade to an
            // update call. ~50 ms vs ~3 s round trip on local dfx.
            //
            // The response is NOT certified, so the IC HTTP gateway will
            // reject it on the standard `<canister>.localhost` /
            // `<canister>.ic0.app` subdomain. The consumer canister must
            // be accessed via the `.raw.` variant
            // (e.g. http://<canister>.raw.localhost:4944/) where the
            // gateway skips response verification.
            //
            // Anything else (POST, paths not in the static map, including
            // /_blazor/* and SSR routes) falls through to the upgrade
            // path. Per-route certified queries — for serving the same
            // assets on the standard subdomain too — land in #61 (M5).
            if (!isUpdate)
            {
                try
                {
                    var probeArg = MessageContext.ArgData();
                    var probeReq = CandidHttp.DecodeRequest(probeArg);
                    // Strip query string so `/?t=v35` matches `/` in the
                    // static-asset map. Cache-busting query params are
                    // ignored — the asset body is identical regardless.
                    int qIdx = probeReq.Url.IndexOf('?');
                    string lookupPath = qIdx >= 0 ? probeReq.Url.Substring(0, qIdx) : probeReq.Url;
                    if (probeReq.Method == "GET"
                        && _staticAssets.TryGetValue(lookupPath, out var asset))
                    {
                        Reply.Bytes(CandidHttp.EncodeResponse(new IcHttpResponse
                        {
                            StatusCode = 200,
                            Body = asset.Body,
                            Headers = new[]
                            {
                                new KeyValuePair<string, string>("content-type", asset.ContentType),
                                new KeyValuePair<string, string>("content-length", asset.Body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                                new KeyValuePair<string, string>("access-control-allow-origin", "*"),
                            },
                        }));
                        return;
                    }
                }
                catch { /* fall through to upgrade on any decode error */ }

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
            if (processingException is not null)
            {
                // Preserve the original stack trace rather than the bare
                // `throw processingException;` which resets it.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(processingException).Throw();
            }

            // 4. Translate the ASP.NET Core response back to an IC HttpResponse.
            var icResp = BuildIcResponse(httpCtx, httpCtx.Response);

            // 5. Reply with Candid-encoded HttpResponse.
            Reply.Bytes(CandidHttp.EncodeResponse(icResp));
        }
        catch (Exception ex)
        {
            // Surface failures as a 500 rather than trapping, mirroring
            // WaspHttp.Dispatch (WaspHttp.cs lines 73–108). Body includes
            // the full stack trace so middleware bugs are debuggable from
            // a single curl.
            var msg = "Wasp.AspNetCore internal error: " + ex.GetType().FullName + ": " + ex.Message
                + (ex.StackTrace is { } st ? "\n" + st : "");
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

        // RDG-generated body resolvers gate on IHttpRequestBodyDetectionFeature.
        // DefaultHttpContext doesn't auto-populate it from the headers, so JSON
        // body binding ends up in the "missing body" path and silently 400s.
        httpCtx.Features.Set<IHttpRequestBodyDetectionFeature>(
            new IcRequestBodyDetectionFeature(canHaveBody: icReq.Body.Length > 0));

        // Provide a single MemoryStream-backed IHttpResponseBodyFeature so
        // writes through Response.Body, Response.BodyWriter, and result writers
        // (Razor's HttpResponseStreamWriter, EndpointResponseBufferingFeature)
        // all converge on the same buffer. Use the framework's
        // StreamResponseBodyFeature so the PipeWriter behavior matches what
        // ASP.NET Core middleware expects.
        var responseBuffer = new MemoryStream();
        httpCtx.Features.Set<IHttpResponseBodyFeature>(new IcResponseBodyFeature(responseBuffer));
        // Track the buffer separately so BuildIcResponse can find it even if
        // a middleware/handler swaps the feature (e.g. Razor SSR's
        // EndpointResponseBufferingFeature does this for buffering output).
        httpCtx.Items["__icResponseBuffer"] = responseBuffer;

        return httpCtx;
    }

    private static IcHttpResponse BuildIcResponse(HttpContext httpCtx, AspHttpResponse aspResp)
    {
        // Read the response body bytes. The IcResponseBodyFeature we installed
        // in BuildHttpContext owns the MemoryStream; every write (via Body,
        // BodyWriter, or third-party result writers) lands there.
        byte[] body = Array.Empty<byte>();
        // Flush the current body feature's pipe writer first — a middleware
        // (e.g. Razor's EndpointResponseBufferingFeature) may have wrapped
        // our feature and the writes need to be drained before we read.
        try
        {
            httpCtx.Response.BodyWriter.FlushAsync().AsTask().GetAwaiter().GetResult();
        }
        catch { /* writer already completed — fine */ }
        // Our stable handle on the response buffer, established in
        // BuildHttpContext. Middlewares can swap the feature; the buffer
        // doesn't move.
        if (httpCtx.Items["__icResponseBuffer"] is MemoryStream buf && buf.Length > 0)
        {
            body = buf.ToArray();
        }
        else if (aspResp.Body is MemoryStream ms && ms.Length > 0)
        {
            body = ms.ToArray();
        }
        else if (aspResp.Body is Stream s && s.CanSeek && s.Length > 0)
        {
            s.Position = 0;
            using var copy = new MemoryStream();
            s.CopyTo(copy);
            body = copy.ToArray();
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

    private sealed class IcRequestBodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        public IcRequestBodyDetectionFeature(bool canHaveBody) => CanHaveBody = canHaveBody;
        public bool CanHaveBody { get; }
    }

    // MemoryStream-backed response body feature. PipeWriter is created with
    // StreamPipeWriterOptions so writes auto-flush to the underlying stream
    // on FlushAsync (the framework convention).
    internal sealed class IcResponseBodyFeature : IHttpResponseBodyFeature
    {
        public MemoryStream Buffer { get; }
        public Stream Stream { get; }
        public System.IO.Pipelines.PipeWriter Writer { get; }

        public IcResponseBodyFeature(MemoryStream buffer)
        {
            Buffer = buffer;
            Stream = buffer;
            Writer = System.IO.Pipelines.PipeWriter.Create(
                buffer,
                new System.IO.Pipelines.StreamPipeWriterOptions(leaveOpen: true));
        }

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendFileAsync(string path, long offset, long? count, CancellationToken ct = default)
            => throw new NotSupportedException("SendFile is not supported on canister.");
        public Task CompleteAsync()
        {
            Writer.Complete();
            return Task.CompletedTask;
        }
        public void DisableBuffering() { }
    }


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
