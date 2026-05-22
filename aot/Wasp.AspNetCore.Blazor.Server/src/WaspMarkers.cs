using System;
using System.Text;

namespace Wasp.AspNetCore.Blazor.Server;

// Renders the Blazor server-component marker comment(s) that
// blazor.web.js scans for to identify component placement. Used from
// Razor pages via @((MarkupString)WaspMarkers.Server(...)).
//
// Two shapes are supported:
//
//   1. Non-prerendered (used here): a SINGLE marker — no prerenderId,
//      no closing marker, no content. The client renders the whole
//      component from the first JS.RenderBatch the server sends.
//      Avoids the SSR ↔ hydration text-frame reconciliation that fails
//      without <!--bl:N--> boundary markers (which Wasp's
//      StaticHtmlRenderer doesn't emit).
//
//          <!--Blazor:{"type":"server","sequence":0,"descriptor":"<b64>",
//                      "key":{"locationHash":"<id>"}}-->
//
//   2. Prerendered (ServerPrerendered): an opening + closing comment
//      pair wrapping the prerendered DOM. Kept for completeness but
//      not currently used — hydration reconciliation requires per-
//      frame boundary markers we don't emit, so dynamic text frames
//      come out blank.
public static class WaspMarkers
{
    public static string Server(
        string componentType,
        string componentAssembly,
        string keyHash)
    {
        if (componentType is null) throw new ArgumentNullException(nameof(componentType));
        if (componentAssembly is null) throw new ArgumentNullException(nameof(componentAssembly));
        if (keyHash is null) throw new ArgumentNullException(nameof(keyHash));

        string descriptorJson =
            "{\"componentAssembly\":\"" + componentAssembly +
            "\",\"componentType\":\"" + componentType + "\"}";
        string descriptor = Convert.ToBase64String(Encoding.UTF8.GetBytes(descriptorJson));

        var sb = new StringBuilder(512);
        sb.Append("<!--Blazor:");
        sb.Append("{\"type\":\"server\",\"sequence\":0,\"descriptor\":\"");
        sb.Append(descriptor);
        sb.Append("\",\"key\":{\"locationHash\":\"").Append(keyHash).Append("\"}}");
        sb.Append("-->");
        return sb.ToString();
    }

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

    /// <summary>
    /// Convenience overload: ServerStart from a CLR Type. Defaults
    /// prerenderId/keyHash to type-name-based deterministic strings.
    /// </summary>
    public static string ServerStart(Type rootComponent, string? prerenderId = null)
    {
        if (rootComponent is null) throw new ArgumentNullException(nameof(rootComponent));
        prerenderId ??= "wasp-" + rootComponent.Name.ToLowerInvariant() + "-0001";
        return ServerStart(
            componentType: rootComponent.FullName ?? rootComponent.Name,
            componentAssembly: rootComponent.Assembly.GetName().Name ?? "unknown",
            prerenderId: prerenderId,
            keyHash: prerenderId + "-key");
    }

    /// <summary>
    /// Convenience overload paired with <see cref="ServerStart(Type, string?)"/>.
    /// </summary>
    public static string ServerEnd(Type rootComponent, string? prerenderId = null)
    {
        if (rootComponent is null) throw new ArgumentNullException(nameof(rootComponent));
        prerenderId ??= "wasp-" + rootComponent.Name.ToLowerInvariant() + "-0001";
        return ServerEnd(prerenderId);
    }

    /// <summary>
    /// The two &lt;script&gt; tags that bootstrap Blazor + the Wasp
    /// bridge. Drop in App.razor's body:
    /// <c>@((MarkupString)WaspMarkers.Scripts())</c>.
    /// </summary>
    public static string Scripts()
        => "<script src=\"/_framework/blazor.web.js\" autostart=\"false\"></script>" +
           "<script src=\"/_framework/wasp-bridge.js\"></script>";
}
