using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Wasp.AspNetCore.Blazor.Wasp;

/// <summary>
/// Stateless HTML renderer for the IC-native render-as-query model
/// (gh #118). Walks Blazor's render-tree output and emits plain HTML,
/// translating <c>@onclick</c>-style event handlers into
/// <c>data-wasp-evt-click="&lt;id&gt;"</c> attributes that the
/// <c>wasp.js</c> bridge intercepts.
///
/// Event-handler IDs are computed from the render-tree position
/// (componentId + frameIndex) and the attribute name — deterministic so
/// the client's stored id still matches after a fresh re-render.
///
/// Lifetime: ONE instance per render call. No state carries over between
/// calls; state lives in DI singletons / stable memory.
/// </summary>
public sealed class WaspHtmlRenderer : Renderer
{
    private static readonly Dispatcher _dispatcher = Dispatcher.CreateDefault();

    private readonly Dictionary<string, EventCallback> _handlers = new();
    private readonly IServiceProvider _services;

    public WaspHtmlRenderer(IServiceProvider services, ILoggerFactory loggerFactory)
        : base(services, loggerFactory)
    {
        _services = services;
    }

    public override Dispatcher Dispatcher => _dispatcher;

    protected override void HandleException(Exception exception)
    {
        // Surface to the canister log so a render-time crash is visible.
        try { global::Wasp.IcCdk.Reply.Print("[wasp-renderer] " + exception); }
        catch { /* best effort */ }
    }

    protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
    {
        // Render-as-query: we don't push diffs to a client. Instead the
        // caller queries the current state via SerializeToHtml after
        // RenderRootComponentAsync completes. The base class still emits
        // an UpdateDisplayAsync for each batch; ignore it.
        return Task.CompletedTask;
    }

    /// <summary>
    /// Render <typeparamref name="TComponent"/> at the given parameters and
    /// return its serialized HTML + the event-handler map.
    /// </summary>
    public (string html, IReadOnlyDictionary<string, EventCallback> handlers)
        RenderToHtml<TComponent>(ParameterView parameters)
        where TComponent : IComponent
    {
        var component = (TComponent)Activator.CreateInstance(typeof(TComponent))!;
        // Fill [Inject] properties from DI. The framework normally does
        // this via ComponentFactory; we replicate it via reflection so
        // we don't depend on internal types.
        InjectServices(component);
        int componentId = AssignRootComponentId(component);
        Dispatcher.InvokeAsync(() => RenderRootComponentAsync(componentId, parameters)).GetAwaiter().GetResult();
        var sb = new StringBuilder();
        WriteComponent(componentId, sb);
        return (sb.ToString(), _handlers);
    }

    /// <summary>
    /// Convenience overload: render a component type known only at
    /// runtime (for the dispatcher's per-route component selection).
    /// </summary>
    public (string html, IReadOnlyDictionary<string, EventCallback> handlers)
        RenderToHtml(Type componentType, ParameterView parameters)
    {
        if (!typeof(IComponent).IsAssignableFrom(componentType))
            throw new ArgumentException($"{componentType} does not implement IComponent");
        var component = (IComponent)Activator.CreateInstance(componentType)!;
        int componentId = AssignRootComponentId(component);
        Dispatcher.InvokeAsync(() => RenderRootComponentAsync(componentId, parameters)).GetAwaiter().GetResult();
        var sb = new StringBuilder();
        WriteComponent(componentId, sb);
        return (sb.ToString(), _handlers);
    }

    /// <summary>
    /// Set [Inject]-annotated properties on the component from the
    /// service provider. Mirrors what Blazor's internal
    /// <c>ComponentFactory.SetProperties</c> does.
    /// </summary>
    private void InjectServices(IComponent component)
    {
        var componentType = component.GetType();
        var props = componentType.GetProperties(
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic);
        foreach (var p in props)
        {
            var injectAttr = p.GetCustomAttributes(
                typeof(Microsoft.AspNetCore.Components.InjectAttribute), inherit: true);
            if (injectAttr.Length == 0) continue;
            var service = _services.GetService(p.PropertyType);
            if (service is not null)
            {
                p.SetValue(component, service);
            }
        }
    }

    private void WriteComponent(int componentId, StringBuilder sb)
    {
        var frames = GetCurrentRenderTreeFrames(componentId);
        WriteFrames(componentId, frames, 0, frames.Count, sb);
    }

    /// <summary>
    /// Walk a contiguous range of frames and emit HTML. Returns the
    /// index of the first frame NOT consumed (= position + ElementSubtreeLength
    /// for elements/components).
    /// </summary>
    private int WriteFrames(int componentId, ArrayRange<RenderTreeFrame> frames, int start, int end, StringBuilder sb)
    {
        int i = start;
        while (i < end)
        {
            ref var frame = ref frames.Array[i];
            switch (frame.FrameType)
            {
                case RenderTreeFrameType.Element:
                    i = WriteElement(componentId, frames, i, sb);
                    break;
                case RenderTreeFrameType.Text:
                    EscapeHtml(frame.TextContent, sb);
                    i++;
                    break;
                case RenderTreeFrameType.Markup:
                    sb.Append(frame.MarkupContent);
                    i++;
                    break;
                case RenderTreeFrameType.Component:
                    // Recurse into child component's render tree.
                    WriteComponent(frame.ComponentId, sb);
                    i += frame.ComponentSubtreeLength;
                    break;
                case RenderTreeFrameType.Region:
                    int regionEnd = i + frame.RegionSubtreeLength;
                    WriteFrames(componentId, frames, i + 1, regionEnd, sb);
                    i = regionEnd;
                    break;
                case RenderTreeFrameType.Attribute:
                case RenderTreeFrameType.ComponentReferenceCapture:
                case RenderTreeFrameType.ElementReferenceCapture:
                case RenderTreeFrameType.None:
                    // Attribute frames are consumed by WriteElement;
                    // reference-captures are no-ops in static HTML.
                    i++;
                    break;
                default:
                    i++;
                    break;
            }
        }
        return i;
    }

