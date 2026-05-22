using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wasp.AspNetCore.Blazor.Server;
using Xunit;

namespace Wasp.AspNetCore.Blazor.Server.Tests;

// End-to-end bidirectional pump tests for IcCircuitTransport.
// These exercise the seam Wasp.WebSockets <-> Blazor by driving the
// transport directly: HandleInbound stands in for WsHandlers.OnMessage and
// the captured `sent` list stands in for WaspWs.Send.
public sealed class IcCircuitTransportTests
{
    private static IcCircuitTransport NewTransport(out List<byte[]> sent)
    {
        var captured = new List<byte[]>();
        sent = captured;
        return new IcCircuitTransport("aaaaa-aa", b => captured.Add(b));
    }

    private static byte[] HandshakeFrame()
        => Encoding.UTF8.GetBytes("{\"protocol\":\"messagepack\",\"version\":1}\x1e");

    [Fact]
    public void Handshake_first_inbound_emits_brace_brace_separator_and_flips_Connected()
    {
        var t = NewTransport(out var sent);
        Assert.False(t.Connected);

        t.HandleInbound(HandshakeFrame());

        Assert.True(t.Connected);
        Assert.Single(sent);
        Assert.Equal(new byte[] { (byte)'{', (byte)'}', 0x1E }, sent[0]);
    }

    [Fact]
    public void Handshake_with_wrong_protocol_throws()
    {
        var t = NewTransport(out _);
        var bad = Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\x1e");
        Assert.Throws<FormatException>(() => t.HandleInbound(bad));
    }

    [Fact]
    public void Inbound_Invocation_after_handshake_raises_InboundMessage_with_target_and_args()
    {
        var t = NewTransport(out var sent);
        IcCircuitInboundMessage? received = null;
        t.InboundMessage += m => received = m;

        // Drive handshake.
        t.HandleInbound(HandshakeFrame());

        // Build an Invocation frame the canonical way (BlazorPackWriter is
        // already cross-validated against @microsoft/signalr-protocol-msgpack).
        var frame = BlazorPackWriter.WriteInvocation(
            "BeginInvokeDotNetFromJS",
            new object?[] { "call-1", "MyAsm", "MyMethod", (long)0, "[]" });

        t.HandleInbound(frame);

        Assert.NotNull(received);
        Assert.Equal("BeginInvokeDotNetFromJS", received!.Value.Target);
        Assert.Equal(5, received.Value.Args.Length);
        Assert.Equal("call-1", received.Value.Args[0]);
        Assert.Equal("MyAsm", received.Value.Args[1]);
        Assert.Equal("MyMethod", received.Value.Args[2]);
        Assert.Equal(0L, received.Value.Args[3]);
        Assert.Equal("[]", received.Value.Args[4]);
    }

    [Fact]
    public void Inbound_handshake_plus_invocation_in_single_payload_parses_both()
    {
        // ic-websocket-gateway may concatenate small WS frames into one
        // application payload. The transport must walk all of them.
        var t = NewTransport(out var sent);
        IcCircuitInboundMessage? received = null;
        t.InboundMessage += m => received = m;

        var handshake = HandshakeFrame();
        var inv = BlazorPackWriter.WriteInvocation("OnRenderCompleted", new object?[] { 42L, null });
        var both = new byte[handshake.Length + inv.Length];
        Buffer.BlockCopy(handshake, 0, both, 0, handshake.Length);
        Buffer.BlockCopy(inv, 0, both, handshake.Length, inv.Length);

        t.HandleInbound(both);

        Assert.True(t.Connected);
        Assert.Single(sent); // handshake ack only
        Assert.NotNull(received);
        Assert.Equal("OnRenderCompleted", received!.Value.Target);
        Assert.Equal(42L, received.Value.Args[0]);
        Assert.Null(received.Value.Args[1]);
    }

    [Fact]
    public void Inbound_two_invocations_one_payload_raises_event_twice()
    {
        var t = NewTransport(out _);
        var seen = new List<IcCircuitInboundMessage>();
        t.InboundMessage += m => seen.Add(m);

        t.HandleInbound(HandshakeFrame());

        var a = BlazorPackWriter.WriteInvocation("OnLocationChanged", new object?[] { "/a", null, false });
        var b = BlazorPackWriter.WriteInvocation("OnLocationChanged", new object?[] { "/b", "{}", true });
        var concat = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, concat, 0, a.Length);
        Buffer.BlockCopy(b, 0, concat, a.Length, b.Length);

