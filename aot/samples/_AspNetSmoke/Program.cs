using System;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

// Issue #44 smoke test — does Microsoft.AspNetCore.App AOT-publish to
// wasm32-wasi at all? This sample is never deployed or invoked. The single
// exported thunk exists only to force the NativeAOT-LLVM compiler to keep
// WebApplication.CreateSlimBuilder + endpoint routing reachable, so we can
// measure trim warnings, module size, per-function instruction count, and
// WASI imports.

namespace WaspSample.AspNetSmoke;

public static class Smoke
{
    [UnmanagedCallersOnly(EntryPoint = "canister_query__smoke")]
    public static void SmokeAspNet()
    {
        var builder = WebApplication.CreateSlimBuilder();
        var app = builder.Build();
        app.MapGet("/", () => Results.Text("hi from AspNetSmoke"));
        GC.KeepAlive(app);
    }
}
