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
