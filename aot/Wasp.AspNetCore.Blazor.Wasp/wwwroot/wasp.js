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
    // Delegation guard: if the listener is on a CONTAINER (e.target is
    // a descendant, not the listener itself), only fire when the
    // clicked subtree actually carries data-wasp-args. Without this,
    // unrelated clicks inside e.g. a chat messages container — a
    // reply link, an emoji-picker open button, an attachment preview
    // close — would trigger the container's @onclick with empty args,
    // wasting an IC update call per click.
    if (e.target !== e.currentTarget) {
      var argSource = e.target.closest && e.target.closest('[data-wasp-args]');
      if (!argSource) return;
    }
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
    // Page-wide persistent inputs ([data-wasp-persist] with a name)
    // bring their current value as a default — same form or not. Lets
    // a username input in a Discord-style sidebar reach a Send handler
    // in the channel composer without forcing developers to nest them
    // in one form. Form-scoped values still win if both exist.
    document.querySelectorAll('[data-wasp-persist][name]').forEach(function (el) {
      var k = el.getAttribute('name');
      if (!(k in args) && el.value !== '') args[k] = String(el.value);
    });
    // Per-element extra args via data-wasp-args='{"x":"5","y":"7"}'.
    // Resolved from the actually-clicked element (e.target), then
    // walked up — useful for event-delegation patterns where the
    // handler sits on a container and cells carry their identity in
    // data-wasp-args. Also collects data-* keys with the wasp- prefix
    // for ergonomic per-cell metadata.
    var argSrc = e.target && e.target.closest && e.target.closest('[data-wasp-args]');
    if (argSrc) {
      try {
        var parsed = JSON.parse(argSrc.getAttribute('data-wasp-args'));
        for (var k in parsed) args[k] = String(parsed[k]);
      } catch (_) { /* ignore malformed json */ }
    }
    // Optimistic paint: handler element advertises a colour via
    // data-wasp-paint-with="<css-colour>". On click, we set
    // e.target.style.background immediately. The IC update call still
    // runs through consensus (~1–2s), but the user sees feedback
    // instantly; the next render-batch overwrites our optimistic
    // paint with the authoritative server colour (no flash if they
    // agree; brief revert if the server rejected, e.g. cooldown).
    var paintWith = e.currentTarget.getAttribute &&
                    e.currentTarget.getAttribute('data-wasp-paint-with');
    if (paintWith && e.target && e.target !== e.currentTarget) {
      try { e.target.style.background = paintWith; } catch (_) {}
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
          path: location.pathname + location.search,
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

  function _applyBatch(batch, opts) {
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
    if (active && active.matches &&
        (active.matches('[data-wasp-persist]') || active.matches('[data-wasp-keep]'))) {
      focusInfo = {
        name: active.getAttribute('name'),
        tag: active.tagName,
        start: active.selectionStart,
        end: active.selectionEnd,
      };
    }
    // Snapshot composer state (text being typed, attached image,
    // active reply target) that would otherwise be wiped by the
    // innerHTML swap. Done only when the swap is triggered by the
    // background reactivity poll — sends and SPA nav want the fresh
    // server state untouched.
    var kept = (opts && opts.keepState) ? _snapshotKeepState(target) : null;
    var scrollKept = (opts && opts.keepState) ? _snapshotScroll() : null;

    target.innerHTML = batch.html;
    lastBatchId = batch.batchId || lastBatchId;
    _wireEvents(target);
    _waspPersistRestore(target);
    if (kept) _restoreKeepState(target, kept);
    if (scrollKept) _restoreScroll(scrollKept);
    if (focusInfo && focusInfo.name) {
      var refocus = target.querySelector('[name="' + focusInfo.name + '"]');
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
    if (scroller && !(kept || scrollKept)) scroller.scrollTop = scroller.scrollHeight;
  }

  // ─── data-wasp-keep — survive reactivity-poll DOM swaps ───────────
  // The bridge's reactivity poll replaces innerHTML every few seconds
  // when another user sends a message. Without this, the textarea
  // value the local user is typing, plus hidden inputs holding an
  // attached image / active reply target, all get wiped under their
  // cursor. Any element marked [data-wasp-keep] has its .value (for
  // inputs/textareas) preserved across poll swaps; derived UI state
  // (image preview, reply badge) is re-applied from those values.
  function _snapshotKeepState(root) {
    var snap = { values: {} };
    root.querySelectorAll('[data-wasp-keep][name]').forEach(function (el) {
      snap.values[el.getAttribute('name')] = el.value;
    });
    return snap;
  }
  function _restoreKeepState(root, snap) {
    Object.keys(snap.values).forEach(function (name) {
      var el = root.querySelector('[data-wasp-keep][name="' + name + '"]');
      if (el) el.value = snap.values[name];
    });
    // Re-derive the image attach preview from imageData.
    var img = root.querySelector('input[name="imageData"]');
    if (img && img.value) {
      var preview = root.querySelector('[data-wasp-image-preview]');
      if (preview) {
        var pimg = preview.querySelector('img') || preview.appendChild(document.createElement('img'));
        pimg.src = img.value;
        preview.classList.add('is-active');
      }
    }
    // Re-derive the reply badge from replyTo + replyToUser + replyToText.
    var rTo = root.querySelector('input[name="replyTo"]');
    if (rTo && rTo.value) {
      var rUser = root.querySelector('input[name="replyToUser"]');
      var rText = root.querySelector('input[name="replyToText"]');
      var badge = root.querySelector('.dc-reply-badge');
      if (badge) {
        var u = badge.querySelector('.dc-reply-user');
        var t = badge.querySelector('.dc-reply-text');
        if (u && rUser) u.textContent = rUser.value;
        if (t && rText) t.textContent = rText.value;
        badge.classList.add('is-active');
      }
    }
  }
  function _snapshotScroll() {
    var s = document.getElementById('chat-scroll');
    if (!s) return null;
    // "Sticky to bottom" — if we're within 64px of the bottom, snap
    // back to bottom after the swap (so new messages slide in). If
    // the user has scrolled up to read history, preserve their exact
    // scroll position so they don't jump.
    var atBottom = (s.scrollHeight - s.scrollTop - s.clientHeight) < 64;
    return { atBottom: atBottom, scrollTop: s.scrollTop };
  }
  function _restoreScroll(snap) {
    var s = document.getElementById('chat-scroll');
    if (!s) return;
    if (snap.atBottom) s.scrollTop = s.scrollHeight;
    else s.scrollTop = snap.scrollTop;
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
        if (stored != null) { el.value = stored; return; }
        // First-time visitor — seed a friendly default. data-wasp-default
        // wins; otherwise generate a guest name for inputs called
        // "username".
        var def = el.getAttribute('data-wasp-default');
        if (def && def.indexOf('{nn}') >= 0) {
          def = def.replace('{nn}', Math.floor(Math.random() * 90 + 10));
        }
        if (!def && el.getAttribute('name') === 'username') {
          def = 'User' + Math.floor(Math.random() * 90 + 10);
        }
        if (def) {
          el.value = def;
          localStorage.setItem(key, def);
        }
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

  // ─── Click popovers ───────────────────────────────────────────────
  // Trigger element has data-wasp-popover-trigger; the *next sibling*
  // is the popover and carries data-wasp-popover. Clicking the trigger
  // toggles a [data-open] attribute on the popover. Clicking outside
  // any popover (or its trigger) closes them all.
  document.addEventListener('click', function (e) {
    var trig = e.target.closest && e.target.closest('[data-wasp-popover-trigger]');
    if (trig) {
      var pop = trig.nextElementSibling;
      if (pop && pop.hasAttribute && pop.hasAttribute('data-wasp-popover')) {
        var wasOpen = pop.hasAttribute('data-open');
        document.querySelectorAll('[data-wasp-popover][data-open]').forEach(function (el) {
          el.removeAttribute('data-open');
        });
        if (!wasOpen) pop.setAttribute('data-open', '');
      }
      e.preventDefault();
      e.stopPropagation();
      return;
    }
    if (!(e.target.closest && e.target.closest('[data-wasp-popover]'))) {
      document.querySelectorAll('[data-wasp-popover][data-open]').forEach(function (el) {
        el.removeAttribute('data-open');
      });
    }
  });

  // ─── Reply state (client-side only) ───────────────────────────────
  // Clicking <button data-wasp-reply-to="msgId"> on a message:
  //   1. sets the composer's hidden <input name="replyTo"> value,
  //   2. surfaces a "Replying to @user: text…" badge above the textarea,
  //   3. focuses the textarea so the user can keep typing.
  // The cancel × ([data-wasp-cancel-reply]) clears both. After a send,
  // the server response swaps the DOM and we naturally lose the reply
  // state — exactly what we want.
  document.addEventListener('click', function (e) {
    var reply = e.target.closest && e.target.closest('[data-wasp-reply-to]');
    if (reply) {
      var msgEl = reply.closest('.dc-message');
      var username = '', text = '';
      if (msgEl) {
        var u = msgEl.querySelector('.dc-username');
        var t = msgEl.querySelector('.dc-text');
        if (u) username = u.textContent || '';
        if (t) text = (t.textContent || '').slice(0, 80);
      }
      _setReplyTarget(reply.getAttribute('data-wasp-reply-to'), username, text);
      e.preventDefault();
      e.stopPropagation();
      return;
    }
    var cancel = e.target.closest && e.target.closest('[data-wasp-cancel-reply]');
    if (cancel) {
      _setReplyTarget('', '', '');
      e.preventDefault();
      e.stopPropagation();
    }
  });
  function _setReplyTarget(id, username, text) {
    function setVal(name, v) {
      var el = document.querySelector('input[name="' + name + '"]');
      if (el) el.value = v || '';
    }
    setVal('replyTo', id);
    setVal('replyToUser', username);
    setVal('replyToText', text);
    var badge = document.querySelector('.dc-reply-badge');
    if (badge) {
      var u = badge.querySelector('.dc-reply-user'); if (u) u.textContent = username;
      var t = badge.querySelector('.dc-reply-text'); if (t) t.textContent = text;
      if (id) badge.classList.add('is-active');
      else    badge.classList.remove('is-active');
    }
    if (id) {
      var ta = document.querySelector('textarea[name="text"]');
      if (ta) ta.focus();
    }
  }

  // ─── File input → base64 in hidden field ──────────────────────────
  // <input type="file" data-wasp-image-into="imageData"> reads the
  // chosen file as a data: URL into the named hidden input. Size is
  // capped client-side at 1.5 MB so we stay safely under the IC
  // ingress limit once the data URL is wrapped in the event POST.
  document.addEventListener('change', function (e) {
    var f = e.target;
    if (!f || !f.matches || !f.matches('input[type="file"][data-wasp-image-into]')) return;
    var file = f.files && f.files[0];
    if (!file) return;
    if (!/^image\//.test(file.type)) {
      alert('Images only.'); f.value = ''; return;
    }
    // 1 MB raw cap — once base64-inflated (×4/3) and wrapped in the
    // JSON event body, this lands at ~1.4 MB, comfortably inside the
    // IC's 2 MB ingress message envelope. 1.5 MB raw would overflow.
    if (file.size > 1000000) {
      alert('Image is too big (max 1 MB). Got ' + Math.round(file.size / 1024) + ' KB.');
      f.value = ''; return;
    }
    var target = document.querySelector('input[name="' + f.getAttribute('data-wasp-image-into') + '"]');
    var preview = document.querySelector('[data-wasp-image-preview]');
    var fr = new FileReader();
    fr.onload = function () {
      if (target) target.value = fr.result;
      if (preview) {
        var img = preview.querySelector('img') || preview.appendChild(document.createElement('img'));
        img.src = fr.result;
        preview.classList.add('is-active');
      }
    };
    fr.readAsDataURL(file);
  });
  // Clear the attachment preview when × is clicked.
  document.addEventListener('click', function (e) {
    var clear = e.target.closest && e.target.closest('[data-wasp-image-clear]');
    if (!clear) return;
    var target = document.querySelector('input[name="' + clear.getAttribute('data-wasp-image-clear') + '"]');
    if (target) target.value = '';
    var fi = document.querySelector('input[type="file"][data-wasp-image-into]');
    if (fi) fi.value = '';
    var preview = document.querySelector('[data-wasp-image-preview]');
    if (preview) preview.classList.remove('is-active');
    e.preventDefault();
    e.stopPropagation();
  });

  // ─── Emoji-insert buttons ─────────────────────────────────────────
  // <button data-wasp-emoji="👍"> inside a form inserts that string
  // into the nearest <textarea> at the current cursor — purely client
  // side, no server roundtrip. Lets a chat composer offer a one-tap
  // emoji picker without round-tripping every keystroke through the
  // canister.
  document.addEventListener('click', function (e) {
    var btn = e.target.closest && e.target.closest('[data-wasp-emoji]');
    if (!btn) return;
    var emoji = btn.getAttribute('data-wasp-emoji');
    var form = btn.closest('form');
    if (!form) return;
    var ta = form.querySelector('textarea');
    if (!ta) return;
    e.preventDefault();
    var s = ta.selectionStart, eEnd = ta.selectionEnd;
    if (typeof s !== 'number') { ta.value += emoji; ta.focus(); return; }
    ta.value = ta.value.slice(0, s) + emoji + ta.value.slice(eEnd);
    var pos = s + emoji.length;
    ta.focus();
    try { ta.setSelectionRange(pos, pos); } catch (_) {}
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
    _navigateTo(url.pathname + url.search, /*push*/ true);
  });
  window.addEventListener('popstate', function () {
    _navigateTo(location.pathname + location.search, /*push*/ false);
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
        var resp = await origFetch('/_wasp/render?path=' + encodeURIComponent(location.pathname + location.search), { headers: headers });
        if (!resp.ok) continue;
        var ct = resp.headers.get('content-type') || '';
        if (ct.indexOf('json') < 0) continue;
        var batch = await resp.json();
        if (batch.unchanged) continue;
        if (batch.batchId && batch.batchId === lastBatchId) continue;
        _applyBatch(batch, { keepState: true });
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