    private int WriteElement(int componentId, ArrayRange<RenderTreeFrame> frames, int start, StringBuilder sb)
    {
        ref var elementFrame = ref frames.Array[start];
        var tag = elementFrame.ElementName;
        int subtreeEnd = start + elementFrame.ElementSubtreeLength;

        sb.Append('<').Append(tag);

        // Walk attribute frames (start+1 onward until first non-Attribute).
        int j = start + 1;
        while (j < subtreeEnd)
        {
            ref var attr = ref frames.Array[j];
            if (attr.FrameType != RenderTreeFrameType.Attribute) break;
            WriteAttribute(componentId, j, ref attr, sb);
            j++;
        }

        // Self-closing void elements.
        if (IsVoidElement(tag))
        {
            sb.Append(" />");
            return subtreeEnd;
        }

        sb.Append('>');
        // Children.
        WriteFrames(componentId, frames, j, subtreeEnd, sb);
        sb.Append("</").Append(tag).Append('>');
        return subtreeEnd;
    }

    private void WriteAttribute(int componentId, int frameIndex, ref RenderTreeFrame attr, StringBuilder sb)
    {
        var name = attr.AttributeName;
        var value = attr.AttributeValue;

        // Event handler attributes (e.g. onclick) — translate to a
        // data-wasp-evt-* marker that wasp.js intercepts.
        if (value is EventCallback ec && name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
        {
            // e.g. "onclick" → "click"
            var evtName = name.Substring(2).ToLowerInvariant();
            // Deterministic id: hash of (componentId, frameIndex, name).
            // Fresh re-renders produce the same id provided the tree
            // shape didn't change.
            var idInput = System.Text.Encoding.UTF8.GetBytes(
                componentId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ":" + frameIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ":" + name);
            var hash = global::Wasp.WebSockets.Sha256.Hash(idInput);
            var id = BytesToHex(hash, 8);
            _handlers[id] = ec;
            sb.Append(' ').Append("data-wasp-evt-").Append(evtName).Append("=\"").Append(id).Append('"');
            return;
        }

        // Generic event delegate (less-typed than EventCallback). Same
        // treatment.
        if (value is MulticastDelegate del && name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
        {
            var evtName = name.Substring(2).ToLowerInvariant();
            var idInput = System.Text.Encoding.UTF8.GetBytes(
                componentId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ":" + frameIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ":" + name);
            var hash = global::Wasp.WebSockets.Sha256.Hash(idInput);
            var id = BytesToHex(hash, 8);
            // Wrap the delegate into an EventCallback so the public map
            // type stays uniform.
            _handlers[id] = EventCallback.Empty;  // store via separate map if needed
            // Store the raw delegate separately so DispatchEvent can find it.
            _rawHandlers[id] = del;
            sb.Append(' ').Append("data-wasp-evt-").Append(evtName).Append("=\"").Append(id).Append('"');
            return;
        }

        // Boolean false attribute → omit.
        if (value is bool b)
        {
            if (b) sb.Append(' ').Append(name);
            return;
        }

        // String / numeric / other — emit normally.
        if (value is null) return;
        sb.Append(' ').Append(name).Append("=\"");
        EscapeAttribute(value.ToString() ?? string.Empty, sb);
        sb.Append('"');
    }

    private readonly Dictionary<string, MulticastDelegate> _rawHandlers = new();

    /// <summary>Look up a handler by id (after a render call).</summary>
    public bool TryGetHandler(string id, out EventCallback ec, out MulticastDelegate? raw)
    {
        if (_handlers.TryGetValue(id, out ec) && _rawHandlers.TryGetValue(id, out var d))
        {
            raw = d;
            return true;
        }
        raw = null;
        return _handlers.TryGetValue(id, out ec);
    }

    private static string BytesToHex(byte[] bytes, int n)
    {
        var sb = new StringBuilder(n * 2);
        for (int i = 0; i < n && i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }

    private static void EscapeHtml(string s, StringBuilder sb)
    {
        foreach (var c in s)
        {
            switch (c)
            {
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '&': sb.Append("&amp;"); break;
                default: sb.Append(c); break;
            }
        }
    }

    private static void EscapeAttribute(string s, StringBuilder sb)
    {
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("&quot;"); break;
                case '<': sb.Append("&lt;"); break;
                case '&': sb.Append("&amp;"); break;
                default: sb.Append(c); break;
            }
        }
    }

    private static bool IsVoidElement(string tag) => tag switch
    {
        "area" or "base" or "br" or "col" or "embed" or "hr" or "img"
        or "input" or "link" or "meta" or "source" or "track" or "wbr" => true,
        _ => false,
    };
}
