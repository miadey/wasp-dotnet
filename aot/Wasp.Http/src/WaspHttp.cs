using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Wasp.IcCdk;

namespace Wasp.Http;

// Tiny HTTP router for canisters.
//
// Wires itself into the canister's exported `http_request` (query) and
// `http_request_update` (update) endpoints, decodes the IC HTTP gateway's
// Candid `HttpRequest`, dispatches to the user's registered handler by
// (method, path), encodes the user's `HttpResponse` back to Candid, and
// replies. No middleware, no DI, no Kestrel.
//
// Usage in user code:
//
//   public static partial class MyCanister
//   {
//       [System.Runtime.CompilerServices.ModuleInitializer]
//       internal static void RegisterRoutes()
//       {
//           WaspHttp.Get("/hello", _ => HttpResponse.Text("hi"));
//       }
//   }
//
// `MyCanister.csproj` must include
//   <UnmanagedEntryPointsAssembly Include="Wasp.Http" />
// so NativeAOT-LLVM picks up the [UnmanagedCallersOnly] thunks below.

public static class WaspHttp
{
    // route key = METHOD + " " + path; e.g. "GET /hello"
    private static readonly Dictionary<string, Func<HttpRequest, HttpResponse>> _routes = new();
    private static Func<HttpRequest, HttpResponse> _fallback = _ => HttpResponse.NotFound();
    private static bool _allRoutesRequireUpdate;

    public static void Get(string path, Func<HttpRequest, HttpResponse> handler)
        => Map("GET", path, handler);

    public static void Post(string path, Func<HttpRequest, HttpResponse> handler)
        => Map("POST", path, handler);

    public static void Put(string path, Func<HttpRequest, HttpResponse> handler)
        => Map("PUT", path, handler);

    public static void Delete(string path, Func<HttpRequest, HttpResponse> handler)
        => Map("DELETE", path, handler);

    public static void Map(string method, string path, Func<HttpRequest, HttpResponse> handler)
        => _routes[method.ToUpperInvariant() + " " + path] = handler;

    public static void Fallback(Func<HttpRequest, HttpResponse> handler)
        => _fallback = handler;

    /// <summary>
    /// If true, every query call returns <c>upgrade = true</c> so the IC
    /// gateway re-issues the request as an update call. Use this when
    /// every route must mutate state.
    /// </summary>
    public static void RequireUpdateForAll(bool value = true)
        => _allRoutesRequireUpdate = value;

    // ─── Canister entry-point thunks ─────────────────────────────────────
    [UnmanagedCallersOnly(EntryPoint = "canister_query__http_request")]
    public static void HttpRequestQuery() => Dispatch(isUpdate: false);

    [UnmanagedCallersOnly(EntryPoint = "canister_update__http_request_update")]
    public static void HttpRequestUpdate() => Dispatch(isUpdate: true);

    private static void Dispatch(bool isUpdate)
    {
        try
        {
            var arg = MessageContext.ArgData();
            var req = CandidHttp.DecodeRequest(arg);

            HttpResponse resp;
            if (!isUpdate && _allRoutesRequireUpdate)
            {
                resp = HttpResponse.Upgrading();
            }
            else
            {
                string key = req.Method.ToUpperInvariant() + " " + req.Path;
                var handler = _routes.TryGetValue(key, out var h) ? h : _fallback;
                resp = handler(req);
            }

            Reply.Bytes(CandidHttp.EncodeResponse(resp));
        }
        catch (Exception ex)
        {
            // Surface the failure as a 500 rather than trapping so the
            // IC gateway returns a debuggable response to the client.
            var msg = "Wasp.Http internal error: " + ex.Message;
            Reply.Print("[wasp.http] " + msg);
            try
            {
                Reply.Bytes(CandidHttp.EncodeResponse(HttpResponse.Text(msg, 500)));
            }
            catch
            {
                Reply.Trap(msg);
            }
        }
    }
}
