using System;
using Wasp.AspNetCore.Blazor.Server;
using Xunit;

namespace Wasp.AspNetCore.Blazor.Server.Tests;

// Round-trip writer -> reader for every (target, args) shape that ComponentHub
// (13 inbound) + RemoteRenderer/RemoteJSRuntime (6 outbound) actually send.
// Argument types come straight from docs/m4-s2-circuit-coupling.md.
//
// Validates the wire format end-to-end inside this assembly. Does NOT yet
// prove cross-compat with blazor.web.js's BlazorPack reader -- that requires
// the @microsoft/signalr-protocol-msgpack JS package round-trip and is
// tracked separately. But it eliminates encoder/decoder spec drift.
public class BlazorPackRoundTripTests
{
    private static void RoundTrip(string target, object?[] args, params Type[] expectedClrTypes)
    {
        byte[] frame = BlazorPackWriter.WriteInvocation(target, args);
        var decoded = BlazorPackReader.ReadInvocation(frame);

        Assert.Equal(1, decoded.Type);
        Assert.Null(decoded.InvocationId);
        Assert.Equal(target, decoded.Target);
        Assert.Equal(args.Length, decoded.Arguments.Length);

        for (int i = 0; i < args.Length; i++)
        {
            object? expected = args[i];
            object? actual = decoded.Arguments[i];

            // Numeric widening: writer encodes ints/longs minimally and reader
            // returns long. Compare via Convert.ToInt64 for the numeric subset.
            if (expected is int or long or short or sbyte or byte or ushort or uint)
            {
                Assert.Equal(Convert.ToInt64(expected), Convert.ToInt64(actual));
            }
            else if (expected is byte[] e && actual is byte[] a)
            {
                Assert.Equal(e, a);
            }
            else if (expected is ArraySegment<byte> seg && actual is byte[] ab)
            {
                Assert.Equal(seg.AsSpan().ToArray(), ab);
            }
            else
            {
                Assert.Equal(expected, actual);
            }

            if (i < expectedClrTypes.Length && actual is not null)
            {
                Assert.IsAssignableFrom(expectedClrTypes[i], actual);
            }
        }
    }

    // ─── 6 outbound JS.* calls (server -> browser) ───────────────────────

    [Fact] public void Outbound_JS_AttachComponent() =>
        RoundTrip("JS.AttachComponent", new object?[] { 7, "#root" });

    [Fact] public void Outbound_JS_RenderBatch() =>
        RoundTrip("JS.RenderBatch", new object?[] { 42L, new ArraySegment<byte>(new byte[] { 1, 2, 3, 4, 5 }) });

    [Fact] public void Outbound_JS_RenderBatch_large() =>
        RoundTrip("JS.RenderBatch", new object?[] { 1000000L, new byte[300] });

    [Fact] public void Outbound_JS_EndInvokeDotNet_success() =>
        RoundTrip("JS.EndInvokeDotNet", new object?[] { "call-abc", true, "{\"result\":42}" });

    [Fact] public void Outbound_JS_EndInvokeDotNet_failure() =>
        RoundTrip("JS.EndInvokeDotNet", new object?[] { "call-abc", false, "boom" });

    [Fact] public void Outbound_JS_ReceiveByteArray() =>
        RoundTrip("JS.ReceiveByteArray", new object?[] { 99, new byte[] { 0xAA, 0xBB, 0xCC } });

    [Fact] public void Outbound_JS_BeginInvokeJS() =>
        RoundTrip("JS.BeginInvokeJS", new object?[] { 5L, "console.log", "[\"hi\"]", 0, 0L, 0 });

    [Fact] public void Outbound_JS_BeginTransmitStream() =>
        RoundTrip("JS.BeginTransmitStream", new object?[] { 1234L });

    // ─── 13 inbound ComponentHub methods (browser -> canister) ───────────

    [Fact] public void Inbound_StartCircuit() =>
        RoundTrip("StartCircuit", new object?[] {
            "https://example.com/", "https://example.com/page",
            "<components-base64>", "<app-state-base64>" });

