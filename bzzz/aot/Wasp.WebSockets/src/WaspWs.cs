using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Wasp.IcCdk;

namespace Wasp.WebSockets;

// C# port of Omnia's ic-websocket-cdk (https://github.com/omnia-network/ic-websocket-cdk-rs).
//
// Speaks the on-wire protocol that ic-websocket-gateway and
// ic-websocket-js expect. Wires four canister exports
// (ws_open / ws_close / ws_message / ws_get_messages) directly via
// [UnmanagedCallersOnly] thunks defined in this assembly; the consuming
// canister project should add `<UnmanagedEntryPointsAssembly Include="Wasp.WebSockets" />`.
//
// User-facing API:
//   - WaspWs.Init(handlers)            — register on_open / on_message / on_close
//   - WaspWs.Send(clientPrincipal, payloadBytes)
//   - WaspWs.Close(clientPrincipal)
//
// Phase 3 v0.1 simplifications vs the Rust CDK:
//   - No periodic ack/keep-alive timers (clients aren't kicked on idle).
//   - Witness reveals every queued key (no pruning); fine for small queues.
//   - Single gateway assumed (no multi-gateway scenarios).
public static class WaspWs
{
    // ─── Per-client state ────────────────────────────────────────────────
    private sealed class RegisteredClient
    {
        public RegisteredClient(byte[] gatewayPrincipal, ulong now)
        {
            GatewayPrincipal = gatewayPrincipal;
            LastKeepAliveTimestampNs = now;
        }
        public byte[] GatewayPrincipal { get; }
        public ulong LastKeepAliveTimestampNs { get; set; }
    }

    private sealed class RegisteredGateway
    {
        public List<CanisterOutputMessage> MessagesQueue { get; } = new();
        public ulong OutgoingMessageNonce { get; set; }
        public int ConnectedClientsCount { get; set; }
    }

