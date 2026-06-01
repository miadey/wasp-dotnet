using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Wasp.AspNetCore;

namespace WaspSample.BlazorWasp;

/// <summary>
/// Walks every embedded resource named "tetris/&lt;path&gt;" and
/// registers it as a static asset at "/tetris/&lt;path&gt;" plus the
/// matching v2-cert pass-through. The TetrisWasm Blazor WebAssembly
/// publish bundle ships into the canister via .csproj
/// EmbeddedResource entries; this class is the runtime half of that.
/// </summary>
public static class TetrisAssets
{
    // Content-Encoding: br header attached to every brotli-precompressed
    // asset. v1 certifies the body only, so this rides as an uncertified
    // pass-through header (same class as content-type/content-length).
    private static readonly IReadOnlyList<KeyValuePair<string, string>> BrHeader =
        new[] { new KeyValuePair<string, string>("content-encoding", "br") };

    public static void Register()
    {
        var asm = typeof(TetrisAssets).Assembly;
        var resourceNames = asm.GetManifestResourceNames();
        int registered = 0;
        foreach (var name in resourceNames)
        {
            if (!name.StartsWith("tetris/", StringComparison.Ordinal)) continue;
            // Resources are brotli-precompressed (".br"); serve the compressed
            // bytes AT the original path with Content-Encoding: br (the browser
            // decodes transparently). RegisterStaticAsset certifies the SAME
            // bytes we serve (v1 = body hash), so it verifies on icp0.io.
            bool isBr = name.EndsWith(".br", StringComparison.Ordinal);
            var logicalName = isBr ? name.Substring(0, name.Length - 3) : name;
            var urlPath = "/" + logicalName;        // → "/tetris/_framework/...wasm"
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
                Wasp.IcCdk.Reply.Print("[tetris-asset] " + name + ": " + ex.Message);
                continue;
            }
            try
            {
                IcServer.RegisterStaticAsset(urlPath, bytes, contentType, isBr ? BrHeader : null);
                registered++;
            }
            catch (Exception ex)
            {
                Wasp.IcCdk.Reply.Print("[tetris-asset] register " + urlPath + ": " + ex.Message);
            }
        }
        Wasp.IcCdk.Reply.Print($"[tetris] registered {registered} static assets");
        // /tetris (no trailing slash) → serve index.html (also brotli).
        try
        {
            using var s = asm.GetManifestResourceStream("tetris/index.html.br");
            if (s is not null)
            {
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var bytes = ms.ToArray();
                IcServer.RegisterStaticAsset("/tetris", bytes, "text/html; charset=utf-8", BrHeader);
                IcServer.RegisterStaticAsset("/tetris/", bytes, "text/html; charset=utf-8", BrHeader);
            }
        }
        catch { /* fallback path-only registration is best-effort */ }
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
