# BlazorPack wire-format cross-validation

Asserts that `BlazorPackWriter` output is byte-compatible with the actual
`@microsoft/signalr-protocol-msgpack` decoder — the same MessagePack
HubProtocol family that `blazor.web.js` uses to parse server messages.

This closes "Risks surfaced #4" from `docs/m4-s2-circuit-coupling.md`
(encoder tested only against its own decoder).

## Run

```sh
npm install
dotnet run --project Emit -- vectors.json
node validate.mjs
```

Expected output: `11/11 vectors decoded by @microsoft/signalr-protocol-msgpack`.

## What it covers

11 vectors across every (target, args) shape from the M4.S2 catalog:

- 7 outbound `JS.*` calls (RemoteRenderer + RemoteJSRuntime)
- 4 representative inbound ComponentHub calls

## What it does NOT prove

- Cross-compat with the BlazorPack-specific tightenings that
  `Microsoft.AspNetCore.Components.Server/BlazorPack/BlazorPackHubProtocol`
  applies on top of vanilla MessagePack HubProtocol. BlazorPack inherits
  the same `MessagePackHubProtocolWorker.WriteInvocationMessage` envelope,
  so the Invocation-frame layout is identical — but a side-by-side test
  against `blazor.web.js` consuming our bytes in a real browser session
  is still S6/S7 work.
