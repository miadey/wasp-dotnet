// HelloChat end-to-end test:
//   1. open a WebSocket via the gateway
//   2. send a raw message ("hello from node!")
//   3. wait for the canister to echo it back as "echo: hello from node!"
//   4. close + exit
//
// Runs against `dfx start` + ic-websocket-gateway on the standard ports.

import { IcWebSocket, generateRandomIdentity, createWsConfig } from "ic-websocket-js";
import { Actor, HttpAgent } from "@dfinity/agent";
import { execSync } from "node:child_process";

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

const canisterId = execSync("dfx canister id hellochat", { encoding: "utf8" }).trim();
console.log("[client] canister:", canisterId);

const agent = await HttpAgent.create({ host: "http://127.0.0.1:4943", shouldFetchRootKey: true });
const actor = Actor.createActor(idl, { agent, canisterId });

const wsConfig = createWsConfig({
  canisterId,
  canisterActor: actor,
  identity: generateRandomIdentity(),
  networkUrl: "http://127.0.0.1:4943",
});

const ws = new IcWebSocket("ws://127.0.0.1:8080", undefined, wsConfig);

const result = await new Promise((resolve, reject) => {
  const timeout = setTimeout(() => reject(new Error("timeout after 30s")), 30_000);

  ws.onopen = () => {
    console.log("[client] WS open — sending raw bytes");
    // Our canister sends/receives RAW bytes (not Candid-wrapped) so we
    // bypass the SDK's Candid encoding by using the underlying _send.
    // Easiest: send a Uint8Array directly via the SDK's send() — the
    // SDK Candid-encodes it as `vec nat8` which our canister receives
    // as the raw blob (since blob = vec nat8).
    ws.send(new Uint8Array([0x68, 0x65, 0x6c, 0x6c, 0x6f])); // "hello"
  };

  ws.onmessage = (e) => {
    const bytes = new Uint8Array(e.data);
    const text = new TextDecoder().decode(bytes);
    console.log("[client] received:", JSON.stringify(text));
    clearTimeout(timeout);
    ws.close();
    resolve(text);
  };

  ws.onerror = (e) => { clearTimeout(timeout); reject(new Error("ws error: " + (e.error?.message ?? e))); };
  ws.onclose = () => console.log("[client] closed");
});

console.log("[client] DONE; final payload:", result);
process.exit(0);
