using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Wasp.AspNetCore.Blazor.Wasp;

/// <summary>
/// IWaspRenderer with path → component routing. Each request's path is
/// matched against a registered route table; the matching component is
/// rendered. Lets the same render-as-query plumbing host multi-page
/// apps (Home / Counter / Weather) without bringing in Blazor's stock
/// Router (which has gh #107 NRE issues on static SSR).
///
/// Routes are registered by component type via fluent API:
///   .AddRoute&lt;Home&gt;("/")
///   .AddRoute&lt;Counter&gt;("/counter")
///   .AddRoute&lt;Weather&gt;("/weather")
/// </summary>
public sealed class WaspRouter : IWaspRenderer
{
    private readonly IServiceProvider _services;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Dictionary<string, Type> _routes = new(StringComparer.OrdinalIgnoreCase);
    private Type? _notFoundComponent;
    private Func<string, string, string>? _shellWrap;

    public WaspRouter(IServiceProvider services)
    {
        _services = services;
        _loggerFactory = services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
    }

    public WaspRouter AddRoute<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TComponent>(string path)
        where TComponent : IComponent
    {
        _routes[NormalizePath(path)] = typeof(TComponent);
        return this;
    }

    public WaspRouter NotFound<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TComponent>()
        where TComponent : IComponent
    {
        _notFoundComponent = typeof(TComponent);
        return this;
    }

    /// <summary>
    /// Optional: wrap the rendered component HTML in a host shell
    /// (sidebar, layout chrome). Signature: (currentPath, innerHtml) → outer.
    /// </summary>
    public WaspRouter WrapShell(Func<string, string, string> wrap)
    {
        _shellWrap = wrap;
        return this;
    }

    public WaspRenderBatch Render(WaspRenderRequest req)
    {
        var normalized = NormalizePath(req.Path);
        var query = ParseQuery(req.Path, req.QueryString);
        string html;
        using (WaspContext.WithRouteQuery(query))
        {
            (html, _) = RenderPath(normalized);
        }
        // Hash includes query so different rooms ("/chat?r=general" vs
        // "/chat?r=random") get distinct batchIds and clients don't
        // dedupe across them via If-None-Match.
        var batchInput = System.Text.Encoding.UTF8.GetBytes(req.Path + "|" + html);
        var hash = global::Wasp.WebSockets.Sha256.Hash(batchInput);
        var batchId = BytesToHex(hash, 16);
        return new WaspRenderBatch
        {
            BatchId = batchId,
            Html = html,
            Anchor = "#wasp-root",
        };
    }

    public WaspRenderBatch DispatchEvent(WaspEventRequest req)
    {
        // Re-render to materialize the handler map, then invoke
        // matching delegate.
        var renderer = new WaspHtmlRenderer(_services, _loggerFactory);
        var componentType = ResolveType(req.Path);
        if (componentType is null)
        {
            return Render(new WaspRenderRequest { Path = req.Path });
        }
        var query = ParseQuery(req.Path, null);
        using (WaspContext.WithRouteQuery(query))
        {
            _ = renderer.RenderToHtml(componentType, ParameterView.Empty);
            if (renderer.TryGetHandler(req.HandlerId, out var ec, out var raw))
            {
                using (WaspContext.WithEvent(req.Args))
                {
                    try
                    {
                        InvokeHandler(raw, ec, req.Args);
                    }
                    catch (Exception ex)
                    {
                        try { global::Wasp.IcCdk.Reply.Print("[wasp-router-dispatch] " + ex); }
                        catch { }
                    }
                }
            }
        }
        return Render(new WaspRenderRequest { Path = req.Path });
    }

    private (string html, IReadOnlyDictionary<string, EventCallback> handlers) RenderPath(string path)
    {
        var componentType = ResolveType(path);
        if (componentType is null)
        {
            var notFound = "<h1>404 — Not Found</h1><p>No route matches " + System.Net.WebUtility.HtmlEncode(path) + "</p>";
            return (_shellWrap?.Invoke(path, notFound) ?? notFound, new Dictionary<string, EventCallback>());
        }
        var renderer = new WaspHtmlRenderer(_services, _loggerFactory);
        var (innerHtml, handlers) = renderer.RenderToHtml(componentType, ParameterView.Empty);
        var html = _shellWrap?.Invoke(path, innerHtml) ?? innerHtml;
        return (html, handlers);
    }

    private static IReadOnlyDictionary<string, string> ParseQuery(string? path, string? queryString)
    {
        // Prefer the embedded query in the path. The outer queryString
        // is the wrapper query of the framework endpoint (e.g.
        // /_wasp/render?path=/foo?c=4 hands us queryString="path=..."
        // which is not what the app cares about).
        string? raw = null;
        if (!string.IsNullOrEmpty(path))
        {
            var q = path.IndexOf('?');
            if (q >= 0) raw = path.Substring(q + 1);
        }
        if (string.IsNullOrEmpty(raw)) raw = queryString;
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(raw)) return dict;
        foreach (var part in raw.Split('&'))
        {
            if (string.IsNullOrEmpty(part)) continue;
            var eq = part.IndexOf('=');
            string key, value;
            if (eq < 0) { key = part; value = string.Empty; }
            else        { key = part.Substring(0, eq); value = part.Substring(eq + 1); }
            try { dict[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value); }
            catch { /* malformed query — skip */ }
        }
        return dict;
    }

    private Type? ResolveType(string path)
    {
        var p = NormalizePath(path);
        if (_routes.TryGetValue(p, out var t)) return t;
        return _notFoundComponent;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "/";
        int q = path.IndexOf('?');
        if (q >= 0) path = path.Substring(0, q);
        if (path.Length > 1 && path.EndsWith("/")) path = path.Substring(0, path.Length - 1);
        return path;
    }

    /// <summary>
    /// Invoke an event handler. Inspects the delegate's signature so the
    /// developer can write:
    ///   Action — no args (e.g. CounterService.Increment)
    ///   Action&lt;IDictionary&lt;string,string&gt;&gt; — receives form data
    ///   Action&lt;string&gt; — receives args["text"] if present
    ///   Func&lt;Task&gt; — awaited
    /// EventCallback is treated as Action — we lose strongly-typed args
    /// for now, the developer routes form data through the
    /// IDictionary overload instead.
    /// </summary>
    private static void InvokeHandler(MulticastDelegate? raw, EventCallback ec, IReadOnlyDictionary<string, string> args)
    {
        if (raw is Action a)
        {
            a();
            return;
        }
        if (raw is Func<System.Threading.Tasks.Task> ft)
        {
            ft().GetAwaiter().GetResult();
            return;
        }
        if (raw is Action<IReadOnlyDictionary<string, string>> aidict)
        {
            aidict(args);
            return;
        }
        if (raw is Action<IDictionary<string, string>> adict)
        {
            adict(new Dictionary<string, string>(args));
            return;
        }
        if (raw is Action<string> astr)
        {
            args.TryGetValue("text", out var t);
            astr(t ?? string.Empty);
            return;
        }
        if (raw is Action<Dictionary<string, string>> adictC)
        {
            adictC(new Dictionary<string, string>(args));
            return;
        }
        // Fallback to EventCallback dispatch with synthetic empty args.
        var t2 = ec.InvokeAsync(EventArgs.Empty);
        t2.GetAwaiter().GetResult();
    }

    private static string BytesToHex(byte[] bytes, int n)
    {
        var sb = new System.Text.StringBuilder(n * 2);
        for (int i = 0; i < n && i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }
}
