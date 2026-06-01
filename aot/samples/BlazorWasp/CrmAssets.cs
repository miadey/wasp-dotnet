using System;
using System.Collections.Generic;
using System.IO;
using Wasp.AspNetCore;

namespace WaspSample.BlazorWasp;

/// <summary>
/// Walks every embedded resource named "crm/&lt;path&gt;" and
/// registers it as a static asset at "/crm/&lt;path&gt;". Mirror of
/// <see cref="TetrisAssets"/>; the CrmWasm Blazor WebAssembly
/// publish output is embedded via BlazorWasp.csproj.
/// </summary>
public static class CrmAssets
{
    // See TetrisAssets.BrHeader — uncertified Content-Encoding: br pass-through.
    private static readonly IReadOnlyList<KeyValuePair<string, string>> BrHeader =
        new[] { new KeyValuePair<string, string>("content-encoding", "br") };

    public static void Register()
    {
        var asm = typeof(CrmAssets).Assembly;
        int registered = 0;
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith("crm/", StringComparison.Ordinal)) continue;
            // brotli-precompressed (".br"); serve at the stripped path with
            // Content-Encoding: br, certifying the same compressed bytes (v1).
            bool isBr = name.EndsWith(".br", StringComparison.Ordinal);
            var logicalName = isBr ? name.Substring(0, name.Length - 3) : name;
            var urlPath = "/" + logicalName;
            var contentType = ContentTypeFor(logicalName);
            byte[] bytes;
            try
            {
                using var s = asm.GetManifestResourceStream(name)!;
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                bytes = ms.ToArray();
            }
            catch (Exception ex)
            {
                Wasp.IcCdk.Reply.Print("[crm-asset] " + name + ": " + ex.Message);
                continue;
            }
            try
            {
                IcServer.RegisterStaticAsset(urlPath, bytes, contentType, isBr ? BrHeader : null);
                registered++;
            }
            catch (Exception ex)
            {
                Wasp.IcCdk.Reply.Print("[crm-asset] register " + urlPath + ": " + ex.Message);
            }
        }
        Wasp.IcCdk.Reply.Print($"[crm] registered {registered} static assets");
        // /crm (no trailing slash) → serve index.html (also brotli).
        try
        {
            using var s = asm.GetManifestResourceStream("crm/index.html.br");
            if (s is not null)
            {
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var bytes = ms.ToArray();
                IcServer.RegisterStaticAsset("/crm", bytes, "text/html; charset=utf-8", BrHeader);
                IcServer.RegisterStaticAsset("/crm/", bytes, "text/html; charset=utf-8", BrHeader);
            }
        }
        catch { }
    }

    private static string ContentTypeFor(string path)
    {
        var dot = path.LastIndexOf('.');
        var ext = dot >= 0 ? path.Substring(dot).ToLowerInvariant() : "";
        return ext switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css"  => "text/css; charset=utf-8",
            ".js"   => "application/javascript; charset=utf-8",
            ".mjs"  => "application/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".wasm" => "application/wasm",
            ".dat"  => "application/octet-stream",
            ".pdb"  => "application/octet-stream",
            ".svg"  => "image/svg+xml",
            ".png"  => "image/png",
            ".webp" => "image/webp",
            ".woff" => "font/woff",
            ".woff2"=> "font/woff2",
            ".ttf"  => "font/ttf",
            ".ico"  => "image/x-icon",
            _       => "application/octet-stream",
        };
    }
}
