using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Wasp.AspNetCore.Blazor.Wasp;

/// <summary>
/// Bridge between <see cref="IWaspRenderer"/> (which the endpoint
/// layer talks to) and a stock <see cref="IComponent"/> root (which is
/// what the developer writes — vanilla Razor with <c>@onclick</c>, etc.).
///
/// Lifetime contract: one render call = one fresh
/// <see cref="WaspHtmlRenderer"/> + one fresh component instance. State
/// that survives across calls MUST live in DI singletons or stable
/// memory; transient component fields are NOT persisted.
/// </summary>
public sealed class WaspComponentRenderer<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TComponent> : IWaspRenderer
    where TComponent : IComponent
{
    private readonly IServiceProvider _services;
    private readonly ILoggerFactory _loggerFactory;

    public WaspComponentRenderer(IServiceProvider services)
    {
        _services = services;
        _loggerFactory = services.GetService<ILoggerFactory>()
                         ?? NullLoggerFactory.Instance;
    }

    public WaspRenderBatch Render(WaspRenderRequest req)
    {
        var renderer = new WaspHtmlRenderer(_services, _loggerFactory);
        var (html, _) = renderer.RenderToHtml<TComponent>(ParameterView.Empty);
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
        // Re-render to materialize the handler map, then invoke the
        // matching delegate. Handler IDs are deterministic w.r.t. the
        // render tree shape so the client's stored id still matches.
        var renderer = new WaspHtmlRenderer(_services, _loggerFactory);
        _ = renderer.RenderToHtml<TComponent>(ParameterView.Empty);
        if (renderer.TryGetHandler(req.HandlerId, out var ec, out var raw))
        {
            try
            {
                if (raw is Action a)
                {
                    a();
                }
                else if (raw is Func<System.Threading.Tasks.Task> f)
                {
                    f().GetAwaiter().GetResult();
                }
                else
                {
                    // EventCallback path — invoke with a synthetic empty
                    // EventArgs for now; v2 will deserialize from req.
                    var t = ec.InvokeAsync(EventArgs.Empty);
                    t.GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                try { global::Wasp.IcCdk.Reply.Print("[wasp-dispatch] " + ex); }
                catch { }
            }
        }
        // After the handler ran, re-render to pick up new state.
        return Render(new WaspRenderRequest { Path = req.Path });
    }

    private static string BytesToHex(byte[] bytes, int n)
    {
        var sb = new System.Text.StringBuilder(n * 2);
        for (int i = 0; i < n && i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }
}

internal sealed class NullLoggerFactory : ILoggerFactory
{
    public static readonly NullLoggerFactory Instance = new();
    public void AddProvider(ILoggerProvider provider) { }
    public ILogger CreateLogger(string categoryName) => NullLogger.Instance;
    public void Dispose() { }
}

internal sealed class NullLogger : ILogger
{
    public static readonly NullLogger Instance = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
}
