using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Wasp.AspNetCore.Blazor.Wasp;
using Xunit;

public class WaspRouterTests
{
    private sealed class HomePage : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "h1");
            builder.AddContent(1, "Home");
            builder.CloseElement();
        }
    }

    private sealed class CounterPage : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "h1");
            builder.AddContent(1, "Counter");
            builder.CloseElement();
        }
    }

    private sealed class NotFoundPage : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "h1");
            builder.AddContent(1, "Not Found");
            builder.CloseElement();
        }
    }

    private static WaspRouter NewRouter()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new WaspRouter(services)
            .AddRoute<HomePage>("/")
            .AddRoute<CounterPage>("/counter")
            .NotFound<NotFoundPage>();
    }

    [Fact]
    public void Route_root_renders_home()
    {
        var batch = NewRouter().Render(new WaspRenderRequest { Path = "/" });
        Assert.Contains("<h1>Home</h1>", batch.Html);
    }

    [Fact]
    public void Route_counter_renders_counter()
    {
        var batch = NewRouter().Render(new WaspRenderRequest { Path = "/counter" });
        Assert.Contains("<h1>Counter</h1>", batch.Html);
    }

    [Fact]
    public void Unknown_path_renders_NotFound_component()
    {
        var batch = NewRouter().Render(new WaspRenderRequest { Path = "/nowhere" });
        Assert.Contains("<h1>Not Found</h1>", batch.Html);
    }

    [Fact]
    public void BatchId_is_stable_across_two_renders_of_same_state()
    {
        var router = NewRouter();
        var a = router.Render(new WaspRenderRequest { Path = "/" });
        var b = router.Render(new WaspRenderRequest { Path = "/" });
        Assert.Equal(a.BatchId, b.BatchId);
    }

    [Fact]
    public void BatchId_differs_between_paths()
    {
        var router = NewRouter();
        var a = router.Render(new WaspRenderRequest { Path = "/" });
        var b = router.Render(new WaspRenderRequest { Path = "/counter" });
        Assert.NotEqual(a.BatchId, b.BatchId);
    }

    [Fact]
    public void Path_normalisation_strips_trailing_slash_and_query()
    {
        var router = NewRouter();
        var a = router.Render(new WaspRenderRequest { Path = "/counter" });
        var b = router.Render(new WaspRenderRequest { Path = "/counter/" });
        var c = router.Render(new WaspRenderRequest { Path = "/counter?x=1" });
        Assert.Equal(a.BatchId, b.BatchId);
        Assert.Equal(a.BatchId, c.BatchId);
    }

    [Fact]
    public void WrapShell_is_invoked_with_inner_html_and_path()
    {
        var router = NewRouter().WrapShell((path, inner) => $"<wrap path=\"{path}\">{inner}</wrap>");
        var batch = router.Render(new WaspRenderRequest { Path = "/counter" });
        Assert.StartsWith("<wrap path=\"/counter\">", batch.Html);
        Assert.Contains("<h1>Counter</h1>", batch.Html);
        Assert.EndsWith("</wrap>", batch.Html);
    }
}
