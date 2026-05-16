// Cross-validate BlazorPackWriter output against the @microsoft/signalr
// MessagePack HubProtocol decoder -- the same MessagePack family blazor.web.js
// uses (BlazorPack is a tightened variant; common Invocation-frame layout).
//
// Strategy:
//   1. .NET-side Emit/ project produces vectors.json with (frameHex, argsExpected).
//   2. We decode frameHex via MessagePackHubProtocol.parseMessages().
//   3. Assert the resulting InvocationMessage matches (target, arguments).
//
// Run:
//   npm install
//   dotnet run --project Emit -- vectors.json
//   npm test

import { readFileSync } from "node:fs";
import { MessagePackHubProtocol } from "@microsoft/signalr-protocol-msgpack";
import { NullLogger } from "@microsoft/signalr";

const vectors = JSON.parse(readFileSync("vectors.json", "utf8"));
const proto = new MessagePackHubProtocol();
let failed = 0;

function hexToBytes(hex) {
  const out = new Uint8Array(hex.length / 2);
  for (let i = 0; i < out.length; i++) out[i] = parseInt(hex.substr(i * 2, 2), 16);
  return out;
}

function eq(a, b) {
  if (a === b) return true;
  if (a == null || b == null) return false;
  if (a instanceof Uint8Array && b instanceof Uint8Array) {
    if (a.length !== b.length) return false;
    for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
    return true;
  }
  if (Array.isArray(a) && Array.isArray(b)) {
    if (a.length !== b.length) return false;
    return a.every((x, i) => eq(x, b[i]));
  }
  return false;
}

function normalizeExpected(v) {
  if (v && typeof v === "object" && "__bin__" in v) {
    return Uint8Array.from(Buffer.from(v.__bin__, "base64"));
  }
  if (v && typeof v === "object" && "__long__" in v) {
    return v.__long__;   // string representation; msgpack lib returns either Number or BigInt
  }
  return v;
}

for (const tc of vectors) {
  const bytes = hexToBytes(tc.frameHex);
  let parsed;
  try {
    parsed = proto.parseMessages(bytes.buffer, NullLogger.instance);
  } catch (e) {
    console.error(`FAIL ${tc.name}: parseMessages threw: ${e.message}`);
    failed++;
    continue;
  }
  if (parsed.length !== 1) {
    console.error(`FAIL ${tc.name}: expected 1 message, got ${parsed.length}`);
    failed++;
    continue;
  }
  const msg = parsed[0];
  // type 1 = Invocation
  if (msg.type !== 1) { console.error(`FAIL ${tc.name}: type ${msg.type} != 1`); failed++; continue; }
  if (msg.target !== tc.target) {
    console.error(`FAIL ${tc.name}: target "${msg.target}" != "${tc.target}"`);
    failed++; continue;
  }
  const expected = tc.argsExpected.map(normalizeExpected);
  if (msg.arguments.length !== expected.length) {
    console.error(`FAIL ${tc.name}: arg count ${msg.arguments.length} != ${expected.length}`);
    failed++; continue;
  }
  let argOk = true;
  for (let i = 0; i < expected.length; i++) {
    const got = msg.arguments[i];
    const exp = expected[i];
    // Numeric: msgpack-lite returns Number for safe ints, BigInt or string for large.
    if (typeof exp === "string" && exp.match(/^-?\d+$/) && (typeof got === "number" || typeof got === "bigint")) {
      if (String(got) !== exp) { console.error(`FAIL ${tc.name} arg[${i}]: ${got} != ${exp}`); argOk = false; }
      continue;
    }
    if (!eq(got, exp)) {
      console.error(`FAIL ${tc.name} arg[${i}]: got`, got, "expected", exp);
      argOk = false;
    }
  }
  if (!argOk) { failed++; continue; }
  console.log(`PASS ${tc.name}  (target=${tc.target}, args=${msg.arguments.length})`);
}

if (failed > 0) {
  console.error(`\n${failed}/${vectors.length} cross-validation failures`);
  process.exit(1);
}
console.log(`\n${vectors.length}/${vectors.length} vectors decoded by @microsoft/signalr-protocol-msgpack`);
