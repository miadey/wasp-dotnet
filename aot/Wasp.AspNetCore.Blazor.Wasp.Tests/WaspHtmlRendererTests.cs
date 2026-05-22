using System.Collections.Generic;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wasp.AspNetCore.Blazor.Wasp;
using Xunit;

public class WaspHtmlRendererTests
{
    private static WaspHtmlRenderer NewRenderer(IServiceProvider? services = null)
    {
        services ??= new ServiceCollection().BuildServiceProvider();
        return new WaspHtmlRenderer(services, NullLoggerFactory.Instance);
    }

    private sealed class PlainElement : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "h1");
            builder.AddContent(1, "Hello");
            builder.CloseElement();
        }
    }

    [Fact]
    public void RenderToHtml_emits_plain_element()
    {
        using var r = NewRenderer();
        var (html, _) = r.RenderToHtml<PlainElement>(ParameterView.Empty);
        Assert.Equal("<h1>Hello</h1>", html);
    }

    private sealed class WithClick : ComponentBase
    {
        public bool WasClicked;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "button");
            builder.AddAttribute(1, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => { WasClicked = true; }));
            builder.AddContent(2, "Click me");
            builder.CloseElement();
        }
    }

    [Fact]
    public void RenderToHtml_translates_onclick_to_data_wasp_evt_click()
    {
        using var r = NewRenderer();
        var (html, handlers) = r.RenderToHtml<WithClick>(ParameterView.Empty);
        Assert.Contains("data-wasp-evt-click=\"", html);
        Assert.DoesNotContain("onclick=", html);
        Assert.Single(handlers);
    }

    [Fact]
    public void Handler_ids_are_deterministic_across_two_renders()
    {
        using var r1 = NewRenderer();
        using var r2 = NewRenderer();
        var (h1, _) = r1.RenderToHtml<WithClick>(ParameterView.Empty);
        var (h2, _) = r2.RenderToHtml<WithClick>(ParameterView.Empty);
        // Extract the id from each.
        var id1 = ExtractId(h1);
        var id2 = ExtractId(h2);
        Assert.NotEmpty(id1);
        Assert.Equal(id1, id2);
    }

    private sealed class TestService { public int Value = 42; }

    private sealed class WithInjection : ComponentBase
    {
        [Inject] internal TestService Svc { get; set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "span");
            builder.AddContent(1, Svc.Value.ToString());
            builder.CloseElement();
        }
    }

    [Fact]
    public void Inject_property_is_filled_from_service_provider()
    {
        var services = new ServiceCollection()
            .AddSingleton<TestService>()
            .BuildServiceProvider();
        using var r = NewRenderer(services);
        var (html, _) = r.RenderToHtml<WithInjection>(ParameterView.Empty);
        Assert.Equal("<span>42</span>", html);
    }

    private static string ExtractId(string html)
    {
        const string marker = "data-wasp-evt-click=\"";
        int i = html.IndexOf(marker);
        if (i < 0) return string.Empty;
        i += marker.Length;
        int j = html.IndexOf('"', i);
        return j < 0 ? string.Empty : html.Substring(i, j - i);
    }
}
