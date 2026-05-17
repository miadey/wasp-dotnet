using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Wasp.AspNetCore.Blazor.Server;

// Concrete IIcCircuitTransport that wires a single client principal between
// Wasp.WebSockets and a Blazor CircuitHost.
//
// Inbound: HandleInbound(payload) is called by IcCircuitTransportRegistry
// when WsHandlers.OnMessage fires for this principal. Lifecycle:
//   1. SignalR handshake (first frame = UTF-8 JSON terminated by 0x1E) →
//      ack with `{}\x1e`, flip _handshakeComplete = true.
//   2. Subsequent frames are BlazorPack length-prefixed messages. Dispatch
//      by HubMessageType:
//        1 Invocation     → raise InboundMessage(target, args)
//        3 Completion     → complete TCS in _pendingInvocations (S3.B)
//        6 Ping           → ack with PingFrame (mirror)
//        7 Close          → flip Connected = false
//
// Outbound: SendCoreAsync builds the Invocation frame via BlazorPackWriter
// (already cross-validated against @microsoft/signalr-protocol-msgpack —
// 11/11 vectors decoded) and hands the bytes to the configured sender
// delegate (in a real canister, `WaspWs.Send(principal, bytes)`).
//
// This is intentionally a thin layer: no CircuitHost coupling here. The S4
// source-gen dispatch table will subscribe to InboundMessage and route
// (target, args) into the generated method-table switch.
public sealed class IcCircuitTransport : IIcCircuitTransport
{
    private static readonly byte[] HandshakeAck = new byte[] { (byte)'{', (byte)'}', 0x1E };

    private readonly Action<byte[]> _send;
    private readonly ConcurrentDictionary<string, IInvocationCompletion> _pendingInvocations = new();
    private long _nextInvocationId;
    private bool _handshakeComplete;
    private bool _connected;
    private readonly string _connectionId;

    public IcCircuitTransport(string connectionId, Action<byte[]> send)
    {
        _connectionId = connectionId ?? throw new ArgumentNullException(nameof(connectionId));
        _send = send ?? throw new ArgumentNullException(nameof(send));
    }

    public event Action<IcCircuitInboundMessage>? InboundMessage;

    public bool Connected => _connected;

    public string ConnectionId => _connectionId;

    /// <summary>
    /// True once a valid SignalR handshake JSON has been processed via
    /// <see cref="HandleInbound"/> and the ack was queued. Used by the
    /// Long Polling poll endpoint to decide whether to keep replying with
    /// the handshake ack on empty polls (to handle the race where a poll
    /// in flight returns before the handshake POST arrives).
    /// </summary>
    public bool HandshakeComplete => _handshakeComplete;

    /// <summary>
    /// Push inbound bytes from Wasp.WebSockets. May contain handshake + zero
    /// or more BlazorPack frames concatenated; the caller need not split.
    /// </summary>
    public void HandleInbound(ReadOnlySpan<byte> payload)
    {
        int start = 0;
        if (!_handshakeComplete)
        {
            int sepIndex = IndexOfRecordSeparator(payload);
            if (sepIndex < 0)
            {
                // Partial handshake; wait for more bytes. Real IC-WS messages
                // are delivered as whole application payloads so this branch
                // should be unreachable in practice — present for safety.
                throw new FormatException(
                    "first inbound frame did not contain a SignalR handshake terminator (0x1E)");
            }
            // Body before separator is the handshake JSON; we don't strictly
            // need to parse it (blazor.web.js only ever sends
            // `{"protocol":"messagepack","version":1}`) but validate the
            // protocol tag to fail fast on misconfiguration.
            ValidateHandshake(payload.Slice(0, sepIndex));
            _send(HandshakeAck);
            _handshakeComplete = true;
            _connected = true;
            start = sepIndex + 1;
        }

        // Consume any remaining bytes as BlazorPack frames.
        while (start < payload.Length)
        {
            int p = start;
            int bodyLen = BlazorPackReader.ReadLengthPrefix(payload, ref p);
            int frameEnd = p + bodyLen;
            if (frameEnd > payload.Length)
                throw new FormatException(
                    $"frame body length {bodyLen} exceeds remaining payload {payload.Length - p}");

            // We slice from `start` so the frame contains its own length
            // prefix — keeps the existing BlazorPackReader.ReadInvocation
            // contract unchanged.
            DispatchFrame(payload.Slice(start, frameEnd - start));
            start = frameEnd;
        }
    }

    private void DispatchFrame(ReadOnlySpan<byte> frame)
    {
        int messageType = PeekMessageType(frame);
        switch (messageType)
        {
            case 1: // Invocation
            {
                var inv = BlazorPackReader.ReadInvocation(frame);
                InboundMessage?.Invoke(new IcCircuitInboundMessage(inv.Target, inv.Arguments, inv.InvocationId));
                break;
            }
            case 3: // Completion
            {
                var completion = BlazorPackReader.ReadCompletion(frame);
                if (completion.InvocationId is null) break;
                if (_pendingInvocations.TryRemove(completion.InvocationId, out var pending))
                {
                    pending.Complete(completion);
                }
                break;
            }
            case 6: // Ping — mirror back. SignalR Ping is array(1) [type=6].
            {
                _send(PingFrame);
                break;
            }
            case 7: // Close
            {
                _connected = false;
                break;
            }
            default:
                // StreamItem(2)/StreamInvocation(4)/CancelInvocation(5) are
                // not produced by blazor.web.js for the InteractiveServer
                // render mode, but ignore rather than crash.
                break;
        }
    }

