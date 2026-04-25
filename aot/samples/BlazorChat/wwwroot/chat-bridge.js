// Bridge between Blazor and ic-websocket-js. Uses direct esm.sh URLs
// so we don't need to share an importmap with Blazor's publish-time
// dotnet.js fingerprint mapping.

import { IcWebSocket, generateRandomIdentity, createWsConfig } from "https://esm.sh/ic-websocket-js@0.5.0";
import { Actor, HttpAgent } from "https://esm.sh/@dfinity/agent@2.4.1";

// Mirror of HelloChat's IDL — must match samples/HelloChat/hellochat.did.
const idl = ({ IDL }) => {
  const ClientPrincipal = IDL.Principal;
  const ClientKey = IDL.Record({ client_principal: ClientPrincipal, client_nonce: IDL.Nat64 });
  const WebsocketMessage = IDL.Record({
    client_key: ClientKey,
    sequence_num: IDL.Nat64,
    timestamp: IDL.Nat64,
    is_service_message: IDL.Bool,
    content: IDL.Vec(IDL.Nat8),
  });
  const Result = IDL.Variant({ Ok: IDL.Null, Err: IDL.Text });
  const CanisterOutputMessage = IDL.Record({
    client_key: ClientKey,
    key: IDL.Text,
    content: IDL.Vec(IDL.Nat8),
  });
  const CanisterOutputCertifiedMessages = IDL.Record({
    messages: IDL.Vec(CanisterOutputMessage),
    cert: IDL.Vec(IDL.Nat8),
    tree: IDL.Vec(IDL.Nat8),
    is_end_of_queue: IDL.Bool,
  });
  return IDL.Service({
    ws_open: IDL.Func(
      [IDL.Record({ client_nonce: IDL.Nat64, gateway_principal: IDL.Principal })],
      [Result], []),
    ws_close: IDL.Func([IDL.Record({ client_key: ClientKey })], [Result], []),
    ws_message: IDL.Func(
      [IDL.Record({ msg: WebsocketMessage }), IDL.Opt(IDL.Vec(IDL.Nat8))],
      [Result], []),
    ws_get_messages: IDL.Func(
      [IDL.Record({ nonce: IDL.Nat64 })],
      [IDL.Variant({ Ok: CanisterOutputCertifiedMessages, Err: IDL.Text })],
      ["query"]),
  });
};

let _ws = null;
let _dotnetRef = null;

window.waspChat = {
  async connect(canisterId, gatewayUrl, networkUrl, dotnetRef) {
    if (_ws) { try { _ws.close(); } catch {} _ws = null; }
    _dotnetRef = dotnetRef;

    const agent = await HttpAgent.create({ host: networkUrl, shouldFetchRootKey: true });
    const actor = Actor.createActor(idl, { agent, canisterId });

    const wsConfig = createWsConfig({
      canisterId,
      canisterActor: actor,
      identity: generateRandomIdentity(),
      networkUrl,
    });

    _ws = new IcWebSocket(gatewayUrl, undefined, wsConfig);
    _ws.onmessage = (e) => {
      const text = new TextDecoder().decode(new Uint8Array(e.data));
      _dotnetRef.invokeMethodAsync("OnMessageReceived", text);
    };
    _ws.onerror = (e) => {
      _dotnetRef.invokeMethodAsync("OnError", String(e.error?.message ?? e));
    };
    _ws.onclose = () => {
      _dotnetRef.invokeMethodAsync("OnClosed");
    };

    return new Promise((resolve) => {
      _ws.onopen = () => { _dotnetRef.invokeMethodAsync("OnOpened"); resolve(true); };
    });
  },

  send(text) {
    if (!_ws) throw new Error("not connected");
    _ws.send(new TextEncoder().encode(text));
  },

  disconnect() {
    if (_ws) { try { _ws.close(); } catch {} _ws = null; }
  },
};