    private sealed class PrincipalEqualityComparer : IEqualityComparer<byte[]>
    {
        public static readonly PrincipalEqualityComparer Instance = new();
        public bool Equals(byte[]? x, byte[]? y) => x is null ? y is null : y is not null && x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] obj) { var hc = new HashCode(); hc.AddBytes(obj); return hc.ToHashCode(); }
    }

    private static readonly Dictionary<ClientKey, RegisteredClient> _registeredClients = new();
    private static readonly Dictionary<byte[], ClientKey> _currentClientKey = new(PrincipalEqualityComparer.Instance);
    private static readonly Dictionary<ClientKey, ulong> _outgoingSeq = new();
    private static readonly Dictionary<ClientKey, ulong> _expectedIncomingSeq = new();
    private static readonly Dictionary<byte[], RegisteredGateway> _registeredGateways = new(PrincipalEqualityComparer.Instance);
    private static readonly CertTree _certTree = new();

    private static WsHandlers _handlers = new();

    public static void Init(WsHandlers handlers) => _handlers = handlers;

    // ─── User API ────────────────────────────────────────────────────────

    /// <summary>Send a Candid-encoded payload to a connected client.</summary>
    public static bool Send(byte[] clientPrincipal, byte[] payload)
    {
        if (!_currentClientKey.TryGetValue(clientPrincipal, out var ck)) return false;
        if (!_registeredClients.TryGetValue(ck, out var rc)) return false;
        Enqueue(ck, rc.GatewayPrincipal, payload, isServiceMessage: false);
        return true;
    }

    /// <summary>Close a client connection, sending a CloseMessage service message first.</summary>
    public static bool Close(byte[] clientPrincipal)
    {
        if (!_currentClientKey.TryGetValue(clientPrincipal, out var ck)) return false;
        if (!_registeredClients.TryGetValue(ck, out var rc)) return false;
        // Best-effort send a CloseMessage(ClosedByApplication) — for v0.1 we
        // emit a minimal Candid-encoded variant tag (ClosedByApplication=3).
        Enqueue(ck, rc.GatewayPrincipal, EncodeCloseMessageReason(CloseMessageReason.ClosedByApplication),
                isServiceMessage: true);
        RemoveClient(ck);
        return true;
    }

    // ─── Internal: queue a WebsocketMessage for a client ─────────────────
    private static void Enqueue(ClientKey ck, byte[] gatewayPrincipal, byte[] content, bool isServiceMessage)
    {
        var gateway = GetOrCreateGateway(gatewayPrincipal);

        ulong seq = _outgoingSeq.TryGetValue(ck, out var s) ? s + 1 : 1;
        _outgoingSeq[ck] = seq;

        var msg = new WebsocketMessage(
            clientKey: ck,
            sequenceNum: seq,
            timestamp: Ic0.time(),
            isServiceMessage: isServiceMessage,
            content: content);

        var cbor = CandidWs.EncodeWebsocketMessageCbor(msg);

        ulong nonce = gateway.OutgoingMessageNonce;
        gateway.OutgoingMessageNonce++;
        string key = $"{Principal.ToText(gatewayPrincipal)}_{nonce:D20}";

        var outMsg = new CanisterOutputMessage(ck, key, cbor);
        gateway.MessagesQueue.Add(outMsg);

        // Update the certified tree with sha256 of the CBOR.
        var hash = Sha256.Hash(cbor);
        _certTree.Insert(key, hash);
    }

    private static RegisteredGateway GetOrCreateGateway(byte[] gatewayPrincipal)
    {
        if (!_registeredGateways.TryGetValue(gatewayPrincipal, out var g))
        {
            g = new RegisteredGateway();
            _registeredGateways[gatewayPrincipal] = g;
        }
        return g;
    }

    private static void RemoveClient(ClientKey ck)
    {
        if (_registeredClients.TryGetValue(ck, out var rc))
        {
            if (_registeredGateways.TryGetValue(rc.GatewayPrincipal, out var g))
            {
                g.ConnectedClientsCount = Math.Max(0, g.ConnectedClientsCount - 1);
            }
        }
        _registeredClients.Remove(ck);
        _currentClientKey.Remove(ck.ClientPrincipal);
        _outgoingSeq.Remove(ck);
        _expectedIncomingSeq.Remove(ck);
        _handlers.OnClose?.Invoke(ck.ClientPrincipal);
    }

    // Encode WebsocketServiceMessageContent as Candid for the variant arms
    // we actually send. v0.1 only sends OpenMessage and CloseMessage.
    private static byte[] EncodeOpenMessage(ClientKey ck)
    {
        // variant { OpenMessage : record { client_key : ClientKey } ; … }
        // Encode as a single-arm variant — the full type table is more
        // bytes but explicit.
        var b = new List<byte>(64);
        b.Add(0x44); b.Add(0x49); b.Add(0x44); b.Add(0x4C);
        // Type table (3 entries):
        //   0: ClientKey record
        //   1: record { client_key: 0 }
        //   2: variant { OpenMessage: 1, AckMessage: <stub>, KeepAliveMessage: <stub>, CloseMessage: <stub> }
        // For v0.1 simplicity, use a 4-arm variant matching the Rust CDK shape.
        Uleb(b, 6);
        // 0: ClientKey { client_principal: principal, client_nonce: nat64 }
        b.Add(0x6C); Uleb(b, 2);
        Uleb(b, 1384677498); b.Add(0x68);  // client_principal: principal
        Uleb(b, 3921324219); b.Add(0x78);  // client_nonce: nat64
        // 1: OpenMessage record { client_key: 0 }
        b.Add(0x6C); Uleb(b, 1);
        Uleb(b, 1025972843); Uleb(b, 0);   // client_key → type 0
        // 2: AckMessage record { last_incoming_sequence_num: nat64 }
        b.Add(0x6C); Uleb(b, 1);
        Uleb(b, 2804597848); b.Add(0x78);  // last_incoming_sequence_num: nat64
        // 3: KeepAliveMessage record { last_incoming_sequence_num: nat64 }
        b.Add(0x6C); Uleb(b, 1);
        Uleb(b, 2804597848); b.Add(0x78);
        // 4: variant CloseMessageReason
        b.Add(0x6B); Uleb(b, 4);
        Uleb(b, 1198956877); b.Add(0x7F);  // ClosedByApplication: null
        Uleb(b, 1409524553); b.Add(0x7F);  // InvalidServiceMessage: null
        Uleb(b, 2743637463); b.Add(0x7F);  // WrongSequenceNumber: null
        Uleb(b, 3580612249); b.Add(0x7F);  // KeepAliveTimeout: null
        // 5: WebsocketServiceMessageContent variant
        // Sort variant arms by hash:
        //   OpenMessage(428171005), KeepAliveMessage(2525358527),
        //   CloseMessage(2918480143), AckMessage(3049003934)
        b.Add(0x6B); Uleb(b, 4);
        Uleb(b, 428171005);  Uleb(b, 1);   // OpenMessage      → record(1)
        Uleb(b, 2525358527); Uleb(b, 3);   // KeepAliveMessage → record(3)
        Uleb(b, 2918480143); b.Add(0x7F);  // CloseMessage     → null (we collapse for v0.1)
        Uleb(b, 3049003934); Uleb(b, 2);   // AckMessage       → record(2)
        // value-types: 1 value of type 5
        Uleb(b, 1);
        Uleb(b, 5);
        // value: variant tag 0 (OpenMessage in sorted order), then OpenMessage record { client_key }
        Uleb(b, 0);
        // ClientKey value: principal + client_nonce
        b.Add(0x01); // principal tag
        Uleb(b, (ulong)ck.ClientPrincipal.Length);
        for (int i = 0; i < ck.ClientPrincipal.Length; i++) b.Add(ck.ClientPrincipal[i]);
        Span<byte> tmp = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(tmp, ck.ClientNonce);
        for (int i = 0; i < 8; i++) b.Add(tmp[i]);
        return b.ToArray();
    }

    private static byte[] EncodeCloseMessageReason(CloseMessageReason _)
    {
        // For v0.1 we send a CloseMessage variant collapsed to null payload
        // (matches the simplified type-table arm in EncodeOpenMessage).
        var b = new List<byte>(64);
        b.Add(0x44); b.Add(0x49); b.Add(0x44); b.Add(0x4C);
        Uleb(b, 6);
        // (Same type table as EncodeOpenMessage — copy-paste for clarity.)
        b.Add(0x6C); Uleb(b, 2);
        Uleb(b, 1384677498); b.Add(0x68);
        Uleb(b, 3921324219); b.Add(0x78);
        b.Add(0x6C); Uleb(b, 1);
        Uleb(b, 1025972843); Uleb(b, 0);
        b.Add(0x6C); Uleb(b, 1);
        Uleb(b, 2804597848); b.Add(0x78);
        b.Add(0x6C); Uleb(b, 1);
        Uleb(b, 2804597848); b.Add(0x78);
        b.Add(0x6B); Uleb(b, 4);
        Uleb(b, 1198956877); b.Add(0x7F);
        Uleb(b, 1409524553); b.Add(0x7F);
        Uleb(b, 2743637463); b.Add(0x7F);
        Uleb(b, 3580612249); b.Add(0x7F);
        b.Add(0x6B); Uleb(b, 4);
        Uleb(b, 428171005);  Uleb(b, 1);
        Uleb(b, 2525358527); Uleb(b, 3);
        Uleb(b, 2918480143); b.Add(0x7F);
        Uleb(b, 3049003934); Uleb(b, 2);
        Uleb(b, 1);
        Uleb(b, 5);
        // variant tag 2 (CloseMessage in the sorted arm list:
        //   OpenMessage=0, KeepAliveMessage=1, CloseMessage=2, AckMessage=3)
        Uleb(b, 2);
        // null payload — no bytes
        return b.ToArray();
    }

    private static void Uleb(List<byte> sink, ulong value)
    {
        do
        {
            byte by = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) by |= 0x80;
            sink.Add(by);
        } while (value != 0);
    }

    // ─── Canister entry-point thunks ─────────────────────────────────────

    [UnmanagedCallersOnly(EntryPoint = "canister_update__ws_open")]
    public static unsafe void WsOpenThunk()
    {
        try
        {
            var arg = MessageContext.ArgData();
            var (clientNonce, gatewayPrincipal) = CandidWs.DecodeWsOpenArgs(arg);
            var caller = MessageContext.CallerPrincipal();

            if (caller.Length == 0)
            {
                Reply.Bytes(CandidWs.EncodeUnitErr("AnonymousPrincipalNotAllowed"));
                return;
            }

            var ck = new ClientKey(caller, clientNonce);

            // If a different key exists for this principal, evict the old one.
            if (_currentClientKey.TryGetValue(caller, out var existing) && !existing.Equals(ck))
            {
                RemoveClient(existing);
            }

            // Register new client.
            var gw = GetOrCreateGateway(gatewayPrincipal);
            gw.ConnectedClientsCount++;
            var rc = new RegisteredClient(gatewayPrincipal, Ic0.time());
            _registeredClients[ck] = rc;
            _currentClientKey[caller] = ck;
            _outgoingSeq[ck] = 0;
            _expectedIncomingSeq[ck] = 1;

            // Queue the OpenMessage service message.
            Enqueue(ck, gatewayPrincipal, EncodeOpenMessage(ck), isServiceMessage: true);

            _handlers.OnOpen?.Invoke(caller);
            Reply.Bytes(CandidWs.EncodeUnitOk());
        }
        catch (Exception ex)
        {
            Reply.Print("[wasp.ws] ws_open: " + ex.Message);
            Reply.Bytes(CandidWs.EncodeUnitErr("ws_open: " + ex.Message));
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "canister_update__ws_close")]
    public static void WsCloseThunk()
    {
        try
        {
            var arg = MessageContext.ArgData();
            var ck = CandidWs.DecodeWsCloseArgs(arg);
            // Caller is the gateway. We don't enforce the gateway-matches
            // check in v0.1.
            if (_registeredClients.ContainsKey(ck))
            {
                RemoveClient(ck);
            }
            Reply.Bytes(CandidWs.EncodeUnitOk());
        }
        catch (Exception ex)
        {
            Reply.Bytes(CandidWs.EncodeUnitErr("ws_close: " + ex.Message));
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "canister_update__ws_message")]
    public static void WsMessageThunk()
    {
        try
        {
            var arg = MessageContext.ArgData();
            var msg = CandidWs.DecodeWsMessageArgs(arg);

            if (!_registeredClients.ContainsKey(msg.ClientKey))
            {
                Reply.Bytes(CandidWs.EncodeUnitErr("ClientNotRegistered"));
                return;
            }

            ulong expected = _expectedIncomingSeq[msg.ClientKey];
            if (msg.SequenceNum != expected)
            {
                // Wrong sequence — kick the client.
                if (_registeredClients.TryGetValue(msg.ClientKey, out var rc))
                {
                    Enqueue(msg.ClientKey, rc.GatewayPrincipal,
                            EncodeCloseMessageReason(CloseMessageReason.WrongSequenceNumber),
                            isServiceMessage: true);
                }
                RemoveClient(msg.ClientKey);
                Reply.Bytes(CandidWs.EncodeUnitErr($"WrongSequenceNumber expected={expected} got={msg.SequenceNum}"));
                return;
            }
            _expectedIncomingSeq[msg.ClientKey] = expected + 1;

            if (msg.IsServiceMessage)
            {
                // Service messages from clients should be KeepAlive only; for
                // v0.1 we simply refresh the keep-alive timestamp.
                if (_registeredClients.TryGetValue(msg.ClientKey, out var rc))
                {
                    rc.LastKeepAliveTimestampNs = Ic0.time();
                }
            }
            else
            {
                _handlers.OnMessage?.Invoke(msg.ClientKey.ClientPrincipal, msg.Content);
            }

            Reply.Bytes(CandidWs.EncodeUnitOk());
        }
        catch (Exception ex)
        {
            Reply.Print("[wasp.ws] ws_message: " + ex.Message);
            Reply.Bytes(CandidWs.EncodeUnitErr("ws_message: " + ex.Message));
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "canister_query__ws_get_messages")]
    public static void WsGetMessagesThunk()
    {
        try
        {
            var arg = MessageContext.ArgData();
            ulong nonce = CandidWs.DecodeWsGetMessagesArgs(arg);
            var caller = MessageContext.CallerPrincipal();

            if (!_registeredGateways.TryGetValue(caller, out var gateway))
            {
                // Not-registered gateway: return an empty success.
                var empty = new CanisterOutputCertifiedMessages(
                    Array.Empty<CanisterOutputMessage>(), Array.Empty<byte>(), Array.Empty<byte>(), true);
                Reply.Bytes(CandidWs.EncodeGetMessagesOk(empty));
                return;
            }

            // Find first message with nonce >= requested nonce.
            // The message keys embed the nonce as the trailing 020-zero-padded
            // integer; messages in the queue are in insertion (= nonce) order.
            int startIndex = 0;
            string keyPrefix = Principal.ToText(caller) + "_";
            string requestedKey = keyPrefix + nonce.ToString("D20");
            for (; startIndex < gateway.MessagesQueue.Count; startIndex++)
            {
                if (StringComparer.Ordinal.Compare(gateway.MessagesQueue[startIndex].Key, requestedKey) >= 0)
                    break;
            }

            const int MaxBatch = 50;
            int endIndex = Math.Min(gateway.MessagesQueue.Count, startIndex + MaxBatch);
            int count = endIndex - startIndex;

            var batch = new CanisterOutputMessage[count];
            for (int i = 0; i < count; i++) batch[i] = gateway.MessagesQueue[startIndex + i];

            byte[] cert = _certTree.GetCertificate();
            byte[] tree = count > 0 ? _certTree.BuildFullTreeCbor() : Array.Empty<byte>();
            bool isEnd = endIndex == gateway.MessagesQueue.Count;

            var result = new CanisterOutputCertifiedMessages(batch, cert, tree, isEnd);
            Reply.Bytes(CandidWs.EncodeGetMessagesOk(result));
        }
        catch (Exception ex)
        {
            Reply.Print("[wasp.ws] ws_get_messages: " + ex.Message);
            Reply.Bytes(CandidWs.EncodeGetMessagesErr("ws_get_messages: " + ex.Message));
        }
    }
}
