using System;
using System.Collections.Generic;

namespace Wasp.AspNetCore.Blazor.Wasp;

/// <summary>
/// Ambient per-event context the renderer's dispatcher sets just
/// before invoking a handler. Lets Razor handlers (which must satisfy
/// the EventCallback signature for <c>@onclick</c>) reach the form
/// values the bridge collected on the client side without us having
/// to invent a new directive syntax.
///
/// Usage in a Razor component:
/// <code>
///   &lt;button @@onclick="HandleSend"&gt;Send&lt;/button&gt;
///   @@code {
///       void HandleSend() {
///           var text = WaspContext.FormArgs["text"];
///           ChatService.Post(text);
///       }
///   }
/// </code>
///
/// IC canisters are single-threaded, so a static is correct here —
/// there is no other in-flight event during dispatch.
/// </summary>
public static class WaspContext
{
    private static IReadOnlyDictionary<string, string> _formArgs =
        new Dictionary<string, string>(0);

    private static IReadOnlyDictionary<string, string> _routeQuery =
        new Dictionary<string, string>(0);

    public static IReadOnlyDictionary<string, string> FormArgs => _formArgs;

    /// <summary>
    /// Parsed query-string args for the current render or event.
    /// e.g. <c>/chat?r=general&amp;n=42</c> → <c>{ ["r"]="general", ["n"]="42" }</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> RouteQuery => _routeQuery;

    public static IDisposable WithEvent(IReadOnlyDictionary<string, string> args)
    {
        var prev = _formArgs;
        _formArgs = args ?? new Dictionary<string, string>(0);
        return new Scope(() => _formArgs = prev);
    }

    public static IDisposable WithRouteQuery(IReadOnlyDictionary<string, string> query)
    {
        var prev = _routeQuery;
        _routeQuery = query ?? new Dictionary<string, string>(0);
        return new Scope(() => _routeQuery = prev);
    }

    private sealed class Scope : IDisposable
    {
        private readonly Action _onDispose;
        public Scope(Action onDispose) { _onDispose = onDispose; }
        public void Dispose() => _onDispose();
    }
}
