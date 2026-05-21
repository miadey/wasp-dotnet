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
        }),
      });
      if (!resp.ok) {
        console.warn('[wasp] event POST failed', resp.status);
        return;
      }
      var batch = await resp.json();
      _applyBatch(batch);
    } catch (err) {
      console.warn('[wasp] event error', err);
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
    target.innerHTML = batch.html;
    lastBatchId = batch.batchId || lastBatchId;
    _wireEvents(target);
  }

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
  window.wasp = {
    rewire: _wireEvents,
    applyBatch: _applyBatch,
    get lastBatchId() { return lastBatchId; },
    set lastBatchId(v) { lastBatchId = v; },
  };

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', function () { _wireEvents(); });
  } else {
    _wireEvents();
  }
})();
