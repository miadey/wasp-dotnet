using System;
using System.Collections.Generic;
using Wasp.WebSockets;

namespace Wasp.AspNetCore.Blazor.Server;

// Owns the per-principal IcCircuitTransport instances and bridges the
// Wasp.WebSockets WsHandlers callbacks into the right transport.
//
// Lifecycle:
//   - ws_open  → CreateTransport(principal) → invoke TransportConnected
//   - ws_message → existing transport.HandleInbound(payload)
//   - ws_close → existing transport (if any) marked disconnected + evicted;
//                invoke TransportDisconnected
//
// Wire-up from a canister Program.cs:
//
//     var registry = new IcCircuitTransportRegistry(WaspWs.Send);
//     WaspWs.Init(new WsHandlers
//     {
//         OnOpen    = registry.HandleOpen,
//         OnMessage = registry.HandleMessage,
//         OnClose   = registry.HandleClose,
//     });
//     registry.TransportConnected += transport => circuitDispatcher.Bind(transport);
//
// S4 source-gen will own `circuitDispatcher.Bind` — currently the hook is
// raised so tests + early integration code can wire whatever consumer.
public sealed class IcCircuitTransportRegistry
{
    private readonly Func<byte[], byte[], bool> _send;
    private readonly Dictionary<byte[], IcCircuitTransport> _transports =
        new(PrincipalEqualityComparer.Instance);

    public event Action<IcCircuitTransport>? TransportConnected;
    public event Action<IcCircuitTransport>? TransportDisconnected;

    /// <param name="send">
    /// Bound to <c>WaspWs.Send</c> in production. Returns false if the
    /// principal isn't currently registered with WaspWs (a race in practice;
    /// the transport drops the byte buffer in that case).
    /// </param>
    public IcCircuitTransportRegistry(Func<byte[], byte[], bool> send)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
    }

    public void HandleOpen(byte[] clientPrincipal)
    {
        var transport = new IcCircuitTransport(
            connectionId: Principal.ToText(clientPrincipal),
            send: bytes => _send(clientPrincipal, bytes));
        _transports[clientPrincipal] = transport;
        TransportConnected?.Invoke(transport);
    }

    public void HandleMessage(byte[] clientPrincipal, byte[] payload)
    {
        if (!_transports.TryGetValue(clientPrincipal, out var transport))
        {
            // ws_message arrived without a prior ws_open. WaspWs already
            // filters this case (ClientNotRegistered) but be defensive.
            return;
        }
        transport.HandleInbound(payload);
    }

    public void HandleClose(byte[] clientPrincipal)
    {
        if (_transports.Remove(clientPrincipal, out var transport))
        {
            TransportDisconnected?.Invoke(transport);
        }
    }

    /// <summary>Used by tests to drive the registry without WaspWs.</summary>
    internal IcCircuitTransport? GetTransport(byte[] clientPrincipal)
        => _transports.TryGetValue(clientPrincipal, out var t) ? t : null;

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
