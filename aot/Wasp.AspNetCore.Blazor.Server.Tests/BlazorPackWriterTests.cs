using System;
using System.Collections.Generic;
using Wasp.AspNetCore.Blazor.Server;
using Xunit;

namespace Wasp.AspNetCore.Blazor.Server.Tests;

// Structural tests for BlazorPackWriter. These validate the byte layout
// against the msgpack spec + SignalR binary message format spec. They do
// NOT prove wire-compat against the live blazor.web.js reader -- that
// validation is deferred (see docs/m4-s2-circuit-coupling.md "Risks
// surfaced" #4). The natural cross-check is a node test using
// @microsoft/signalr's MessagePackHubProtocol.parseMessages -- TODO M4.S3.5.
public class BlazorPackWriterTests
{
    // ─── Length prefix (SignalR 7-bit VLQ) ───────────────────────────────

    [Theory]
    [InlineData(0,   new byte[] { 0x00 })]
    [InlineData(1,   new byte[] { 0x01 })]
    [InlineData(127, new byte[] { 0x7F })]
    [InlineData(128, new byte[] { 0x80, 0x01 })]
    [InlineData(255, new byte[] { 0xFF, 0x01 })]
    [InlineData(300, new byte[] { 0xAC, 0x02 })]
    [InlineData(16384,    new byte[] { 0x80, 0x80, 0x01 })]
    [InlineData(2097152,  new byte[] { 0x80, 0x80, 0x80, 0x01 })]
    public void LengthPrefix_VLQ(int len, byte[] expected)
    {
        Span<byte> buf = stackalloc byte[5];
        int n = BlazorPackWriter.WriteLengthPrefix(len, buf);
        Assert.Equal(expected, buf[..n].ToArray());
    }

    // ─── Outbound calls from the 6 known S2 call sites ───────────────────

    [Fact]
    public void JS_AttachComponent_layout_matches_spec()
    {
        // RemoteRenderer.cs ~88:  _client.SendAsync("JS.AttachComponent", componentId, domElementSelector);
        byte[] frame = BlazorPackWriter.WriteInvocation("JS.AttachComponent", new object?[] { 42, "#app" });

        int offset = 0;

        // [a] varint length prefix
        int bodyLen = ReadVarInt(frame, ref offset);
        Assert.Equal(frame.Length - offset, bodyLen);

        // [b] outer fixarray(6) = 0x96
        Assert.Equal(0x96, frame[offset++]);

        // [c] type = 1 (positive fixint)
        Assert.Equal(0x01, frame[offset++]);

        // [d] headers = empty fixmap = 0x80
        Assert.Equal(0x80, frame[offset++]);

        // [e] invocationId = nil = 0xC0
        Assert.Equal(0xC0, frame[offset++]);

        // [f] target = fixstr "JS.AttachComponent" (18 chars)
        Assert.Equal((byte)(0xA0 | 18), frame[offset++]);
        var target = System.Text.Encoding.UTF8.GetString(frame, offset, 18);
        Assert.Equal("JS.AttachComponent", target);
        offset += 18;

        // [g] args = fixarray(2)
        Assert.Equal(0x92, frame[offset++]);

        // [g.0] componentId = positive fixint 42
        Assert.Equal(0x2A, frame[offset++]);

        // [g.1] domElementSelector = fixstr "#app" (4 chars)
        Assert.Equal((byte)(0xA0 | 4), frame[offset++]);
        var sel = System.Text.Encoding.UTF8.GetString(frame, offset, 4);
        Assert.Equal("#app", sel);
        offset += 4;

        // [h] streamIds = empty fixarray = 0x90
        Assert.Equal(0x90, frame[offset++]);

        // exactly consumed
        Assert.Equal(frame.Length, offset);
    }

    [Fact]
    public void JS_RenderBatch_layout_matches_spec()
    {
        // RemoteRenderer.cs ~223:  await _client.SendAsync("JS.RenderBatch", pending.BatchId, segment);
        // BatchId is a long; segment is ArraySegment<byte>.
        long batchId = 7;
        byte[] payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        byte[] frame = BlazorPackWriter.WriteInvocation("JS.RenderBatch", new object?[] { batchId, new ArraySegment<byte>(payload) });

        int offset = 0;
        int bodyLen = ReadVarInt(frame, ref offset);
        Assert.Equal(frame.Length - offset, bodyLen);

        // outer array(6), type=1, headers={}, invocationId=nil
        Assert.Equal(0x96, frame[offset++]);
        Assert.Equal(0x01, frame[offset++]);
        Assert.Equal(0x80, frame[offset++]);
        Assert.Equal(0xC0, frame[offset++]);

        // target = fixstr "JS.RenderBatch" (14 chars)
        Assert.Equal((byte)(0xA0 | 14), frame[offset++]);
        Assert.Equal("JS.RenderBatch", System.Text.Encoding.UTF8.GetString(frame, offset, 14));
        offset += 14;

        // args = fixarray(2)
        Assert.Equal(0x92, frame[offset++]);

        // [arg0] long 7 -> positive fixint (since 7 <= 127)
        Assert.Equal(0x07, frame[offset++]);

        // [arg1] bin8(4) = 0xC4 0x04 <4 bytes>
        Assert.Equal(0xC4, frame[offset++]);
        Assert.Equal(0x04, frame[offset++]);
        Assert.Equal(payload, frame[offset..(offset + 4)]);
        offset += 4;

        // streamIds = []
        Assert.Equal(0x90, frame[offset++]);
        Assert.Equal(frame.Length, offset);
    }

