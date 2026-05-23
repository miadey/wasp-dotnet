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
    public static void Register()
    {
        var asm = typeof(TetrisAssets).Assembly;
        var resourceNames = asm.GetManifestResourceNames();
        int registered = 0;
        foreach (var name in resourceNames)
        {
            if (!name.StartsWith("tetris/", StringComparison.Ordinal)) continue;
            var urlPath = "/" + name;        // → "/tetris/index.html"
            var contentType = ContentTypeFor(name);
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
                IcServer.RegisterStaticAsset(urlPath, bytes, contentType);
                IcCertifiedAssets.Insert(urlPath, bytes);
                registered++;
            }
            catch (Exception ex)
            {
                Wasp.IcCdk.Reply.Print("[tetris-asset] register " + urlPath + ": " + ex.Message);
            }
        }
        Wasp.IcCdk.Reply.Print($"[tetris] registered {registered} static assets");
        // /tetris (no trailing slash) → serve index.html. Same trick as
        // the rest of the app uses for its registered shells.
        try
        {
            using var s = asm.GetManifestResourceStream("tetris/index.html");
            if (s is not null)
            {
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var bytes = ms.ToArray();
                IcServer.RegisterStaticAsset("/tetris", bytes, "text/html; charset=utf-8");
                IcCertifiedAssets.Insert("/tetris", bytes);
                IcServer.RegisterStaticAsset("/tetris/", bytes, "text/html; charset=utf-8");
                IcCertifiedAssets.Insert("/tetris/", bytes);
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