    private static int PeekMessageType(ReadOnlySpan<byte> frame)
    {
        int p = 0;
        BlazorPackReader.ReadLengthPrefix(frame, ref p);
        // body[0] is the array header; body[?] after that is the first int.
        // We use the public ReadInvocation prefix logic: read array hdr, then
        // first int.
        var body = frame.Slice(p);
        int bp = 0;
        BlazorPackReader.ReadArrayHeader(body, ref bp);
        return (int)BlazorPackReader.ReadInt64(body, ref bp);
    }

    /// <summary>
    /// Ship a pre-built BlazorPack frame (length-prefixed body) without
    /// the SendCoreAsync Invocation envelope. Used by CircuitHubFacade's
    /// completion sink to forward Completion frames produced by
    /// BlazorHubDispatcher.
    /// </summary>
    public void SendRawFrame(byte[] frameBytes)
    {
        if (frameBytes is null) throw new ArgumentNullException(nameof(frameBytes));
        _send(frameBytes);
    }

    public ValueTask SendCoreAsync(string target, object?[] args, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(target)) throw new ArgumentException("target required", nameof(target));
        if (args is null) throw new ArgumentNullException(nameof(args));
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = BlazorPackWriter.WriteInvocation(target, args);
        _send(bytes);
        return ValueTask.CompletedTask;
    }

    public ValueTask<T> InvokeCoreAsync<T>(string target, object?[] args, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(target)) throw new ArgumentException("target required", nameof(target));
        if (args is null) throw new ArgumentNullException(nameof(args));
        cancellationToken.ThrowIfCancellationRequested();

        string invocationId = Interlocked.Increment(ref _nextInvocationId).ToString();
        var completion = new InvocationCompletion<T>();
        _pendingInvocations[invocationId] = completion;

        var bytes = BlazorPackWriter.WriteInvocationWithId(invocationId, target, args);
        _send(bytes);

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(state =>
            {
                var (id, dict) = ((string, ConcurrentDictionary<string, IInvocationCompletion>))state!;
                if (dict.TryRemove(id, out var p)) p.Cancel();
            }, (invocationId, _pendingInvocations));
        }

        return new ValueTask<T>(completion.Task);
    }

    private static int IndexOfRecordSeparator(ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
            if (bytes[i] == 0x1E) return i;
        return -1;
    }

    private static void ValidateHandshake(ReadOnlySpan<byte> jsonBytes)
    {
        // Blazor Server's SignalR client sends `{"protocol":"blazorpack",
        // "version":1}` — `blazorpack` is its MessagePack-derived hub
        // protocol (a subset of msgpack with Hub-specific framing, which
        // BlazorPackReader/Writer handle). Plain SignalR clients send
        // `messagepack`; accept either so we don't surprise non-Blazor
        // SignalR consumers.
        if (Contains(jsonBytes, "blazorpack")) return;
        if (Contains(jsonBytes, "messagepack")) return;
        throw new FormatException(
            "SignalR handshake must advertise 'blazorpack' (Blazor Server) or 'messagepack'");

        static bool Contains(ReadOnlySpan<byte> hay, string needle)
        {
            for (int i = 0; i + needle.Length <= hay.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (hay[i + j] != (byte)needle[j]) { match = false; break; }
                }
                if (match) return true;
            }
            return false;
        }
    }

    private static readonly byte[] PingFrame = BuildPingFrame();
    private static byte[] BuildPingFrame()
    {
        // msgpack array(1) [type=6 (Ping)] = 0x91 0x06, length-prefixed.
        Span<byte> body = stackalloc byte[] { 0x91, 0x06 };
        Span<byte> lenBuf = stackalloc byte[5];
        int lenLen = BlazorPackWriter.WriteLengthPrefix(body.Length, lenBuf);
        var result = new byte[lenLen + body.Length];
        for (int i = 0; i < lenLen; i++) result[i] = lenBuf[i];
        for (int i = 0; i < body.Length; i++) result[lenLen + i] = body[i];
        return result;
    }

    // ─── Internal: invocation correlation slot ───────────────────────────
    internal interface IInvocationCompletion
    {
        void Complete(BlazorPackReader.DecodedCompletion completion);
        void Cancel();
    }

    private sealed class InvocationCompletion<T> : IInvocationCompletion
    {
        private readonly TaskCompletionSource<T> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Task => _tcs.Task;

        public void Complete(BlazorPackReader.DecodedCompletion completion)
        {
            if (completion.ErrorMessage is not null)
            {
                _tcs.TrySetException(new InvalidOperationException(
                    $"Hub Completion frame returned error: {completion.ErrorMessage}"));
                return;
            }
            if (completion.HasResult)
            {
                try
                {
                    _tcs.TrySetResult((T)Convert.ChangeType(completion.Result!, typeof(T))!);
                }
                catch (Exception ex)
                {
                    _tcs.TrySetException(ex);
                }
            }
            else
            {
                // Void-completion: only valid for Task (non-generic). We
                // treat it as default(T).
                _tcs.TrySetResult(default!);
            }
        }

        public void Cancel() => _tcs.TrySetCanceled();
    }
}
