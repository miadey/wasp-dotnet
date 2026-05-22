// ic-ws-blazor-adapter.js
//
// Monkey-patches `window.WebSocket` so blazor.web.js's Blazor Server
// connection routes through ic-websocket-js to a backend IC canister
// instead of a vanilla TCP WebSocket. Every other WebSocket on the page
// (dev HMR, third-party SDKs, etc.) passes through to the native impl.
//
// HOW IT WORKS
//
// 1. blazor.web.js opens its Server connection by calling
//    `new WebSocket("ws[s]://<page-host>/_blazor?id=...")` after the
//    HttpConnection negotiates against /_blazor/negotiate. We don't run
//    a SignalR negotiate endpoint on the canister; instead the page
//    serves a pre-canned negotiate response (Wasp.AspNetCore.Blazor.Server
//    serves it from BlazorNegotiateEndpoint when M4.S6 lands the back-end
//    HTTP integration). The fact that the negotiate returns no
//    `availableTransports` other than WebSockets forces blazor.web.js
//    straight into the WS path.
//
// 2. This shim intercepts the WebSocket constructor, sees the `/_blazor`
//    URL, and constructs an `IcWebSocket` from `@ic-reactor/ic-websocket-js`
//    pointed at the configured backend canister id. The IcWebSocket
//    already implements the standard `addEventListener`/`send`/`close`
//    surface so blazor.web.js sees no difference.
//
// 3. ic-websocket-js handles the IC-WS protocol: opens against the
//    public gateway, polls `ws_get_messages`, dispatches `ws_message`
//    update calls. Our Wasp.WebSockets canister-side code handles the
//    other end.
//
// CONFIGURATION
//
// Load this script BEFORE `_framework/blazor.web.js` in the page head:
//
//   <script>
//     window.IcWsBlazorConfig = {
//       backendCanisterId: 'rrkah-fqaaa-aaaaa-aaaaq-cai',
//       gatewayUrl: 'wss://gateway.icws.io',
//       icHost: 'https://icp-api.io',
//       // OPTIONAL: identity provider. Defaults to anonymous (DelegationIdentity
//       // from an Internet Identity sign-in is recommended for non-anonymous
//       // canisters).
//       identity: undefined,
//     };
//   </script>
//   <script src="/ic-ws-blazor-adapter.js"></script>
//   <script src="/_framework/blazor.web.js" autostart="false"></script>
//
// WHAT THIS DOES NOT DO
//
//  - Does not bundle ic-websocket-js — that ships separately (as a UMD
//    bundle on the asset canister, see aot/Wasp.AspNetCore.AssetCanister
//    MSBuild target). This file expects `window.IcWebSocket` to already
//    be present at construction time.
//  - Does not negotiate the SignalR handshake — that happens in
//    blazor.web.js after the WS opens. The first 0x1E-terminated JSON
//    frame is dealt with by IcCircuitTransport (C# side).
//  - Does not handle re-connects beyond what IcWebSocket already does.

(function () {
    'use strict';

    var NativeWebSocket = window.WebSocket;
    if (typeof NativeWebSocket !== 'function') {
        console.error('[ic-ws-blazor] native WebSocket missing; aborting shim install');
        return;
    }

    function isBlazorUrl(url) {
        if (typeof url !== 'string') return false;
        // blazor.web.js opens `/_blazor?id=…` (server mode) or
        // `/_blazor/disconnect` (rarely). Match the prefix.
        var path = '';
        try {
            var u = new URL(url, document.baseURI);
            path = u.pathname;
        } catch (_) {
            path = url;
        }
        return path.indexOf('/_blazor') === 0;
    }

    function makeIcShim(url, protocols) {
        var cfg = window.IcWsBlazorConfig;
        if (!cfg || !cfg.backendCanisterId || !cfg.gatewayUrl) {
            throw new Error(
                '[ic-ws-blazor] window.IcWsBlazorConfig must define ' +
                'backendCanisterId and gatewayUrl before any /_blazor ' +
                'WebSocket is opened.');
        }
        // The bundled ic-websocket.umd.js exposes the module as
        // window.IcWebSocketModule (IIFE global name). Unwrap to the
        // IcWebSocket constructor. window.IcWebSocket is also accepted
        // for projects that expose it directly.
        var IcWS = window.IcWebSocket
            || (window.IcWebSocketModule && window.IcWebSocketModule.IcWebSocket)
            || (window.IcWebSocketModule && window.IcWebSocketModule.default && window.IcWebSocketModule.default.IcWebSocket);
        if (typeof IcWS !== 'function') {
            throw new Error(
                '[ic-ws-blazor] IcWebSocket constructor not found — load ' +
                'the ic-websocket-js UMD bundle (window.IcWebSocketModule) ' +
                'before this adapter.');
        }
        console.debug('[ic-ws-blazor] intercepting', url, '-> IC canister', cfg.backendCanisterId);
        return new IcWS({
            canisterId: cfg.backendCanisterId,
            gatewayUrl: cfg.gatewayUrl,
            host: cfg.icHost || 'https://icp-api.io',
            identity: cfg.identity,
        });
    }

    function WaspBlazorWebSocket(url, protocols) {
        if (isBlazorUrl(url)) {
            return makeIcShim(url, protocols);
        }
        return new NativeWebSocket(url, protocols);
    }

    // Preserve the static surface that some libraries (and the JS spec)
    // expose on the WebSocket constructor.
    WaspBlazorWebSocket.CONNECTING = NativeWebSocket.CONNECTING;
    WaspBlazorWebSocket.OPEN = NativeWebSocket.OPEN;
    WaspBlazorWebSocket.CLOSING = NativeWebSocket.CLOSING;
    WaspBlazorWebSocket.CLOSED = NativeWebSocket.CLOSED;
    WaspBlazorWebSocket.prototype = NativeWebSocket.prototype;

    window.WebSocket = WaspBlazorWebSocket;
    // Stash the native ref for any code that needs to opt out.
    window.WaspNativeWebSocket = NativeWebSocket;
})();
