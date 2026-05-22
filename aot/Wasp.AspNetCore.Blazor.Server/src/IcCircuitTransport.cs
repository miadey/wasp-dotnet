using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wasp.AspNetCore.Blazor.Server;

// Transport seam between Blazor's CircuitHost and the IC-WebSocket protocol.
//
// On stock ASP.NET Core, blazor.web.js talks to ComponentHub (a SignalR Hub)
// over WebSockets. ComponentHub deserializes the incoming Hub method call,
// dispatches to the CircuitHost, and sends responses back through
// IClientProxy.SendAsync.
//
// On IC, we replace that whole pipe:
//   - inbound:  ic-websocket-gateway -> ws_message canister export ->
//               Wasp.WebSockets.WsHandlers.OnMessage -> raise InboundMessage
//   - outbound: SendCoreAsync(target, args, ct) -> serialize -> WaspWs.Send
//
// This is the SEAM. S2 ships only the interface; S3 lands the impl, S4 the
// source-gen dispatch table that replaces SignalR's reflective Hub method
// lookup, S5 the stable-memory circuit store.
//
// See docs/m4-s2-circuit-coupling.md for the full inbound/outbound catalog
// + the line:file citations into dotnet/aspnetcore release/10.0.
public interface IIcCircuitTransport
{
    // Outbound: serialize (target, args) into the BlazorPack-compatible
    // envelope blazor.web.js expects, queue via WaspWs.Send.
    //
    // `target` is one of the JS.* method names enumerated in
    // docs/m4-s2-circuit-coupling.md (JS.RenderBatch, JS.AttachComponent,
    // JS.BeginInvokeJS, JS.EndInvokeDotNet, JS.ReceiveByteArray,
    // JS.BeginTransmitStream). `args` are positional.
    ValueTask SendCoreAsync(string target, object?[] args, CancellationToken cancellationToken);

    // Outbound expecting a typed response (CircuitClientProxy.InvokeCoreAsync<T>).
    // Used for Hub methods marked with a return value; on the IC transport,
    // request/response is correlated via an embedded invocation id since
    // ws_get_messages is a polled stream, not a duplex socket.
    ValueTask<T> InvokeCoreAsync<T>(string target, object?[] args, CancellationToken cancellationToken);

    // Inbound: raised by the host after Wasp.WebSockets receives a payload
    // for the registered client. The S4 source-generated dispatch table
    // subscribes here and routes (target, args) into CircuitHost.
    event Action<IcCircuitInboundMessage>? InboundMessage;

    // Lifecycle: mirrors CircuitClientProxy.Connected. False until the
    // client's `ws_open` arrives + StartCircuit completes; flips back on
    // CloseMessage / WaspWs.Close.
    bool Connected { get; }

    // Per-client identifier, surfaced as CircuitClientProxy.ConnectionId.
    // We use the client principal text form (e.g. "rrkah-...-qaaaa") so it
    // matches whatever the client side reports in audit logs.
    string ConnectionId { get; }
}

public readonly record struct IcCircuitInboundMessage(string Target, object?[] Args, string? InvocationId);
