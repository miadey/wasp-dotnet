using System;
using System.Collections.Generic;

namespace Wasp.WebSockets;

// C# mirrors of the Candid types defined in ic-websocket-cdk's
// `ws_types.did`. These flow between the canister and the gateway.

public sealed class ClientKey : IEquatable<ClientKey>
{
    public ClientKey(byte[] clientPrincipal, ulong clientNonce)
    {
        ClientPrincipal = clientPrincipal;
        ClientNonce = clientNonce;
    }

    public byte[] ClientPrincipal { get; }
    public ulong ClientNonce { get; }

    public bool Equals(ClientKey? other) =>
        other is not null &&
        ClientNonce == other.ClientNonce &&
        ClientPrincipal.AsSpan().SequenceEqual(other.ClientPrincipal);

    public override bool Equals(object? obj) => obj is ClientKey ck && Equals(ck);

    public override int GetHashCode()
    {
        var hc = new HashCode();
        hc.Add(ClientNonce);
        hc.AddBytes(ClientPrincipal);
        return hc.ToHashCode();
    }
}

public sealed class WebsocketMessage
{
    public WebsocketMessage(ClientKey clientKey, ulong sequenceNum, ulong timestamp, bool isServiceMessage, byte[] content)
    {
        ClientKey = clientKey;
        SequenceNum = sequenceNum;
        Timestamp = timestamp;
        IsServiceMessage = isServiceMessage;
        Content = content;
    }
    public ClientKey ClientKey { get; }
    public ulong SequenceNum { get; }
    public ulong Timestamp { get; }
    public bool IsServiceMessage { get; }
    public byte[] Content { get; }
}

public sealed class CanisterOutputMessage
{
    public CanisterOutputMessage(ClientKey clientKey, string key, byte[] content)
    {
        ClientKey = clientKey;
        Key = key;
        Content = content;
    }
    public ClientKey ClientKey { get; }
    public string Key { get; }       // "{gateway_principal_text}_{nonce:020}"
    public byte[] Content { get; }   // CBOR-encoded WebsocketMessage
}

public sealed class CanisterOutputCertifiedMessages
{
    public CanisterOutputCertifiedMessages(
        IReadOnlyList<CanisterOutputMessage> messages,
        byte[] cert,
        byte[] tree,
        bool isEndOfQueue)
    {
        Messages = messages;
        Cert = cert;
        Tree = tree;
        IsEndOfQueue = isEndOfQueue;
    }
    public IReadOnlyList<CanisterOutputMessage> Messages { get; }
    public byte[] Cert { get; }
    public byte[] Tree { get; }
    public bool IsEndOfQueue { get; }
}

public enum CloseMessageReason : byte
{
    WrongSequenceNumber = 0,
    InvalidServiceMessage = 1,
    KeepAliveTimeout = 2,
    ClosedByApplication = 3,
}
