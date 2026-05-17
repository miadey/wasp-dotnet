using System;
using System.Text;

namespace Wasp.AspNetCore.Blazor.Server;

// Renders the Blazor server-component marker comment pair that
// blazor.web.js scans for to identify hydration anchors. Used from
// Razor pages via @((MarkupString)WaspMarkers.ServerStart(...)) so the
// markers WRAP the prerendered component's DOM (rather than being
// dropped at end-of-body, which prevents JS.RenderBatch edits from
// finding their target nodes).
//
// Shape (must match what BlazorMarkerMiddleware previously emitted —
// blazor.web.js's parser is strict on field names):
//
//   <!--Blazor:{"type":"server","sequence":0,"descriptor":"<base64>",
//               "prerenderId":"<id>","key":{"locationHash":"<id>"}}-->
//   <prerendered HTML>
//   <!--Blazor:{"prerenderId":"<id>"}-->
public static class WaspMarkers
{
    public static string ServerStart(
        string componentType,
        string componentAssembly,
        string prerenderId,
        string keyHash)
    {
        if (componentType is null) throw new ArgumentNullException(nameof(componentType));
        if (componentAssembly is null) throw new ArgumentNullException(nameof(componentAssembly));
        if (prerenderId is null) throw new ArgumentNullException(nameof(prerenderId));
        if (keyHash is null) throw new ArgumentNullException(nameof(keyHash));

        string descriptorJson =
            "{\"componentAssembly\":\"" + componentAssembly +
            "\",\"componentType\":\"" + componentType + "\"}";
        string descriptor = Convert.ToBase64String(Encoding.UTF8.GetBytes(descriptorJson));

        var sb = new StringBuilder(512);
        sb.Append("<!--Blazor:");
        sb.Append("{\"type\":\"server\",\"sequence\":0,\"descriptor\":\"");
        sb.Append(descriptor);
        sb.Append("\",\"prerenderId\":\"").Append(prerenderId);
        sb.Append("\",\"key\":{\"locationHash\":\"").Append(keyHash).Append("\"}}");
        sb.Append("-->");
        return sb.ToString();
    }

    public static string ServerEnd(string prerenderId)
    {
        if (prerenderId is null) throw new ArgumentNullException(nameof(prerenderId));
        return "<!--Blazor:{\"prerenderId\":\"" + prerenderId + "\"}-->";
    }
}
