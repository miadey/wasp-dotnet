using System;

namespace Wasp.WebSockets;

// Application-supplied callbacks invoked by the CDK on WS lifecycle events.
public delegate void OnWsOpen(byte[] clientPrincipal);
public delegate void OnWsMessage(byte[] clientPrincipal, byte[] payload);
public delegate void OnWsClose(byte[] clientPrincipal);

public sealed class WsHandlers
{
    public OnWsOpen? OnOpen { get; init; }
    public OnWsMessage? OnMessage { get; init; }
    public OnWsClose? OnClose { get; init; }
}
