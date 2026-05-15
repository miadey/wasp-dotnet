using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace WaspSample.RazorOnIc.Components;

// Hand-written component — no Razor compiler-generated BuildRenderTree.
// Used to isolate whether App.razor's generated render path is being trimmed
// vs an issue in the renderer itself.
public sealed class CodeOnlyHello : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "h1");
        builder.AddContent(1, "Hello from CodeOnlyHello (woven framework DLL test)");
        builder.CloseElement();
        builder.AddMarkupContent(2, "<p>Markup test: this should appear via AppendMarkup.</p>");
    }
}
