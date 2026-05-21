// Wasp Blazor-on-IC bridge. Loaded automatically by the
// BlazorOnICRuntime component.
//
// Two pieces here:
//   1. fetch wrapper — retries empty /_blazor poll responses a few
//      times before giving up so the SignalR client's initial
//      handshake probe doesn't see a flapping connection.
//   2. Pre-registered renderer interop bridge — trimming kills the
//      framework's WebRendererInteropMethods registration path; we
//      install a manual bridge before Blazor.start that proxies
//      .NET-from-JS calls back through the captured SignalR hub.
(function () {
  if (window._waspBridgeLoaded) return;
  window._waspBridgeLoaded = true;

  // ─── 1. Negotiate nonce + client-side long-poll proxy ─────────────
  //
  // Two responsibilities:
  //
  // (a) Negotiate nonce injection — per-request nonce defeats the IC
  //     boundary's ~10 s query cache for byte-identical multi-tab
  //     negotiate POSTs. Server-side the nonce mixes into the
  //     SHA-256-derived connectionId so each tab still gets a unique
  //     id.
  //
  // (b) Long-poll proxy for /_blazor?id GETs — vanilla Blazor Server
  //     uses true long polling: the server holds the GET open for up
  //     to ~90 s and returns the moment data arrives. On IC the
  //     canister can't hold a query call open (no async wait between
  //     calls), so SignalR's natural "fire-next-poll-as-soon-as-
  //     previous-returns" loop hits the canister 4–5×/second. We
  //     emulate the hold on the CLIENT: when a poll comes back empty,
  //     re-poll the canister every POLL_INTERVAL_MS until either data
  //     arrives or HOLD_TIMEOUT_MS elapses. SignalR sees one slow
  //     response, not many fast ones.
  var origFetch = window.fetch;
  var POLL_INTERVAL_MS = 1000;   // canister poll cadence inside the hold
  var HOLD_TIMEOUT_MS = 25000;   // max hold for an ESTABLISHED connection

  // SignalR's LongPolling connect sequence:
  //   1. POST /_blazor/negotiate         (we already speed this up via v2)
  //   2. open GET /_blazor?id=X          (waits for FIRST response)
  //   3. send POST /_blazor?id=X         (handshake frame)
  //   4. next GET poll picks up the ack from server
  // If we hold step 2 for 25 s waiting for data, SignalR never sends
  // step 3 — the connection appears dead. So we only hold AFTER we've
  // observed at least one non-empty response on this connection id
  // (= server has sent something, handshake or render-diff).
  var _establishedIds = new Object();
  function _extractId(url) {
    var m = url.match(/[?&]id=([^&]+)/);
    return m ? m[1] : null;
  }

  async function _wrappedFetch(input, init) {
    var url = typeof input === 'string' ? input : input.url;
    var method = (init && init.method) || 'GET';
    if (url && url.indexOf('/_blazor/negotiate') >= 0 && method === 'POST') {
      var nonce = (window.crypto && window.crypto.randomUUID)
        ? window.crypto.randomUUID()
        : Math.random().toString(36).slice(2) + Date.now().toString(36);
      var sep = url.indexOf('?') >= 0 ? '&' : '?';
      var newUrl = url + sep + 'wasp-nonce=' + encodeURIComponent(nonce);
      if (typeof input === 'string') {
        input = newUrl;
      } else {
        input = new Request(newUrl, input);
      }
    }
    if (url && url.match(/\/_blazor\?id=/) && method === 'GET') {
      var id = _extractId(url);
      var seenBefore = !!(id && _establishedIds[id]);
      if (id) _establishedIds[id] = true;
      if (!seenBefore) {
        // First poll on this connection — pass-through (no hold), so
        // SignalR's transport-open probe completes quickly and it
        // proceeds to send the handshake POST.
        return await origFetch(input, init);
      }
      // Subsequent polls — emulate server-side long polling. Inspect
      // each response via Content-Length header WITHOUT consuming the
      // body stream; SignalR needs the body intact for blazorpack
      // parsing. The server always sets content-length.
      var start = Date.now();
      while (Date.now() - start < HOLD_TIMEOUT_MS) {
        if (init && init.signal && init.signal.aborted) break;
        var resp = await origFetch(input, init);
        if (resp.status !== 200) return resp;
        var cl = parseInt(resp.headers.get('content-length') || '-1', 10);
        if (cl > 0 || cl < 0) {
          // cl > 0: data present, hand off untouched.
          // cl < 0: header missing — be safe, hand off too.
          return resp;
        }
        // cl === 0: empty payload, wait and re-poll. The discarded
        // resp's body is empty so closing it is harmless.
        await new Promise(function (r) { setTimeout(r, POLL_INTERVAL_MS); });
      }
      return await origFetch(input, init);
    }
    return await origFetch(input, init);
  }
  window.fetch = _wrappedFetch;

  // ─── 2. Pre-registered renderer interop bridge ───────────────────
  // Trimming on wasi-wasm AOT removes the framework's
  // WebRendererInteropMethods + DotNetObjectReference registration,
  // so blazor.web.js's `determinePendingOperation` returns null and
  // event dispatch fails with "A(...).invokeMethodAsync is not a
  // function". We install a hand-rolled bridge that proxies
  // BeginInvokeDotNetFromJS through the captured SignalR hub.
  function _waspStartBlazor() {
    var _waspHub = null;
    var interop = {
      invokeMethodAsync: function (methodName /*, ...args */) {
        var args = Array.prototype.slice.call(arguments, 1);
        if (!_waspHub) {
          return Promise.reject(new Error('Wasp SignalR hub not built yet'));
        }
        var callId = String(Math.floor(Math.random() * 1e9));
        _waspHub.send('BeginInvokeDotNetFromJS', callId, null, methodName, 0, JSON.stringify(args))
          .catch(function (e) { console.log('[wasp] hub send err', e); });
        return Promise.resolve(null);
      },
      invokeMethod: function () {
        throw new Error('synchronous invokeMethod not supported on Wasp/Server transport');
      }
    };
    try {
      Blazor._internal.attachWebRendererInterop(1, interop, null, null);
    } catch (e) {
      console.log('[wasp] attachWebRendererInterop:', e.message);
    }

    Blazor.start({
      circuit: {
        configureSignalR: function (builder) {
          builder
            .withUrl('/_blazor', { transport: 4 /* LongPolling */ })
            .configureLogging(1);
          var origBuild = builder.build.bind(builder);
          builder.build = function () {
            _waspHub = origBuild();
            return _waspHub;
          };
        }
      }
    });
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', _waspStartBlazor);
  } else {
    _waspStartBlazor();
  }
})();