    [Fact]
    public void JS_BeginInvokeJS_six_args_use_extended_fixarray_correctly()
    {
        // RemoteJSRuntime.cs:140 -- six positional args, all primitives.
        byte[] frame = BlazorPackWriter.WriteInvocation(
            "JS.BeginInvokeJS",
            new object?[] { 0L, "import", "[]", 1, 0L, 0 });

        int offset = 0;
        ReadVarInt(frame, ref offset);

        // outer fixarray(6), type=1, headers={}, invocationId=nil
        Assert.Equal(0x96, frame[offset++]);
        Assert.Equal(0x01, frame[offset++]);
        Assert.Equal(0x80, frame[offset++]);
        Assert.Equal(0xC0, frame[offset++]);

        // target string
        int tlen = frame[offset++] & 0x1F;
        Assert.Equal("JS.BeginInvokeJS", System.Text.Encoding.UTF8.GetString(frame, offset, tlen));
        offset += tlen;

        // args = fixarray(6)
        Assert.Equal(0x96, frame[offset++]);
    }

    [Fact]
    public void JS_ReceiveByteArray_binary_arg()
    {
        byte[] data = new byte[300];   // > 255 -> bin16
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i & 0xFF);

        byte[] frame = BlazorPackWriter.WriteInvocation("JS.ReceiveByteArray", new object?[] { 1, data });

        int offset = 0;
        ReadVarInt(frame, ref offset);

        // skip outer array + type + headers + invocationId
        offset += 4;
        // skip target string
        int tlen = frame[offset++] & 0x1F;
        offset += tlen;
        // args fixarray(2)
        offset++;
        // arg0: int 1 (positive fixint)
        Assert.Equal(0x01, frame[offset++]);
        // arg1: bin16 = 0xC5 <len:u16> <data...>
        Assert.Equal(0xC5, frame[offset++]);
        int blen = (frame[offset] << 8) | frame[offset + 1];
        Assert.Equal(300, blen);
        offset += 2;
        Assert.Equal(data, frame[offset..(offset + 300)]);
    }

    // ─── IcClientProxy integration ───────────────────────────────────────

    [Fact]
    public void IcClientProxy_pipes_bytes_to_sender()
    {
        var captured = new List<byte[]>();
        var proxy = new IcClientProxy(bytes => captured.Add(bytes));

        var task1 = proxy.SendCoreAsync("JS.AttachComponent", new object?[] { 1, "#root" });
        var task2 = proxy.SendCoreAsync("JS.RenderBatch", new object?[] { 1L, new byte[] { 0xAA } });

        Assert.True(task1.IsCompletedSuccessfully);
        Assert.True(task2.IsCompletedSuccessfully);
        Assert.Equal(2, captured.Count);

        // Both payloads must start with a non-zero varint (the body length).
        Assert.True(captured[0][0] > 0);
        Assert.True(captured[1][0] > 0);

        // Sender receives the COMPLETE framed bytes (length-prefix included).
        // Compare to a fresh encode for determinism.
        var ref1 = BlazorPackWriter.WriteInvocation("JS.AttachComponent", new object?[] { 1, "#root" });
        Assert.Equal(ref1, captured[0]);
    }

    [Fact]
    public async Task IcClientProxy_raw_send_constructor_rejects_InvokeCoreAsync()
    {
        // The raw-send constructor cannot correlate Completion frames; the
        // transport-backed constructor (used in production via
        // IcCircuitTransportRegistry) is the supported InvokeCoreAsync path.
        var proxy = new IcClientProxy(_ => { });
        await Assert.ThrowsAsync<NotSupportedException>(
            () => proxy.InvokeCoreAsync<bool>("ConnectCircuit", new object?[] { "abc" }));
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static int ReadVarInt(byte[] buf, ref int offset)
    {
        int value = 0;
        int shift = 0;
        for (;;)
        {
            byte b = buf[offset++];
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
            shift += 7;
            if (shift > 28) throw new FormatException("varint > 32 bits");
        }
    }
}