    [Fact] public void Inbound_UpdateRootComponents() =>
        RoundTrip("UpdateRootComponents", new object?[] { "<ops>", "<state>" });

    [Fact] public void Inbound_ConnectCircuit() =>
        RoundTrip("ConnectCircuit", new object?[] { "secret-xyz" });

    [Fact] public void Inbound_ResumeCircuit() =>
        RoundTrip("ResumeCircuit", new object?[] {
            "secret-xyz", "https://example.com/", "https://example.com/page",
            "<roots>", "<state>" });

    [Fact] public void Inbound_PauseCircuit() =>
        RoundTrip("PauseCircuit", Array.Empty<object?>());

    [Fact] public void Inbound_BeginInvokeDotNetFromJS() =>
        RoundTrip("BeginInvokeDotNetFromJS", new object?[] {
            "call-1", "MyAsm", "OnClick", 0L, "[]" });

    [Fact] public void Inbound_EndInvokeJSFromDotNet() =>
        RoundTrip("EndInvokeJSFromDotNet", new object?[] { 17L, true, "[42]" });

    [Fact] public void Inbound_ReceiveByteArray() =>
        RoundTrip("ReceiveByteArray", new object?[] { 1, new byte[] { 0xDE, 0xAD } });

    [Fact] public void Inbound_ReceiveJSDataChunk() =>
        RoundTrip("ReceiveJSDataChunk", new object?[] { 8L, 0L, new byte[] { 0xFF }, "" });

    [Fact] public void Inbound_OnRenderCompleted_no_error() =>
        RoundTrip("OnRenderCompleted", new object?[] { 100L, (string?)null });

    [Fact] public void Inbound_OnRenderCompleted_with_error() =>
        RoundTrip("OnRenderCompleted", new object?[] { 100L, "render failed" });

    [Fact] public void Inbound_OnLocationChanged() =>
        RoundTrip("OnLocationChanged", new object?[] { "https://example.com/x", (string?)null, true });

    [Fact] public void Inbound_OnLocationChanging() =>
        RoundTrip("OnLocationChanging", new object?[] { 5, "https://example.com/y", "<state>", false });

    // ─── Edge-case numeric encodings ─────────────────────────────────────

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(127L)]
    [InlineData(128L)]
    [InlineData(255L)]
    [InlineData(256L)]
    [InlineData(65535L)]
    [InlineData(65536L)]
    [InlineData(int.MaxValue)]
    [InlineData((long)int.MaxValue + 1)]
    [InlineData(long.MaxValue)]
    [InlineData(-1L)]
    [InlineData(-32L)]
    [InlineData(-33L)]
    [InlineData((long)sbyte.MinValue)]
    [InlineData((long)sbyte.MinValue - 1)]
    [InlineData((long)short.MinValue)]
    [InlineData((long)short.MinValue - 1)]
    [InlineData((long)int.MinValue)]
    [InlineData(long.MinValue)]
    public void NumericRoundTrip(long value) => RoundTrip("Test", new object?[] { value });

    [Theory]
    [InlineData(0)]   // empty fixstr
    [InlineData(1)]
    [InlineData(31)]  // fixstr max
    [InlineData(32)]  // str8
    [InlineData(255)]
    [InlineData(256)] // str16
    [InlineData(65535)]
    [InlineData(65536)] // str32
    public void StringLengthRoundTrip(int n)
    {
        var s = new string('x', n);
        RoundTrip("Test", new object?[] { s });
    }

    [Theory]
    [InlineData(0)]    // empty bin8
    [InlineData(255)]  // bin8 max
    [InlineData(256)]  // bin16
    [InlineData(65535)]
    [InlineData(65536)] // bin32
    public void BinaryLengthRoundTrip(int n)
    {
        var data = new byte[n];
        for (int i = 0; i < n; i++) data[i] = (byte)(i & 0xFF);
        RoundTrip("Test", new object?[] { data });
    }
}
