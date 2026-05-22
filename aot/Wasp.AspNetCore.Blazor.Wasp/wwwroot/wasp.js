// Wasp render-as-query JS bridge (gh #118).
//
// Replaces SignalR Long Polling with two HTTP endpoints:
//   GET  /_wasp/render  → canister_query, returns the current render batch
//   POST /_wasp/event   → canister_update, processes an event and returns
//                         the resulting render batch inline (no follow-up
//                         poll needed)
//
// Wire-protocol of a render batch in v1: JSON
//   { "batchId": "<sha256 hex>", "html": "<rendered fragment>",
//     "anchor": "<css-selector>" }
// The bridge swaps the anchor element's innerHTML to the html string.
// In v2 this becomes a proper diff format; for v1 keeping it as plain
// HTML keeps the JS bridge tiny.
//
// Client tracks lastBatchId across requests so the GET render can return
// 304 Not Modified when state hasn't changed → sub-50 ms cached call.
(function () {
  if (window._waspLoaded) return;
  window._waspLoaded = true;

  var lastBatchId = null;

  // ─── Initial hydrate ─────────────────────────────────────────────
  // The SSR pre-render already produced page HTML at the canister's
  // canonical subdomain. We just need to wire up event handlers on the
  // existing DOM. Find every element with a wasp event marker and
  // attach a fetch-based listener.
  function _wireEvents(root) {
    root = root || document;
    var nodes = root.querySelectorAll('[data-wasp-evt-click]');
    for (var i = 0; i < nodes.length; i++) {
      var el = nodes[i];
      if (el._waspWired) continue;
      el._waspWired = true;
      el.addEventListener('click', _onClick);
    }
  }

  async function _onClick(e) {
    var handlerId = e.currentTarget.getAttribute('data-wasp-evt-click');
    if (!handlerId) return;
    e.preventDefault();
    _markLocalEvent();
    // Collect form-field values: if the button is inside a <form>, we
    // serialise the form's inputs and ship them as args. Lets handlers
    // that take an IDictionary<string,string> read submitted text
    // (chat sample) without needing JS interop or @bind plumbing.
    var args = {};
    var form = e.currentTarget.closest && e.currentTarget.closest('form');
    if (form) {
      var fd = new FormData(form);
      fd.forEach(function (v, k) { args[k] = String(v); });
    }
    // Optimistic UX: disable the source button + clear the form's
    // text inputs immediately, so the user can keep typing while
    // consensus runs. If the POST errors, we restore.
    var disabledEls = [];
    var clearedEls = [];
    var btn = e.currentTarget;
    if (btn && btn.tagName === 'BUTTON' && !btn.disabled) {
      btn.disabled = true;
      disabledEls.push(btn);
    }
    if (form) {
      form.querySelectorAll('input[type=text], input:not([type]), textarea').forEach(function (el) {
        // Don't wipe persistent fields (e.g. a username input the
        // user wants to keep across messages).
        if (el.hasAttribute('data-wasp-persist')) return;
        if (el.value) {
          clearedEls.push({ el: el, prev: el.value });
          el.value = '';
        }
      });
    }
    try {
      var resp = await fetch('/_wasp/event', {
        method: 'POST',
        headers: {
          'content-type': 'application/json',
          'accept': 'application/json',
        },
        body: JSON.stringify({
          path: location.pathname,
          handlerId: handlerId,
          lastBatchId: lastBatchId,
          eventName: 'click',
          args: args,
        }),
      });
      if (!resp.ok) {
        console.warn('[wasp] event POST failed', resp.status);
        // Restore optimistic clears so the user can retry.
        clearedEls.forEach(function (c) { c.el.value = c.prev; });
        return;
      }
      var batch = await resp.json();
      _applyBatch(batch);
    } catch (err) {
      console.warn('[wasp] event error', err);
      clearedEls.forEach(function (c) { c.el.value = c.prev; });
    } finally {
      disabledEls.forEach(function (el) { el.disabled = false; });
    }
  }

  function _applyBatch(batch) {
    if (!batch || !batch.html) return;
    var anchor = batch.anchor || '#wasp-root';
    var target = document.querySelector(anchor);
    if (!target) {
      console.warn('[wasp] anchor not found:', anchor);
      return;
    }
    // Snapshot focus + cursor in any persistent input so the user
    // doesn't lose their place when a poll re-renders the page under
    // their cursor.
    var focusInfo = null;
    var active = document.activeElement;
    if (active && active.matches && active.matches('[data-wasp-persist]')) {
      focusInfo = {
        name: active.getAttribute('name'),
        start: active.selectionStart,
        end: active.selectionEnd,
      };
    }
    target.innerHTML = batch.html;
    lastBatchId = batch.batchId || lastBatchId;
    _wireEvents(target);
    _waspPersistRestore(target);
    if (focusInfo && focusInfo.name) {
      var refocus = target.querySelector('[data-wasp-persist][name="' + focusInfo.name + '"]');
      if (refocus) {
        refocus.focus();
        try { refocus.setSelectionRange(focusInfo.start, focusInfo.end); }
        catch (_) {}
      }
    }
    // After applying, autoscroll any "follow tail" scrollbox to its
    // bottom — chat list, log viewer, etc. Looks for a #chat-scroll
    // id for now; generalise to a data-wasp-stick attribute when the
    // need arises.
    var scroller = document.getElementById('chat-scroll');
    if (scroller) scroller.scrollTop = scroller.scrollHeight;
  }

  // Enter-to-send on chat-style textareas. Shift+Enter inserts a
  // newline. Looks for a `data-wasp-enter-submits` opt-in attribute,
  // OR a sibling element with data-wasp-evt-click (which is what the
  // composer pattern naturally produces).
  document.addEventListener('keydown', function (e) {
    if (e.key !== 'Enter' || e.shiftKey || e.ctrlKey || e.metaKey || e.altKey) return;
    var ta = e.target;
    if (!ta || ta.tagName !== 'TEXTAREA') return;
    var form = ta.closest && ta.closest('form');
    if (!form) return;
    var sendBtn = form.querySelector('[data-wasp-evt-click]');
    if (!sendBtn) return;
    e.preventDefault();
    sendBtn.click();
  });

  // ─── Persisted input values ───────────────────────────────────────
  // The render-as-query model re-renders the entire chunk inside
  // #wasp-root on every batch, so any user-typed input values would
  // normally be wiped between renders.
  //
  // Components opt elements into client-side persistence by adding
  // `data-wasp-persist` to any <input> / <textarea>. The bridge:
  //   1. On hydrate / after every applyBatch, fills the element's
  //      value from localStorage under the key `wasp-persist:<name>`.
  //   2. On every keystroke, writes the new value back to
  //      localStorage so it survives reload + reactivity-poll
  //      re-renders + cross-tab.
  //   3. On form-event POSTs, the element's current value is already
  //      shipped to the server via FormData — handlers see it under
  //      `args[<name>]` just like a normal form field.
  //
  // The Razor component never has to know any of this exists.
  function _waspPersistKey(el) {
    var n = el.getAttribute('name');
    return n ? ('wasp-persist:' + n) : null;
  }
  function _waspPersistRestore(root) {
    root = root || document;
    root.querySelectorAll('[data-wasp-persist]').forEach(function (el) {
      var key = _waspPersistKey(el);
      if (!key) return;
      try {
        var stored = localStorage.getItem(key);
        if (stored != null) el.value = stored;
      } catch (_) { /* private mode */ }
    });
  }
  document.addEventListener('input', function (e) {
    var t = e.target;
    if (!t || !t.matches || !t.matches('[data-wasp-persist]')) return;
    var key = _waspPersistKey(t);
    if (!key) return;
    try { localStorage.setItem(key, t.value); }
    catch (_) { /* private mode */ }
  });

  // ─── SPA-style nav ────────────────────────────────────────────────
  // Intercept clicks on internal links, GET /_wasp/render for the new
  // path, swap innerHTML. No full page reload → no fresh fetch of
  // wasp.js or the page shell.
  async function _navigateTo(path, push) {
    try {
      var resp = await fetch('/_wasp/render?path=' + encodeURIComponent(path), {
        headers: { 'accept': 'application/json' },
      });
      if (!resp.ok) {
        // Fall back to full load on error.
        location.href = path;
        return;
      }
      var batch = await resp.json();
      _applyBatch(batch);
      if (push) history.pushState({ wasp: true, path: path }, '', path);
      // Update active class on sidebar links (re-query on each nav).
      document.querySelectorAll('aside.sidebar .nav a').forEach(function (a) {
        var href = a.getAttribute('href');
        a.classList.toggle('active', href === path);
      });
    } catch (e) {
      console.warn('[wasp] nav error', e);
      location.href = path;
    }
  }

  document.addEventListener('click', function (e) {
    if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey) return;
    var a = e.target.closest && e.target.closest('a');
    if (!a) return;
    var href = a.getAttribute('href');
    if (!href) return;
    if (href.startsWith('http://') || href.startsWith('https://')) return;
    if (href.startsWith('#') || href.startsWith('mailto:') || href.startsWith('tel:')) return;
    if (a.getAttribute('target') === '_blank') return;
    // Resolve relative href to a path.
    var url = new URL(a.href, location.origin);
    if (url.origin !== location.origin) return;
    e.preventDefault();
    _navigateTo(url.pathname, /*push*/ true);
  });
  window.addEventListener('popstate', function () {
    _navigateTo(location.pathname, /*push*/ false);
  });

  // ─── Public surface ──────────────────────────────────────────────
  window.wasp = window.wasp || {};
  window.wasp.rewire = _wireEvents;
  window.wasp.applyBatch = _applyBatch;
  Object.defineProperty(window.wasp, 'lastBatchId', {
    configurable: true,
    get: function () { return lastBatchId; },
    set: function (v) { lastBatchId = v; },
  });

  // ─── Cross-device reactivity poll ─────────────────────────────────
  // Render-as-query has no server push (canisters can't initiate
  // connections), so to see another user/device's changes the client
  // has to ask. Every POLL_INTERVAL_MS we GET /_wasp/render for the
  // current path; if the returned batchId differs from what we last
  // applied we swap in the new render. Each poll is a v2-cert query
  // (~300 ms on mainnet, ~10 ms on local) so the idle traffic is one
  // tiny request every few seconds.
  // Adaptive polling: fast burst right after a local event (user
  // clicked, their click POST is in flight — the response will set
  // the new state, but if another tab/device is racing we want their
  // change too). Falls back to relaxed cadence when nothing has
  // happened for a while.
  var POLL_FAST_MS = 500;
  var POLL_RELAXED_MS = 3000;
  var POLL_HIDDEN_MS = 15000;    // background tabs
  var FAST_WINDOW_MS = 5000;     // stay in fast mode this long after an event

  var lastLocalEventAt = 0;
  function _markLocalEvent() { lastLocalEventAt = Date.now(); }

  function _nextInterval() {
    if (document.hidden) return POLL_HIDDEN_MS;
    if (Date.now() - lastLocalEventAt < FAST_WINDOW_MS) return POLL_FAST_MS;
    return POLL_RELAXED_MS;
  }

  async function _reactivityPoll() {
    while (true) {
      try {
        await new Promise(function (r) { setTimeout(r, _nextInterval()); });
        var headers = { 'accept': 'application/json' };
        if (lastBatchId) headers['if-none-match'] = lastBatchId;
        var resp = await origFetch('/_wasp/render?path=' + encodeURIComponent(location.pathname), { headers: headers });
        if (!resp.ok) continue;
        var ct = resp.headers.get('content-type') || '';
        if (ct.indexOf('json') < 0) continue;
        var batch = await resp.json();
        if (batch.unchanged) continue;
        if (batch.batchId && batch.batchId === lastBatchId) continue;
        _applyBatch(batch);
      } catch (e) {
        // network blip — try again next interval
      }
    }
  }

  // Use origFetch (not the wrapped fetch) for the reactivity poll —
  // these are background requests, no event triggers.
  var origFetch = window.fetch;

  function _waspHydrate() {
    _wireEvents();
    _waspPersistRestore();
    var sc = document.getElementById('chat-scroll');
    if (sc) sc.scrollTop = sc.scrollHeight;
    _reactivityPoll();
  }
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', _waspHydrate);
  } else {
    _waspHydrate();
  }
})();
