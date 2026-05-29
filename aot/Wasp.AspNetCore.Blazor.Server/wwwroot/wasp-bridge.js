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
  // Per-connection warmup window — keep polls tight while the circuit
  // is still bootstrapping (handshake → StartCircuit → initial
  // render-diff, each ~2 s consensus), then back off so the network
  // panel isn't dominated by idle polls. Measured from the first poll
  // on a given connection id.
  var WARMUP_WINDOW_MS = 10000;
  var POLL_FAST_MS = 150;
  var POLL_SLOW_MS = 1000;
  var HOLD_TIMEOUT_MS = 25000;
  // Idle tier — once a connection has been established and quiet for
  // longer than IDLE_AFTER_MS, back the cadence off further so a truly
  // idle tab costs ~1 poll / 2.5 s instead of 1 / 1 s. "Quiet" is
  // tracked per-connection by recording the last-activity wall-clock
  // ms on the same per-id _firstPollAt state object (key
  // _firstPollAt['__act__' + id]); we stamp it on every non-empty poll
  // response and on every inbound /_blazor POST. Warmup and the fast
  // path are untouched, so first interaction is never delayed.
  var POLL_IDLE_MS = 2500;
  var IDLE_AFTER_MS = 30000;

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
  var _firstPollAt = new Object();
  // Handshake-batching state. Blazor Server's connect sequence is:
  //   1. SignalR POST handshake {"protocol":"blazorpack","version":1}\x1e
  //   2. Server queues ack {}\x1e in outbox            ← update call
  //   3. Client GET poll picks up ack
  //   4. Blazor POST circuit-init (UpdateRootComponents / AttachToCircuit)
  //   5. Server processes, queues initial render-diff   ← update call
  //   6. Client GET poll picks up diff
  // On IC each ← update call is ~2 s consensus. Combining steps 1 and 4
  // into one POST cuts one full round trip. The trick: SignalR awaits the
  // handshake ack before firing the circuit-init POST. We resolve that
  // wait *locally* by synthesizing the ack into the next GET response;
  // SignalR proceeds; the circuit-init POST arrives; we combine it with
  // the buffered handshake bytes and fire ONE real POST. The server then
  // processes both frames in a single update call and queues only the
  // render-diff (or queues ack + diff, in which case we strip the
  // duplicate ack on the way back).
  var _pendingHandshake = new Object();   // id → Uint8Array of handshake bytes
  var _ackInjected = new Object();        // id → boolean
  var _stripDupAck = new Object();        // id → boolean
  function _extractId(url) {
    var m = url.match(/[?&]id=([^&]+)/);
    return m ? m[1] : null;
  }
  function _bytesStartWith(bytes, prefix) {
    if (bytes.length < prefix.length) return false;
    for (var i = 0; i < prefix.length; i++) if (bytes[i] !== prefix[i]) return false;
    return true;
  }
  // {"protocol":  — the handshake frame's leading characters (JSON, sent
  // before blazorpack binary begins).
  var HANDSHAKE_PREFIX = new Uint8Array([0x7B, 0x22, 0x70, 0x72, 0x6F, 0x74, 0x6F, 0x63, 0x6F, 0x6C, 0x22, 0x3A]);
  // {}<RS> — handshake ack
  var ACK_BYTES = new Uint8Array([0x7B, 0x7D, 0x1E]);
  function _signalCircuitReady() {
    // Fire once per page-load. App.razor (or any host page) can listen
    // for this and hide its "Connecting…" UI without waiting on the
    // 16-s MutationObserver fallback.
    if (window._waspCircuitReadySignalled) return;
    window._waspCircuitReadySignalled = true;
    try {
      window.dispatchEvent(new CustomEvent('wasp:circuit-ready'));
    } catch (_) { /* old browsers — fine */ }
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
    // POST /_blazor?id=…  — batching of handshake + circuit-init
    if (url && url.match(/\/_blazor\?id=/) && method === 'POST') {
      var pid = _extractId(url);
      // Inbound send = real traffic. Stamp last-activity so a tab that
      // just sent a user event isn't immediately demoted to the idle
      // tier on its next poll.
      if (pid) _firstPollAt['__act__' + pid] = Date.now();
      var body = init && init.body;
      var bodyBytes;
      if (body instanceof Uint8Array) {
        bodyBytes = body;
      } else if (body instanceof ArrayBuffer) {
        bodyBytes = new Uint8Array(body);
      } else if (body) {
        bodyBytes = new Uint8Array(await new Response(body).arrayBuffer());
      } else {
        bodyBytes = new Uint8Array(0);
      }
      var isHandshake = _bytesStartWith(bodyBytes, HANDSHAKE_PREFIX);
      if (isHandshake && pid && !_pendingHandshake[pid]) {
        // Buffer the handshake bytes and fake a 200 to SignalR so it
        // proceeds to await the ack. We'll inject the ack on the next
        // GET poll.
        _pendingHandshake[pid] = bodyBytes;
        _ackInjected[pid] = false;
        // Safety net: if the circuit-init POST never arrives within
        // FLUSH_ALONE_MS, fire the handshake alone so the connection
        // doesn't stall.
        setTimeout(function () {
          if (_pendingHandshake[pid]) {
            var alone = _pendingHandshake[pid];
            delete _pendingHandshake[pid];
            origFetch(input, { method: 'POST', body: alone, headers: init && init.headers })
              .catch(function () { /* best effort */ });
          }
        }, 400);
        return new Response('', {
          status: 200,
          headers: { 'content-type': 'application/octet-stream', 'content-length': '0' },
        });
      }
      if (pid && _pendingHandshake[pid]) {
        // Combine buffered handshake + this POST's bytes and fire ONE
        // real POST to the canister. The server's HandleInbound parses
        // multiple length-prefixed frames natively.
        var hs = _pendingHandshake[pid];
        delete _pendingHandshake[pid];
        var combined = new Uint8Array(hs.length + bodyBytes.length);
        combined.set(hs, 0);
        combined.set(bodyBytes, hs.length);
        _stripDupAck[pid] = true;
        var newInit = { method: 'POST', body: combined };
        if (init && init.headers) newInit.headers = init.headers;
        if (init && init.signal) newInit.signal = init.signal;
        return await origFetch(input, newInit);
      }
      // Not a handshake or circuit-init batch — pass through.
      return await origFetch(input, init);
    }
    if (url && url.match(/\/_blazor\?id=/) && method === 'GET') {
      var id = _extractId(url);
      // Fake-ack injection: if we've buffered a handshake POST for this
      // id and haven't yet handed an ack to SignalR, do so now. This
      // resolves SignalR's _handshakeCompleted so it fires the next
      // (circuit-init) POST, which we'll combine with the buffered
      // handshake bytes.
      if (id && _pendingHandshake[id] && !_ackInjected[id]) {
        _ackInjected[id] = true;
        if (id) _establishedIds[id] = true;
        return new Response(ACK_BYTES, {
          status: 200,
          headers: { 'content-type': 'application/octet-stream', 'content-length': '3' },
        });
      }
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
      // parsing.
      //
      // Polling cadence is per-CONNECTION (not per-cycle): if the
      // connection is still inside its warmup window (first 10 s),
      // poll every POLL_FAST_MS so the handshake-ack → StartCircuit
      // → initial render-diff sequence isn't gated on slow polls.
      // After warmup, poll every POLL_SLOW_MS — idle network looks
      // like one HTTP request per second, comparable to vanilla
      // long-poll cadence.
      if (id && !_firstPollAt[id]) _firstPollAt[id] = Date.now();
      var start = Date.now();
      while (Date.now() - start < HOLD_TIMEOUT_MS) {
        if (init && init.signal && init.signal.aborted) break;
        var resp = await origFetch(input, init);
        if (resp.status !== 200) return resp;
        var cl = parseInt(resp.headers.get('content-length') || '-1', 10);
        if (cl > 0 || cl < 0) {
          // Non-empty body = real traffic. Stamp last-activity so the
          // idle tier resets and we stay responsive while data flows.
          if (id) _firstPollAt['__act__' + id] = Date.now();
          // If we're owed a duplicate-ack strip (we fed SignalR a fake
          // ack during the batching), strip the server's real ack
          // prefix `{}\x1e` here so SignalR's blazorpack parser doesn't
          // re-process it as a frame.
          if (id && _stripDupAck[id]) {
            delete _stripDupAck[id];
            var rb = new Uint8Array(await resp.arrayBuffer());
            if (_bytesStartWith(rb, ACK_BYTES)) {
              var rest = rb.slice(ACK_BYTES.length);
              _signalCircuitReady();
              return new Response(rest, {
                status: 200,
                headers: { 'content-type': 'application/octet-stream', 'content-length': String(rest.length) },
              });
            }
            _signalCircuitReady();
            return new Response(rb, {
              status: 200,
              headers: { 'content-type': 'application/octet-stream', 'content-length': String(rb.length) },
            });
          }
          _signalCircuitReady();
          return resp;
        }
        var inWarmup = id && (Date.now() - _firstPollAt[id]) < WARMUP_WINDOW_MS;
        // After warmup, drop to the slow tier; if the connection has
        // also been quiet (no non-empty poll / no inbound POST) for
        // longer than IDLE_AFTER_MS, drop further to the idle tier.
        var pollMs;
        if (inWarmup) {
          pollMs = POLL_FAST_MS;
        } else {
          var lastAct = id ? _firstPollAt['__act__' + id] : 0;
          if (!lastAct) lastAct = id ? _firstPollAt[id] : 0;
          var quietFor = lastAct ? (Date.now() - lastAct) : 0;
          pollMs = (quietFor > IDLE_AFTER_MS) ? POLL_IDLE_MS : POLL_SLOW_MS;
        }
        await new Promise(function (r) { setTimeout(r, pollMs); });
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
          // NOTE: skipNegotiation=true would save the /_blazor/negotiate
          // round trip, BUT SignalR's client enforces "skipNegotiation
          // can only be used with WebSocket transport." LongPolling
          // mandates negotiate. The negotiate POST goes via our v2
          // query path (~300 ms) so the cost is bounded.
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
