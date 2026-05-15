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
        // Test layout-bug hypothesis:
        //   Markup uses MarkupContentField (offset 16, same slot as
        //   ElementNameField). If markup renders correctly but elements
        //   render with empty names, the struct's ref-field layout is
        //   broken in *one direction* (write or read).
        builder.AddMarkupContent(0, "<p>markup test: should appear if string fields work</p>");
        builder.OpenElement(1, "h1");
        builder.AddContent(2, "text inside h1");
        builder.CloseElement();
    }
}