        t.HandleInbound(concat);

        Assert.Equal(2, seen.Count);
        Assert.Equal("/a", seen[0].Args[0]);
        Assert.Equal("/b", seen[1].Args[0]);
    }

    [Fact]
    public void Outbound_SendCoreAsync_emits_canonical_BlazorPack_invocation()
    {
        var t = NewTransport(out var sent);
        t.HandleInbound(HandshakeFrame());                     // drain handshake ack
        sent.Clear();

        var task = t.SendCoreAsync(
            "JS.AttachComponent",
            new object?[] { 5, "selector" },
            CancellationToken.None);
        Assert.True(task.IsCompletedSuccessfully);

        Assert.Single(sent);

        // Byte-equal to BlazorPackWriter direct output (which is already
        // cross-validated against @microsoft/signalr-protocol-msgpack).
        var expected = BlazorPackWriter.WriteInvocation(
            "JS.AttachComponent", new object?[] { 5, "selector" });
        Assert.Equal(expected, sent[0]);
    }

    [Fact]
    public async Task InvokeCoreAsync_round_trips_string_result_via_Completion_frame()
    {
        var t = NewTransport(out var sent);
        t.HandleInbound(HandshakeFrame());
        sent.Clear();

        var task = t.InvokeCoreAsync<string>(
            "StartCircuit",
            new object?[] { "https://x", "https://x/", "rec", "state" },
            CancellationToken.None);

        Assert.False(task.IsCompleted);
        Assert.Single(sent);

        // Pull the invocationId out of the outbound bytes so the test
        // doesn't depend on the monotonic-id allocation strategy.
        var outboundInv = BlazorPackReader.ReadInvocation(sent[0]);
        Assert.NotNull(outboundInv.InvocationId);

        // Now simulate the server's Completion frame coming back inbound.
        var completion = BlazorPackWriter.WriteCompletion(outboundInv.InvocationId!, "circuit-secret-xyz");
        t.HandleInbound(completion);

        var result = await task;
        Assert.Equal("circuit-secret-xyz", result);
    }

    [Fact]
    public async Task InvokeCoreAsync_error_Completion_throws_to_caller()
    {
        var t = NewTransport(out var sent);
        t.HandleInbound(HandshakeFrame());
        sent.Clear();

        var task = t.InvokeCoreAsync<bool>(
            "ConnectCircuit", new object?[] { "secret" }, CancellationToken.None);

        var outboundInv = BlazorPackReader.ReadInvocation(sent[0]);
        var err = BlazorPackWriter.WriteCompletionError(outboundInv.InvocationId!, "bad-circuit");
        t.HandleInbound(err);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await task);
        Assert.Contains("bad-circuit", ex.Message);
    }

    [Fact]
    public void Inbound_Ping_is_acked_with_Ping()
    {
        var t = NewTransport(out var sent);
        t.HandleInbound(HandshakeFrame());
        sent.Clear();

        // Hand-roll a Ping frame: array(1) [type=6], 7-bit VLQ length=2.
        var ping = new byte[] { 0x02, 0x91, 0x06 };
        t.HandleInbound(ping);

        Assert.Single(sent);
        // Echo back the same Ping shape.
        Assert.Equal(new byte[] { 0x02, 0x91, 0x06 }, sent[0]);
    }

    [Fact]
    public void Inbound_Close_flips_Connected_false()
    {
        var t = NewTransport(out _);
        t.HandleInbound(HandshakeFrame());
        Assert.True(t.Connected);

        // Close frame: type=7. Array(1) [7].
        var close = new byte[] { 0x02, 0x91, 0x07 };
        t.HandleInbound(close);
        Assert.False(t.Connected);
    }

    [Fact]
    public void Frame_split_across_payloads_throws()
    {
        // S3 simplification: we assume Wasp.WebSockets delivers whole
        // payloads (matches the IC-WS protocol — each ws_message is one
        // application message). If that assumption breaks, the transport
        // raises a FormatException rather than silently corrupting state.
        var t = NewTransport(out _);
        t.HandleInbound(HandshakeFrame());

        var inv = BlazorPackWriter.WriteInvocation("PauseCircuit", Array.Empty<object?>());
        var half = new byte[inv.Length - 1];
        Buffer.BlockCopy(inv, 0, half, 0, half.Length);
        Assert.Throws<FormatException>(() => t.HandleInbound(half));
    }
}
