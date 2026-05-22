using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Wasp.AspNetCore.Blazor.Server;

// Emits a JSON file with the BlazorPack-encoded byte vectors that the
// node-side validate.mjs decodes via @microsoft/signalr-protocol-msgpack.
// Run: dotnet run --project Emit -- vectors.json

if (args.Length != 1) { Console.Error.WriteLine("usage: emit <out-path>"); return 1; }

var cases = new (string name, string target, object?[] callArgs)[]
{
    ("attach",        "JS.AttachComponent",  new object?[] { 7, "#root" }),
    ("renderBatch",   "JS.RenderBatch",      new object?[] { 42L, new byte[] { 1, 2, 3, 4, 5 } }),
    ("endInvokeOK",   "JS.EndInvokeDotNet",  new object?[] { "call-1", true, "{\"r\":1}" }),
    ("endInvokeErr",  "JS.EndInvokeDotNet",  new object?[] { "call-1", false, "boom" }),
    ("recvBytes",     "JS.ReceiveByteArray", new object?[] { 9, new byte[] { 0xAA, 0xBB, 0xCC } }),
    ("beginJS",       "JS.BeginInvokeJS",    new object?[] { 5L, "console.log", "[\"hi\"]", 0, 0L, 0 }),
    ("beginStream",   "JS.BeginTransmitStream", new object?[] { 1234L }),

    ("startCircuit",  "StartCircuit",
        new object?[] { "https://x/", "https://x/p", "<components>", "<state>" }),
    ("beginDotNet",   "BeginInvokeDotNetFromJS",
        new object?[] { "call-1", "MyAsm", "OnClick", 0L, "[]" }),
    ("renderDone",    "OnRenderCompleted",   new object?[] { 100L, (string?)null }),
    ("locChanged",    "OnLocationChanged",   new object?[] { "https://x/y", (string?)null, true }),
};

var arr = new object[cases.Length];
for (int i = 0; i < cases.Length; i++)
{
    var (name, target, callArgs) = cases[i];
    byte[] frame = BlazorPackWriter.WriteInvocation(target, callArgs);

    var jsonArgs = new object?[callArgs.Length];
    for (int j = 0; j < callArgs.Length; j++)
    {
        jsonArgs[j] = callArgs[j] switch
        {
            byte[] b           => new Dictionary<string, object> { ["__bin__"] = Convert.ToBase64String(b) },
            ArraySegment<byte> s => new Dictionary<string, object> { ["__bin__"] = Convert.ToBase64String(s.AsSpan()) },
            long l             => new Dictionary<string, object> { ["__long__"] = l.ToString() },
            _ => callArgs[j],
        };
    }

    arr[i] = new
    {
        name,
        target,
        argsExpected = jsonArgs,
        frameHex = Convert.ToHexString(frame),
    };
}

File.WriteAllText(args[0], JsonSerializer.Serialize(arr, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"wrote {cases.Length} vectors to {args[0]}");
return 0;
