using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Wasp.WebSockets;

namespace Wasp.AspNetCore.Blazor.Server;

// Owns IcCircuitTransport instances. Two coexisting keying schemes:
//
//   (A) Principal-keyed (legacy ic-websocket-gateway path). Bridges
//       Wasp.WebSockets.WsHandlers callbacks into a transport per client
//       principal. Still used by samples/HelloChat for the raw-WS demo.
//
//   (B) ConnectionId-keyed (SignalR Long Polling path, M4.S7). Each
//       /_blazor/negotiate response mints a fresh GUID; subsequent
//       /_blazor?id=... requests carry it; we look up the transport and
//       its outbound queue by that GUID. Outbound bytes are enqueued for
//       the next poll instead of pushed via WaspWs.
//
// TransportConnected/TransportDisconnected fire for both schemes, so the
// consumer doesn't care which transport carries the bytes.
public sealed class IcCircuitTransportRegistry
{
    private readonly Func<byte[], byte[], bool>? _wsSend;

    // (A) principal map — populated only when constructed with a WaspWs send.
    private readonly Dictionary<byte[], IcCircuitTransport> _wsTransports =
        new(PrincipalEqualityComparer.Instance);

    // (B) connectionId map — populated by /_blazor/negotiate.
    private readonly ConcurrentDictionary<string, LongPollingConnection> _lpConnections = new();

    public event Action<IcCircuitTransport>? TransportConnected;
    public event Action<IcCircuitTransport>? TransportDisconnected;

    /// <summary>Long Polling–only registry. No WaspWs hookup.</summary>
    public IcCircuitTransportRegistry()
    {
        _wsSend = null;
    }

    /// <summary>Legacy WS-gateway registry. Bound to <c>WaspWs.Send</c>.</summary>
    public IcCircuitTransportRegistry(Func<byte[], byte[], bool> send)
    {
        _wsSend = send ?? throw new ArgumentNullException(nameof(send));
    }

    // ─── (A) Principal-keyed WS path ───────────────────────────────────────

    public void HandleOpen(byte[] clientPrincipal)
    {
        if (_wsSend is null) return;
        var transport = new IcCircuitTransport(
            connectionId: Principal.ToText(clientPrincipal),
            send: bytes => _wsSend(clientPrincipal, bytes));
        _wsTransports[clientPrincipal] = transport;
        TransportConnected?.Invoke(transport);
    }

    public void HandleMessage(byte[] clientPrincipal, byte[] payload)
    {
        if (!_wsTransports.TryGetValue(clientPrincipal, out var transport)) return;
        transport.HandleInbound(payload);
    }

    public void HandleClose(byte[] clientPrincipal)
    {
        if (_wsTransports.Remove(clientPrincipal, out var transport))
        {
            TransportDisconnected?.Invoke(transport);
        }
    }

    internal IcCircuitTransport? GetTransport(byte[] clientPrincipal)
        => _wsTransports.TryGetValue(clientPrincipal, out var t) ? t : null;

    // ─── (B) ConnectionId-keyed Long Polling path ──────────────────────────

    /// <summary>
    /// Created by /_blazor/negotiate. Outbound bytes that
    /// IcCircuitTransport.SendCoreAsync would have shipped over WS are
    /// instead enqueued here; the GET /_blazor?id=… handler drains the queue
    /// into the response body.
    /// </summary>
    public sealed class LongPollingConnection
    {
        public IcCircuitTransport Transport { get; }
        public ConcurrentQueue<byte[]> Outbound { get; } = new();

        private LongPollingConnection(IcCircuitTransport transport) { Transport = transport; }

        internal static LongPollingConnection Create(string connectionId)
        {
            ConcurrentQueue<byte[]>? outbound = null;
            var transport = new IcCircuitTransport(
                connectionId: connectionId,
                send: bytes => outbound!.Enqueue(bytes));
            var conn = new LongPollingConnection(transport);
            outbound = conn.Outbound;
            return conn;
        }
    }

    public LongPollingConnection CreateLongPollingConnection(string connectionId)
    {
        var conn = LongPollingConnection.Create(connectionId);
        _lpConnections[connectionId] = conn;
        TransportConnected?.Invoke(conn.Transport);
        return conn;
    }

    public LongPollingConnection? GetLongPollingConnection(string connectionId)
        => _lpConnections.TryGetValue(connectionId, out var c) ? c : null;

    public void CloseLongPollingConnection(string connectionId)
    {
        if (_lpConnections.TryRemove(connectionId, out var conn))
        {
            TransportDisconnected?.Invoke(conn.Transport);
        }
    }

    // ───────────────────────────────────────────────────────────────────────

    private sealed class PrincipalEqualityComparer : IEqualityComparer<byte[]>
    {
        public static readonly PrincipalEqualityComparer Instance = new();
        public bool Equals(byte[]? x, byte[]? y)
            => x is null ? y is null : y is not null && x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] obj)
        {
            var hc = new HashCode();
            hc.AddBytes(obj);
            return hc.ToHashCode();
        }
    }
}
