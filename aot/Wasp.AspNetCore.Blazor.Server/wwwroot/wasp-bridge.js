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

  // ─── 1. /_blazor poll-retry wrapper ──────────────────────────────
  var origFetch = window.fetch;
  var POLL_RETRY = 6;
  var POLL_DELAY = [50, 100, 150, 200, 250, 300];
  async function _wrappedFetch(input, init) {
    var url = typeof input === 'string' ? input : input.url;
    var method = (init && init.method) || 'GET';
    if (url && url.match(/\/_blazor\?id=/) && method === 'GET') {
      for (var i = 0; i < POLL_RETRY; i++) {
        if (init && init.signal && init.signal.aborted) break;
        var resp = await origFetch(input, init);
        if (resp.status !== 200) return resp;
        var cloned = resp.clone();
        var bytes = await cloned.arrayBuffer();
        if (bytes.byteLength > 0) return resp;
        await new Promise(function (r) { setTimeout(r, POLL_DELAY[i] || 300); });
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
