using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Routing;

namespace Wasp.AspNetCore.Blazor.Server;

/// <summary>
/// Drop-in replacement for <see cref="NavLink"/> that works on canister.
///
/// The framework <c>Microsoft.AspNetCore.Components.Routing.NavLink</c>
/// renders as a bare <c>&lt;a&gt;</c> with no <c>href</c> or <c>class</c>
/// attribute on wasm32-wasi NativeAOT-LLVM builds (gh #82). Root cause is
/// the framework's <c>ComponentProperties.SetProperties</c> reflection
/// path losing <c>[Parameter]</c> setter discoverability under aggressive
/// trim, so framework NavLink's <c>Href</c> / <c>ActiveClass</c> stay null
/// at <c>OnParametersSet</c> time.
///
/// This implementation sidesteps that pipeline by reading the parameters
/// in <see cref="OnParametersSet"/> using a hand-rolled
/// <see cref="SetParametersAsync"/> override and computing the active
/// state via <see cref="NavigationManager.Uri"/> directly. Same usage:
///
/// <code>
/// &lt;WaspNavLink href="counter" Match="NavLinkMatch.All" ActiveClass="active"&gt;
///     Counter
/// &lt;/WaspNavLink&gt;
/// </code>
///
/// Disposes its NavigationManager <c>LocationChanged</c> subscription on
/// <see cref="IDisposable.Dispose"/>.
/// </summary>
public sealed class WaspNavLink : ComponentBase, IDisposable
{
    private bool _isActive;
    private string? _hrefAbsolute;
    private string? _class;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    /// <summary>Target URL (relative to base href).</summary>
    [Parameter]
    public string? Href { get; set; }

    /// <summary>Match strategy: All = exact URL match, Prefix = current URL starts with Href.</summary>
    [Parameter]
    public NavLinkMatch Match { get; set; } = NavLinkMatch.Prefix;

    /// <summary>CSS class to add when this link's <see cref="Href"/> matches the current URL.</summary>
    [Parameter]
    public string ActiveClass { get; set; } = "active";

    /// <summary>Inner content (typically the link's visible text).</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <summary>Capture-all for <c>style=...</c>, <c>id=...</c>, etc.</summary>
    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    /// <summary>
    /// Custom SetParametersAsync that reads parameters directly from the
    /// ParameterView. The framework's default ComponentBase.SetParametersAsync
    /// delegates to ComponentProperties.SetProperties, which is the broken
    /// reflection-based binding path — by reading the values ourselves we
    /// avoid that path entirely.
    /// </summary>
    public override System.Threading.Tasks.Task SetParametersAsync(ParameterView parameters)
    {
        foreach (var p in parameters)
        {
            switch (p.Name)
            {
                case nameof(Href): Href = p.Value as string; break;
                case nameof(Match): Match = (NavLinkMatch)(p.Value ?? NavLinkMatch.Prefix); break;
                case nameof(ActiveClass): ActiveClass = (p.Value as string) ?? "active"; break;
                case nameof(ChildContent): ChildContent = p.Value as RenderFragment; break;
                default:
                    // CaptureUnmatchedValues equivalent — accumulate into a
                    // mutable dict so OnParametersSet can emit them.
                    _additional ??= new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    if (p.Value is not null) _additional[p.Name] = p.Value;
                    break;
            }
        }
        AdditionalAttributes = _additional;

        OnParametersSet();
        StateHasChanged();
        return System.Threading.Tasks.Task.CompletedTask;
    }

    private Dictionary<string, object>? _additional;

    protected override void OnInitialized()
    {
        NavigationManager.LocationChanged += OnLocationChanged;
    }

    protected override void OnParametersSet()
    {
        _hrefAbsolute = Href is null ? null : NavigationManager.ToAbsoluteUri(Href).AbsoluteUri;
        _isActive = ComputeIsActive();
        _class = _isActive ? ActiveClass : null;
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        var prev = _isActive;
        _isActive = ComputeIsActive();
        if (_isActive != prev)
        {
            _class = _isActive ? ActiveClass : null;
            StateHasChanged();
        }
    }

    private bool ComputeIsActive()
    {
        if (_hrefAbsolute is null) return false;
        var current = NavigationManager.Uri;
        return Match == NavLinkMatch.All
            ? UrlEquals(current, _hrefAbsolute)
            : UrlStartsWith(current, _hrefAbsolute);
    }

    private static bool UrlEquals(string current, string href)
    {
        // Normalize trailing slash + query/fragment so "/" matches "/?foo".
        var cur = TrimTrailingSlash(StripQueryFragment(current));
        var hr = TrimTrailingSlash(StripQueryFragment(href));
        return string.Equals(cur, hr, StringComparison.OrdinalIgnoreCase);
    }

    private static bool UrlStartsWith(string current, string href)
    {
        var cur = StripQueryFragment(current);
        var hr = StripQueryFragment(href);
        // Treat trailing slash as a boundary so "/counter" doesn't match
        // "/counter-2" but does match "/counter/" and "/counter".
        if (cur.StartsWith(hr, StringComparison.OrdinalIgnoreCase))
        {
            if (cur.Length == hr.Length) return true;
            var nextChar = cur[hr.Length];
            return nextChar is '/' or '?' or '#';
        }
        return false;
    }

    private static string StripQueryFragment(string url)
    {
        var iq = url.IndexOf('?');
        if (iq >= 0) url = url.Substring(0, iq);
        var ih = url.IndexOf('#');
        if (ih >= 0) url = url.Substring(0, ih);
        return url;
    }

    private static string TrimTrailingSlash(string url)
        => url.Length > 1 && url[^1] == '/' ? url.Substring(0, url.Length - 1) : url;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "a");
        if (AdditionalAttributes is not null)
        {
            builder.AddMultipleAttributes(1, AdditionalAttributes);
        }
        if (_class is not null)
        {
            builder.AddAttribute(2, "class", _class);
        }
        if (Href is not null)
        {
            builder.AddAttribute(3, "href", Href);
        }
        if (ChildContent is not null)
        {
            builder.AddContent(4, ChildContent);
        }
        builder.CloseElement();
    }

    public void Dispose()
    {
        NavigationManager.LocationChanged -= OnLocationChanged;
    }
}
