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

  // Track in-flight click POSTs so the background reactivity poll
  // doesn't DOM-swap the user's optimistic state away while their
  // update call is mid-consensus.
  var inFlightClicks = 0;

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
    // e.target.style.background immediately. Optional companion
    // data-wasp-cooldown-ms enforces a client-side throttle that
    // matches the server's per-user cooldown — clicks within the
    // window are ignored entirely (no POST, no optimistic paint), so
    // we don't enqueue rejected updates whose stale full-render
    // response would wipe other in-flight optimistic paints.
    var paintEl = e.currentTarget;
    var paintWith = paintEl.getAttribute && paintEl.getAttribute('data-wasp-paint-with');
    if (paintWith) {
      var coolMs = parseInt(paintEl.getAttribute('data-wasp-cooldown-ms') || '0', 10);
      if (coolMs > 0) {
        // Persist the cooldown across SPA navs + page reloads, keyed
        // by the current username (server's cooldown is per-user).
        // Without this, changing colour (SPA nav) replaces paintEl
        // and the element-local timestamp is lost — user clicks fast,
        // server quietly rejects, optimistic paint lingers locally,
        // and a later full re-render reveals only the placements
        // that actually committed.
        var u = (document.querySelector('input[name="username"]') || {}).value || 'anon';
        var key = 'wasp:cooldown:' + u;
        var last = 0;
        try { last = parseInt(localStorage.getItem(key) || '0', 10); } catch (_) {}
        if (Date.now() - last < coolMs) {
          e.preventDefault();
          e.stopPropagation();
          return;
        }
        try { localStorage.setItem(key, Date.now().toString()); } catch (_) {}
        // Mirror onto the element for the cooldown-status indicator's
        // 100ms tick (it reads paintEl._waspLastClickAt).
        paintEl._waspLastClickAt = Date.now();
      }
      if (e.target && e.target !== paintEl) {
        try { e.target.style.background = paintWith; } catch (_) {}
      }
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
    inFlightClicks++;
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
      // If the click target sits inside a [data-wasp-bind-grid]
      // element, the delta-poll loop is authoritative — applying the
      // server's full HTML response would clobber concurrent
      // optimistic paints from rapid clicks. Skip the swap; the next
      // delta poll picks up the change.
      // NOTE: e.currentTarget is nulled by the browser after the
      // first await, so check via paintEl (captured synchronously
      // above) instead.
      var inGridBinding = paintEl && paintEl.closest &&
                          paintEl.closest('[data-wasp-bind-grid]');
      if (inGridBinding) {
        // Nothing to do — store poll will catch up authoritatively.
        return;
      }
      // Sends snap to bottom (user wants to see their new message
      // / image). Reactions, create-room, replies — preserve scroll
      // so clicking 👍 on an older message doesn't yank the view.
      var isSend = !!(args.text || args.imageData);
      _applyBatch(batch, { scrollStrategy: isSend ? 'tail' : 'preserve' });
    } catch (err) {
      console.warn('[wasp] event error', err);
      clearedEls.forEach(function (c) { c.el.value = c.prev; });
    } finally {
      disabledEls.forEach(function (el) { el.disabled = false; });
      inFlightClicks--;
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
    // Scroll strategy: 'tail' snaps to bottom (default for sends,
    // SPA nav into a new room — show the latest message). 'preserve'
    // restores the prior scrollTop (sticky-to-bottom if was near
    // bottom). 'preserve' is the default for reactions, replies,
    // create-room — anywhere the user isn't expecting to be yanked
    // around.
    var scrollStrategy = (opts && opts.scrollStrategy)
        || ((opts && opts.keepState) ? 'preserve' : 'tail');
    var scrollKept = (scrollStrategy === 'preserve') ? _snapshotScroll() : null;

    target.innerHTML = batch.html;
    lastBatchId = batch.batchId || lastBatchId;
    _wireEvents(target);
    _waspPersistRestore(target);
    // Reset bindings — old elements are gone, scan new ones.
    stores = {};
    _waspScanBindings(target);
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
    // Tail snap — only when no explicit scroll restore happened.
    if (scrollStrategy === 'tail' && !scrollKept) {
      _waspChatScrollWatch();
    } else if (target.querySelector && target.querySelector('#chat-scroll')) {
      // Even when not tailing, re-attach the observer to the new
      // #chat-scroll element so future content growth keeps users
      // pinned to bottom (when they were already there).
      _waspChatScrollWatch();
    }
    // Re-apply client-side chat decorations (self-mention highlight,
    // own-message edit/delete visibility, "new messages" divider,
    // mark-as-read) after every DOM swap.
    _waspChatDecorate();
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
    // Re-derive the edit badge from a kept editMsgId so an in-progress
    // edit survives a reactivity-poll DOM swap.
    var eId = root.querySelector('input[name="editMsgId"]');
    if (eId && eId.value) {
      var eb = root.querySelector('.dc-edit-badge');
      if (eb) eb.classList.add('is-active');
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
        if (t) {
          // Read the message body but drop the trailing "(edited)" marker
          // so the reply preview shows the text, not "hello(edited)".
          text = Array.prototype.filter
            .call(t.childNodes, function (n) { return !(n.classList && n.classList.contains('dc-edited')); })
            .map(function (n) { return n.textContent; }).join('').slice(0, 80);
        }
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

  // ─── Mention-insert buttons ───────────────────────────────────────
  // <button data-wasp-mention="user99"> inserts "@user99 " into the
  // first <textarea data-wasp-mention-target> on the page (or, as a
  // fallback, the first textarea inside a <form> on the page). Unlike
  // data-wasp-emoji the trigger can live outside the composer form —
  // e.g. a Discord-style member rail rendered as a sibling <aside>.
  document.addEventListener('click', function (e) {
    var btn = e.target.closest && e.target.closest('[data-wasp-mention]');
    if (!btn) return;
    var name = btn.getAttribute('data-wasp-mention');
    if (!name) return;
    var ta = document.querySelector('textarea[data-wasp-mention-target]')
          || document.querySelector('form textarea');
    if (!ta) return;
    e.preventDefault();
    var insert = '@' + name + ' ';
    var s = ta.selectionStart, eEnd = ta.selectionEnd;
    if (typeof s !== 'number') { ta.value += insert; ta.focus(); return; }
    ta.value = ta.value.slice(0, s) + insert + ta.value.slice(eEnd);
    var pos = s + insert.length;
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
    // Opt-out: links into a separate sub-app (e.g. a Blazor WASM SPA
    // hosted as static assets) should be a real browser navigation
    // so they load their own bootstrapper instead of being routed
    // through /_wasp/render.
    if (a.hasAttribute('data-wasp-no-spa')) return;
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

  // ─── Cooldown status indicator ────────────────────────────────────
  // Any element with [data-wasp-paint-status] gets its textContent +
  // class updated every 100 ms based on the cooldown of the nearest
  // [data-wasp-cooldown-ms] element on the page. Shows "+" green when
  // a click would be accepted, "× Ns" red while we're in the
  // cooldown window.
  setInterval(function () {
    var cool = document.querySelector('[data-wasp-cooldown-ms]');
    if (!cool) return;
    var ms = parseInt(cool.getAttribute('data-wasp-cooldown-ms') || '0', 10);
    if (ms <= 0) return;
    // Source of truth is localStorage keyed by current username —
    // same key the click handler uses, so the indicator stays
    // accurate across SPA navs (palette colour changes) and reloads.
    var u = (document.querySelector('input[name="username"]') || {}).value || 'anon';
    var last = 0;
    try { last = parseInt(localStorage.getItem('wasp:cooldown:' + u) || '0', 10); } catch (_) {}
    var remaining = ms - (Date.now() - last);
    var ready = remaining <= 0;
    document.querySelectorAll('[data-wasp-paint-status]').forEach(function (s) {
      if (ready) {
        if (s.textContent !== '+') s.textContent = '+';
        s.className = 'wasp-status wasp-status-ready';
      } else {
        var sec = Math.ceil(remaining / 1000);
        s.textContent = '× ' + sec + 's';
        s.className = 'wasp-status wasp-status-cooling';
      }
    });
  }, 100);

  // ─── data-wasp-bind realtime stores ───────────────────────────────
  // Markup pattern:
  //   <span data-wasp-bind="counter:value">@initial</span>
  //   <div  data-wasp-bind-grid="place"   data-wasp-grid-palette='[colours]'>
  // The bridge polls /api/<store>?since=<cursor> a few times per second
  // and patches just the bound elements — no innerHTML swap, no full
  // render. Much cheaper than the full /_wasp/render poll, and feels
  // like push.
  var stores = {};   // name -> { cursor, fields: { fieldName: [elements] }, grids: [{el, palette}] }

  function _waspScanBindings(root) {
    root = root || document;
    root.querySelectorAll('[data-wasp-bind]').forEach(function (el) {
      var spec = el.getAttribute('data-wasp-bind');  // "counter:value"
      var i = spec.indexOf(':');
      if (i < 0) return;
      var name = spec.slice(0, i), field = spec.slice(i + 1);
      if (!stores[name]) stores[name] = { cursor: 0, fields: {}, grids: [] };
      if (!stores[name].fields[field]) stores[name].fields[field] = [];
      stores[name].fields[field].push(el);
    });
    root.querySelectorAll('[data-wasp-bind-grid]').forEach(function (el) {
      var name = el.getAttribute('data-wasp-bind-grid');
      var palette = [];
      try { palette = JSON.parse(el.getAttribute('data-wasp-grid-palette') || '[]'); }
      catch (_) {}
      if (!stores[name]) stores[name] = { cursor: 0, fields: {}, grids: [] };
      stores[name].grids.push({ el: el, palette: palette });
    });
  }

  async function _waspPollStores() {
    while (true) {
      // Same race-avoidance as the reactivity poll — don't overwrite
      // optimistic UI while the user's click is mid-consensus.
      if (inFlightClicks > 0) {
        await new Promise(function (r) { setTimeout(r, 200); });
        continue;
      }
      var names = Object.keys(stores);
      for (var i = 0; i < names.length; i++) {
        var name = names[i];
        var s = stores[name];
        try {
          var resp = await origFetch('/api/' + name + '?since=' + s.cursor,
                                     { headers: { 'accept': 'application/json' } });
          if (!resp.ok) continue;
          var data = await resp.json();
          if (data.unchanged) continue;
          if (data.resync) {
            // Server can't fit our requested delta — let the standard
            // reactivity poll do a full render-batch swap instead.
            continue;
          }
          // Scalar fields: update every bound element's textContent.
          Object.keys(s.fields).forEach(function (f) {
            if (data[f] !== undefined) {
              s.fields[f].forEach(function (el) { el.textContent = data[f]; });
            }
          });
          // Grid changes: apply each [x, y, colour] to the right cell.
          if (data.changes && s.grids.length > 0) {
            s.grids.forEach(function (g) {
              var cells = g.el.children;
              var cols = parseInt(g.el.getAttribute('data-wasp-grid-cols') || '40', 10);
              data.changes.forEach(function (c) {
                var idx = c[1] * cols + c[0];
                var cell = cells[idx];
                if (cell) cell.style.background = g.palette[c[2]] || '#000';
              });
            });
          }
          if (data.cursor !== undefined) s.cursor = data.cursor;
        } catch (_) { /* network blip — try again next tick */ }
      }
      // Faster than the full-render poll: bound updates are 10s of bytes.
      await new Promise(function (r) { setTimeout(r, document.hidden ? 4000 : 400); });
    }
  }

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
        // While the user's click POST is mid-consensus, the canister's
        // current state is "pre-click" — applying this poll batch would
        // visibly undo the user's optimistic action (e.g. paint a pixel
        // and watch it flicker back to white). Skip this round; the
        // POST will return its own up-to-date batch soon.
        if (inFlightClicks > 0) continue;
        _applyBatch(batch, { keepState: true });
      } catch (e) {
        // network blip — try again next interval
      }
    }
  }

  // Use origFetch (not the wrapped fetch) for the reactivity poll —
  // these are background requests, no event triggers.
  var origFetch = window.fetch;

  function _waspSnapChatToBottom() {
    var sc = document.getElementById('chat-scroll');
    if (sc) sc.scrollTop = sc.scrollHeight;
  }

  // Reactive scroll-to-bottom. The messages container changes height
  // multiple times after the initial render: avatars paint, emoji
  // fonts swap in, inline reactions push lines down, images decode.
  // A handful of fixed-delay snaps misses some of these. Instead,
  // watch the scrollHeight while the user is "near bottom" and
  // re-snap any time it grows. The instant the user scrolls up to
  // read history, we stop forcing them back down.
  var _chatStickToBottom = true;
  var _chatObserver = null;
  function _waspChatScrollWatch() {
    var sc = document.getElementById('chat-scroll');
    if (!sc) return;
    if (_chatObserver) { try { _chatObserver.disconnect(); } catch (_) {} }
    _chatStickToBottom = true;
    _waspSnapChatToBottom();
    if (!window.ResizeObserver) return;
    sc.addEventListener('scroll', function () {
      // Within 80px of the bottom counts as "still tailing".
      _chatStickToBottom = (sc.scrollHeight - sc.scrollTop - sc.clientHeight) < 80;
      // Reaching the bottom marks the channel read + clears its badge.
      _waspMarkRead(sc);
    }, { passive: true });
    _chatObserver = new ResizeObserver(function () {
      if (_chatStickToBottom) sc.scrollTop = sc.scrollHeight;
    });
    _chatObserver.observe(sc);
    // Children's individual layout shifts (avatar load, emoji glyph
    // paint, image decode) also count.
    Array.prototype.forEach.call(sc.children, function (c) {
      try { _chatObserver.observe(c); } catch (_) {}
    });
  }

  // ── Online presence ─────────────────────────────────────────
  // Every page sends a heartbeat to /api/online-ping every 10 s and
  // polls /api/online-count every 5 s. Any element with the attribute
  // data-online-count has its textContent set to the live count, so a
  // single <span data-online-count> in the sidebar (or a sub-app's
  // header) automatically displays the number.
  function _waspOnlineSetup() {
    let p = localStorage.getItem('wasp-online-id');
    if (!p) {
      p = 'web-' + Math.random().toString(36).slice(2, 10);
      localStorage.setItem('wasp-online-id', p);
    }
    // Prefer the II-bound display name when the user has signed in;
    // fall back to a host-app-set 'wasp-online-name'; finally 'Visitor'.
    // Read fresh each ping so a mid-session sign-in / sign-out flips
    // the name on the next 10-s tick without needing a page reload.
    function _displayName() {
      return localStorage.getItem('wasp:ii:name')
          || localStorage.getItem('wasp-online-name')
          || 'Visitor';
    }

    function ping() {
      const n = _displayName();
      try {
        fetch('/api/online-ping?p=' + encodeURIComponent(p) + '&n=' + encodeURIComponent(n),
          { method: 'POST', body: '{}', headers: { 'content-type': 'application/json' } })
          .catch(() => {});
      } catch (_) { /* ignore */ }
    }
    function refresh() {
      fetch('/api/online-count').then(r => r.ok ? r.json() : null).then(j => {
        if (!j) return;
        const c = j.count || 0;
        document.querySelectorAll('[data-online-count]').forEach(el => {
          el.textContent = String(c);
        });
      }).catch(() => {});
    }
    ping(); refresh();
    setInterval(ping, 10000);
    setInterval(refresh, 5000);
  }

  // ══ Discord-parity chat enhancements ════════════════════════════
  // All client-side, layered on top of the server-rendered markup.

  // The local user's display name — used for self-mention highlight,
  // own-message edit/delete visibility, typing pings, and mark-as-read.
  function _waspLocalName() {
    // Must match the identity messages are POSTed under (the username
    // input / wasp-persist:username), so own-message styling, self-mention
    // highlight and typing all key off the same name the server stores.
    var inp = document.querySelector('input[name="username"]');
    // Strip a leading "@": a stale persisted value or an II display name
    // can carry one (e.g. "@miadey"), but message data-author never does,
    // so without this the own-message edit/delete match silently fails.
    return ((inp && inp.value)
         || localStorage.getItem('wasp-persist:username')
         || localStorage.getItem('wasp:ii:name')
         || '').trim().replace(/^@+/, '');
  }
  // Stable per-browser id (matches the online-presence ping id) used for
  // typing self-exclusion so it doesn't depend on a display name.
  function _waspClientId() {
    var p = localStorage.getItem('wasp-online-id');
    if (!p) { p = 'web-' + Math.random().toString(36).slice(2, 10); try { localStorage.setItem('wasp-online-id', p); } catch (_) {} }
    return p;
  }
  function _waspChatRoom() {
    var sc = document.getElementById('chat-scroll');
    return sc ? sc.getAttribute('data-room-id') : null;
  }

  // ── Self-mention highlight + own-message action visibility ───────
  function _waspChatDecorate() {
    var sc = document.getElementById('chat-scroll');
    if (!sc) return;
    var me = _waspLocalName().toLowerCase();
    sc.querySelectorAll('.dc-message').forEach(function (m) {
      var author = (m.getAttribute('data-author') || '').toLowerCase();
      m.classList.toggle('dc-message-own', !!me && author === me);
    });
    if (me) {
      sc.querySelectorAll('.dc-mention').forEach(function (el) {
        var t = (el.textContent || '').replace(/^@/, '').toLowerCase();
        if (t === me) {
          el.classList.add('dc-mention-self');
          var msg = el.closest('.dc-message');
          if (msg) msg.classList.add('dc-mention-me');
        }
      });
    }
    _waspApplyNewDivider(sc);
    _waspMarkRead(sc);
  }

  // ── Unread state: localStorage last-read per room + "NEW" divider ─
  var _chatAnchors = {};      // roomId -> last-read snapshot at room-open
  var _chatCurrentRoom = null;
  function _waspReadKey(room) { return 'wasp:chat:lastread:' + room; }
  function _waspGetLastRead(room) {
    try { return parseInt(localStorage.getItem(_waspReadKey(room)) || '0', 10) || 0; }
    catch (_) { return 0; }
  }
  function _waspSetLastRead(room, v) {
    try { localStorage.setItem(_waspReadKey(room), String(v)); } catch (_) {}
  }
  function _waspApplyNewDivider(sc) {
    var room = sc.getAttribute('data-room-id');
    if (!room) return;
    // Re-anchor on room switch so a returning visit doesn't show a stale
    // divider; keep the anchor stable while live in the same room.
    if (_chatCurrentRoom !== room) {
      _chatCurrentRoom = room;
      _chatAnchors[room] = _waspGetLastRead(room);
    }
    var anchor = _chatAnchors[room];
    var old = sc.querySelector('.dc-new-divider');
    if (old) old.remove();
    if (!anchor) return;   // never visited -> nothing is "new"
    // When pinned to the bottom you're caught up — don't sprout a divider
    // above the tail you're actively watching. It only appears once you've
    // scrolled up to read history while new messages land below.
    var atBottom = (sc.scrollHeight - sc.scrollTop - sc.clientHeight) < 120;
    if (atBottom) return;
    var msgs = sc.querySelectorAll('.dc-message[data-msg-id]');
    for (var i = 0; i < msgs.length; i++) {
      if (parseInt(msgs[i].getAttribute('data-msg-id'), 10) > anchor) {
        var div = document.createElement('div');
        div.className = 'dc-new-divider';
        div.innerHTML = '<span>New messages</span>';
        msgs[i].parentNode.insertBefore(div, msgs[i]);
        break;
      }
    }
  }
  function _waspMarkRead(sc) {
    var room = sc.getAttribute('data-room-id');
    if (!room) return;
    var atBottom = (sc.scrollHeight - sc.scrollTop - sc.clientHeight) < 120;
    if (!atBottom || document.hidden) return;
    var msgs = sc.querySelectorAll('.dc-message[data-msg-id]');
    var maxId = 0;
    for (var i = 0; i < msgs.length; i++) {
      var id = parseInt(msgs[i].getAttribute('data-msg-id'), 10);
      if (id > maxId) maxId = id;
    }
    if (maxId > _waspGetLastRead(room)) {
      _waspSetLastRead(room, maxId);
      // Advance the in-room anchor too, so scrolling back up through
      // now-read messages doesn't re-show a stale divider; a divider only
      // returns for messages that land while you're scrolled away.
      if (room === _chatCurrentRoom) _chatAnchors[room] = maxId;
      _waspRefreshUnread();
    }
  }
  // serverId(string) -> [roomId,...]; fetched once from /api/servers (exists).
  var _serverChannels = null;
  function _waspLoadServerChannels() {
    if (_serverChannels) return Promise.resolve(_serverChannels);
    return origFetch('/api/servers', { headers: { 'accept': 'application/json' } })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (d) {
        _serverChannels = {};
        if (d && d.servers) d.servers.forEach(function (s) { _serverChannels[String(s.id)] = (s.channelIds || []).map(String); });
        return _serverChannels;
      }).catch(function () { _serverChannels = {}; return _serverChannels; });
  }
  function _waspRefreshUnread() {
    Promise.all([
      origFetch('/api/chat/room-cursors', { headers: { 'accept': 'application/json' } }).then(function (r) { return r.ok ? r.json() : null; }),
      _waspLoadServerChannels()
    ]).then(function (res) {
      var data = res[0]; var srvCh = res[1] || {};
      if (!data || !data.rooms) return;
      var active = _waspChatRoom();
      var unreadByRoom = {};
      Object.keys(data.rooms).forEach(function (rid) {
        var diff = (data.rooms[rid] || 0) - _waspGetLastRead(rid);
        var hasUnread = diff > 0 && String(rid) !== String(active);
        unreadByRoom[String(rid)] = hasUnread ? diff : 0;
        var badge = document.querySelector('[data-room-unread="' + rid + '"]');
        if (badge) {
          badge.hidden = !hasUnread;
          badge.textContent = hasUnread ? (diff > 99 ? '99+' : String(diff)) : '';
          badge.classList.toggle('has-count', hasUnread);
        }
        var link = document.querySelector('.dc-room[data-room-id="' + rid + '"]');
        if (link) link.classList.toggle('dc-room-unread', hasUnread);
      });
      // Aggregate unread onto each server-rail icon.
      Object.keys(srvCh).forEach(function (sid) {
        var any = srvCh[sid].some(function (rid) { return (unreadByRoom[rid] || 0) > 0; });
        var rail = document.querySelector('.dc-rail-server[data-server-id="' + sid + '"]');
        if (rail) rail.classList.toggle('dc-rail-unread', any);
      });
    }).catch(function () {});
  }
  async function _waspUnreadPoll() {
    while (true) {
      _waspRefreshUnread();
      await new Promise(function (r) { setTimeout(r, document.hidden ? 12000 : 4000); });
    }
  }

  // ── Typing indicators (heap-only presence bucket, polled) ────────
  var _typingLastPing = 0;
  document.addEventListener('input', function (e) {
    var t = e.target;
    if (!t || !t.matches || !t.matches('textarea[name="text"]') || !t.value) return;
    var now = Date.now();
    if (now - _typingLastPing < 3000) return;   // throttle: never per-keystroke
    _typingLastPing = now;
    var room = _waspChatRoom();
    if (!room) return;
    var name = _waspLocalName() || 'Someone';
    var pid = _waspClientId();
    try {
      origFetch('/api/chat/typing-ping?roomId=' + encodeURIComponent(room)
        + '&n=' + encodeURIComponent(name) + '&p=' + encodeURIComponent(pid),
        { method: 'POST', body: '{}', headers: { 'content-type': 'application/json' } }).catch(function () {});
    } catch (_) {}
  });
  function _waspTypingText(names) {
    if (!names.length) return '';
    if (names.length === 1) return names[0] + ' is typing…';
    if (names.length === 2) return names[0] + ' and ' + names[1] + ' are typing…';
    if (names.length === 3) return names[0] + ', ' + names[1] + ' and ' + names[2] + ' are typing…';
    return 'Several people are typing…';
  }
  async function _waspTypingPoll() {
    while (true) {
      await new Promise(function (r) { setTimeout(r, document.hidden ? 8000 : 2000); });
      var row = document.querySelector('.dc-typing');
      var room = _waspChatRoom();
      if (!row || !room) continue;
      try {
        var resp = await origFetch('/api/chat/typing?roomId=' + encodeURIComponent(room)
          + '&self=' + encodeURIComponent(_waspClientId()),
          { headers: { 'accept': 'application/json' } });
        if (!resp.ok) continue;
        var data = await resp.json();
        var names = (data && data.names) || [];
        row.textContent = _waspTypingText(names);
        row.classList.toggle('is-active', names.length > 0);
      } catch (_) {}
    }
  }

  // ── @mention autocomplete (typeahead over the member rail) ───────
  var _acBox = null, _acItems = [], _acIndex = 0, _acStart = -1, _acTa = null;
  function _acClose() { if (_acBox) { _acBox.remove(); _acBox = null; } _acItems = []; _acIndex = 0; _acStart = -1; _acTa = null; }
  function _acNames() {
    var names = [];
    document.querySelectorAll('.dc-member[data-wasp-mention]').forEach(function (b) {
      var n = b.getAttribute('data-wasp-mention');
      if (n && names.indexOf(n) < 0) names.push(n);
    });
    return names;
  }
  function _isWordChar(c) { return /[A-Za-z0-9_\-]/.test(c); }
  function _acUpdate(ta) {
    var val = ta.value, caret = ta.selectionStart, i = caret - 1;
    while (i >= 0) {
      var c = val[i];
      if (c === '@') break;
      if (!_isWordChar(c)) { _acClose(); return; }
      i--;
    }
    if (i < 0) { _acClose(); return; }
    if (i > 0 && _isWordChar(val[i - 1])) { _acClose(); return; }   // @ must follow non-word
    var frag = val.slice(i + 1, caret).toLowerCase();
    var matches = _acNames().filter(function (n) { return n.toLowerCase().indexOf(frag) === 0; }).slice(0, 8);
    if (!matches.length) { _acClose(); return; }
    _acStart = i; _acTa = ta; _acItems = matches; _acIndex = 0;
    _acRender();
  }
  function _acRender() {
    if (!_acBox) { _acBox = document.createElement('div'); _acBox.className = 'dc-ac'; document.body.appendChild(_acBox); }
    _acBox.innerHTML = '';
    _acItems.forEach(function (n, idx) {
      var item = document.createElement('div');
      item.className = 'dc-ac-item' + (idx === _acIndex ? ' active' : '');
      item.textContent = '@' + n;
      item.addEventListener('mousedown', function (e) { e.preventDefault(); _acAccept(idx); });
      _acBox.appendChild(item);
    });
    var r = _acTa.getBoundingClientRect();
    _acBox.style.left = r.left + 'px';
    _acBox.style.width = Math.min(260, r.width) + 'px';
    // Prefer above the textarea; if it would clip off the top, flip below.
    var above = r.top - _acBox.offsetHeight - 6;
    _acBox.style.top = (above >= 4 ? above : r.bottom + 6) + 'px';
  }
  function _acAccept(idx) {
    if (!_acTa || _acStart < 0) { _acClose(); return; }
    var ta = _acTa, name = _acItems[idx], caret = ta.selectionStart;
    var before = ta.value.slice(0, _acStart), after = ta.value.slice(caret), insert = '@' + name + ' ';
    ta.value = before + insert + after;
    var pos = before.length + insert.length;
    ta.focus();
    try { ta.setSelectionRange(pos, pos); } catch (_) {}
    _acClose();
  }
  document.addEventListener('input', function (e) {
    var t = e.target;
    if (t && t.matches && t.matches('textarea[data-wasp-mention-target]')) _acUpdate(t);
  });
  // Capture phase so Enter/Tab/Arrows are intercepted before enter-to-send.
  document.addEventListener('keydown', function (e) {
    if (!_acBox) return;
    if (e.key === 'ArrowDown') { _acIndex = (_acIndex + 1) % _acItems.length; _acRender(); }
    else if (e.key === 'ArrowUp') { _acIndex = (_acIndex - 1 + _acItems.length) % _acItems.length; _acRender(); }
    else if (e.key === 'Enter' || e.key === 'Tab') { _acAccept(_acIndex); }
    else if (e.key === 'Escape') { _acClose(); }
    else return;
    e.preventDefault(); e.stopImmediatePropagation();
  }, true);

  // ── Edit message: load raw text into the composer ────────────────
  document.addEventListener('click', function (e) {
    var ed = e.target.closest && e.target.closest('[data-wasp-edit-id]');
    if (ed) {
      e.preventDefault(); e.stopPropagation();
      _waspSetEditTarget(ed.getAttribute('data-wasp-edit-id'), ed.getAttribute('data-wasp-edit-raw') || '');
      return;
    }
    var ce = e.target.closest && e.target.closest('[data-wasp-cancel-edit]');
    if (ce) { e.preventDefault(); e.stopPropagation(); _waspSetEditTarget('', ''); }
  });
  function _waspSetEditTarget(id, raw) {
    var idEl = document.querySelector('input[name="editMsgId"]');
    if (idEl) idEl.value = id || '';
    var badge = document.querySelector('.dc-edit-badge');
    var ta = document.querySelector('textarea[name="text"]');
    if (id) {
      _setReplyTarget('', '', '');   // edit + reply are mutually exclusive
      if (ta) { ta.value = raw; ta.focus(); try { ta.setSelectionRange(raw.length, raw.length); } catch (_) {} }
      if (badge) badge.classList.add('is-active');
    } else {
      if (badge) badge.classList.remove('is-active');
      if (ta) ta.value = '';
    }
  }

  // ── Image lightbox (mounted on body, survives poll swaps) ────────
  document.addEventListener('click', function (e) {
    var a = e.target.closest && e.target.closest('[data-wasp-lightbox]');
    if (!a) return;
    e.preventDefault();
    _waspOpenLightbox(a.getAttribute('data-wasp-lightbox'));
  });
  function _waspOpenLightbox(src) {
    if (!src) return;
    var existing = document.querySelector('.dc-lightbox');   // never stack overlays
    if (existing) existing.remove();
    var ov = document.createElement('div');
    ov.className = 'dc-lightbox';
    var img = document.createElement('img');
    img.src = src; img.alt = 'image';
    var btn = document.createElement('button');
    btn.className = 'dc-lightbox-close'; btn.setAttribute('aria-label', 'close'); btn.textContent = '×';
    ov.appendChild(img); ov.appendChild(btn);
    function close() { ov.remove(); document.removeEventListener('keydown', onKey); }
    function onKey(ev) { if (ev.key === 'Escape') close(); }
    ov.addEventListener('click', function (ev) { if (ev.target === ov || ev.target === btn) close(); });
    document.addEventListener('keydown', onKey);
    document.body.appendChild(ov);
  }

  // ── Video embeds (click-to-load player, mounted on body) ─────────
  // A video link renders as a [data-wasp-video] play card; the actual
  // player (YouTube/Vimeo iframe or a <video> for direct files) only
  // loads on click, into a body-level overlay that survives poll swaps
  // — so no third-party request fires from the feed until the user opts in.
  document.addEventListener('click', function (e) {
    var b = e.target.closest && e.target.closest('[data-wasp-video]');
    if (!b) return;
    e.preventDefault();
    _waspOpenVideo(b.getAttribute('data-wasp-video'), b.getAttribute('data-wasp-video-kind'));
  });
  function _waspOpenVideo(src, kind) {
    if (!src) return;
    var existing = document.querySelector('.dc-lightbox'); if (existing) existing.remove();
    var ov = document.createElement('div');
    ov.className = 'dc-lightbox dc-video-lightbox';
    var frame = document.createElement('div');
    frame.className = 'dc-video-frame';
    var media;
    if (kind === 'file') {
      media = document.createElement('video');
      media.src = src; media.controls = true; media.autoplay = true; media.playsInline = true;
      media.setAttribute('playsinline', ''); media.setAttribute('referrerpolicy', 'no-referrer');
    } else {
      media = document.createElement('iframe');
      media.src = src + (src.indexOf('?') >= 0 ? '&' : '?') + 'autoplay=1';
      media.setAttribute('allow', 'autoplay; encrypted-media; picture-in-picture; fullscreen');
      media.setAttribute('allowfullscreen', '');
      // NOTE: do NOT set referrerpolicy=no-referrer here — YouTube/Vimeo embeds
      // validate the embedding origin via the Referer and return Error 153
      // without it. youtube-nocookie.com already gives privacy-enhanced (no
      // cookies/tracking until play); the referrer just authorizes the embed.
      media.setAttribute('sandbox', 'allow-scripts allow-same-origin allow-presentation allow-popups');
    }
    var btn = document.createElement('button');
    btn.className = 'dc-lightbox-close'; btn.setAttribute('aria-label', 'close'); btn.textContent = '×';
    frame.appendChild(media); ov.appendChild(frame); ov.appendChild(btn);
    function close() { ov.remove(); document.removeEventListener('keydown', onKey); }
    function onKey(ev) { if (ev.key === 'Escape') close(); }
    ov.addEventListener('click', function (ev) { if (ev.target === ov || ev.target === btn) close(); });
    document.addEventListener('keydown', onKey);
    document.body.appendChild(ov);
  }

  // ── Spoiler reveal (client-side only) ────────────────────────────
  // ||spoiler|| renders as <span class='dc-spoiler' data-wasp-spoiler>.
  // First click / Enter / Space reveals it (adds .dc-revealed). Body-level
  // delegation survives poll DOM swaps; carries no data-wasp-args so a
  // reveal never fires a server update.
  function _waspRevealSpoiler(sp) {
    if (!sp || sp.classList.contains('dc-revealed')) return;
    sp.classList.add('dc-revealed');
    sp.removeAttribute('role'); sp.removeAttribute('tabindex'); sp.setAttribute('aria-label', '');
  }
  document.addEventListener('click', function (e) {
    var sp = e.target.closest && e.target.closest('[data-wasp-spoiler]');
    if (!sp || sp.classList.contains('dc-revealed')) return;
    e.preventDefault(); e.stopPropagation();
    _waspRevealSpoiler(sp);
  });
  document.addEventListener('keydown', function (e) {
    if (e.key !== 'Enter' && e.key !== ' ' && e.key !== 'Spacebar') return;
    var sp = e.target.closest && e.target.closest('[data-wasp-spoiler]');
    if (!sp || sp.classList.contains('dc-revealed')) return;
    e.preventDefault();
    _waspRevealSpoiler(sp);
  });

  // ── Cmd-K global message search palette ──────────────────────────
  // Body-mounted overlay (survives poll swaps). Hits from the free query
  // GET /api/chat/search?q= ; jump via SPA to /chat?r=<name> (?r= resolves
  // by channel NAME server-side). 150ms debounce, arrow/Enter/Esc nav.
  var _ckBox = null, _ckHits = [], _ckIndex = 0, _ckSeq = 0, _ckTimer = null;
  function _ckClose() {
    if (_ckBox) { _ckBox.remove(); _ckBox = null; }
    _ckHits = []; _ckIndex = 0;
    if (_ckTimer) { clearTimeout(_ckTimer); _ckTimer = null; }
  }
  function _ckOpen() {
    if (_ckBox) { var i0 = _ckBox.querySelector('.ck-input'); if (i0) i0.focus(); return; }
    var ov = document.createElement('div');
    ov.className = 'ck-backdrop';
    ov.innerHTML =
      '<div class="ck-palette" role="dialog" aria-label="Search messages">' +
        '<div class="ck-input-row">' +
          '<span class="ck-icon" aria-hidden="true">⌕</span>' +
          '<input class="ck-input" type="text" placeholder="Search messages…" autocomplete="off" spellcheck="false">' +
          '<span class="ck-hint">esc</span>' +
        '</div>' +
        '<div class="ck-results"><div class="ck-empty">Start typing to search.</div></div>' +
      '</div>';
    ov.addEventListener('mousedown', function (e) { if (e.target === ov) _ckClose(); });
    document.body.appendChild(ov);
    _ckBox = ov;
    var input = ov.querySelector('.ck-input');
    input.addEventListener('input', function () { _ckOnTyped(input.value); });
    input.focus();
  }
  function _ckOnTyped(q) {
    if (_ckTimer) clearTimeout(_ckTimer);
    var query = (q || '').trim();
    if (!query) { _ckRender(null); return; }
    _ckTimer = setTimeout(function () {
      var seq = ++_ckSeq;
      origFetch('/api/chat/search?q=' + encodeURIComponent(query), { headers: { 'accept': 'application/json' } })
        .then(function (r) { return r.ok ? r.json() : { results: [] }; })
        .then(function (data) { if (seq !== _ckSeq || !_ckBox) return; _ckRender((data && data.results) || []); })
        .catch(function () {});
    }, 150);
  }
  function _ckRender(hits) {
    if (!_ckBox) return;
    var list = _ckBox.querySelector('.ck-results');
    if (hits === null) { _ckHits = []; _ckIndex = 0; list.innerHTML = '<div class="ck-empty">Start typing to search.</div>'; return; }
    _ckHits = hits; _ckIndex = 0;
    if (!hits.length) { list.innerHTML = '<div class="ck-empty">No matches.</div>'; return; }
    list.innerHTML = '';
    hits.forEach(function (h, idx) {
      var row = document.createElement('button');
      row.type = 'button';
      row.className = 'ck-hit' + (idx === 0 ? ' active' : '');
      var top = document.createElement('div'); top.className = 'ck-hit-top';
      var ch = document.createElement('span'); ch.className = 'ck-hit-channel'; ch.textContent = '#' + (h.channelName || '');
      var au = document.createElement('span'); au.className = 'ck-hit-author'; au.textContent = h.author || '';
      top.appendChild(ch); top.appendChild(au);
      var snip = document.createElement('div'); snip.className = 'ck-hit-snippet'; snip.textContent = h.snippet || '';
      row.appendChild(top); row.appendChild(snip);
      row.addEventListener('mousedown', function (e) { e.preventDefault(); _ckGo(idx); });
      list.appendChild(row);
    });
  }
  function _ckHighlight() {
    if (!_ckBox) return;
    _ckBox.querySelectorAll('.ck-hit').forEach(function (r, i) {
      var on = (i === _ckIndex);
      r.classList.toggle('active', on);
      if (on && r.scrollIntoView) r.scrollIntoView({ block: 'nearest' });
    });
  }
  function _ckGo(idx) {
    var h = _ckHits[idx];
    if (!h) return;
    _ckClose();
    _navigateTo('/chat?r=' + encodeURIComponent(h.channelName), true);
  }
  document.addEventListener('keydown', function (e) {
    if ((e.metaKey || e.ctrlKey) && !e.altKey && !e.shiftKey && (e.key === 'k' || e.key === 'K')) {
      e.preventDefault();
      if (_ckBox) _ckClose(); else _ckOpen();
    }
  });
  document.addEventListener('keydown', function (e) {
    if (!_ckBox) return;
    if (e.key === 'Escape') { _ckClose(); }
    else if (e.key === 'ArrowDown') { if (_ckHits.length) { _ckIndex = (_ckIndex + 1) % _ckHits.length; _ckHighlight(); } }
    else if (e.key === 'ArrowUp') { if (_ckHits.length) { _ckIndex = (_ckIndex - 1 + _ckHits.length) % _ckHits.length; _ckHighlight(); } }
    else if (e.key === 'Enter') { if (_ckHits.length) _ckGo(_ckIndex); }
    else return;
    e.preventDefault(); e.stopImmediatePropagation();
  }, true);

  // ── Paste-to-upload + drag-and-drop upload ───────────────────────
  function _waspIngestImageFile(file) {
    if (!file || !/^image\//.test(file.type)) { alert('Images only.'); return; }
    if (file.size > 1000000) { alert('Image is too big (max 1 MB). Got ' + Math.round(file.size / 1024) + ' KB.'); return; }
    var target = document.querySelector('input[name="imageData"]');
    var preview = document.querySelector('[data-wasp-image-preview]');
    var fr = new FileReader();
    fr.onload = function () {
      if (target) target.value = fr.result;
      if (preview) {
        var img = preview.querySelector('img') || preview.appendChild(document.createElement('img'));
        img.src = fr.result; preview.classList.add('is-active');
      }
      var ta = document.querySelector('textarea[name="text"]'); if (ta) ta.focus();
    };
    fr.readAsDataURL(file);
  }
  document.addEventListener('paste', function (e) {
    if (!document.getElementById('chat-scroll')) return;
    var items = e.clipboardData && e.clipboardData.items;
    if (!items) return;
    for (var i = 0; i < items.length; i++) {
      if (items[i].type && items[i].type.indexOf('image/') === 0) {
        var file = items[i].getAsFile();
        if (file) { _waspIngestImageFile(file); e.preventDefault(); }
        return;
      }
    }
  });
  document.addEventListener('dragover', function (e) {
    if (!document.getElementById('chat-scroll')) return;
    var types = (e.dataTransfer && e.dataTransfer.types) || [];
    if (Array.prototype.indexOf.call(types, 'Files') >= 0) { e.preventDefault(); document.body.classList.add('dc-dragging'); }
  });
  document.addEventListener('dragleave', function (e) { if (e.relatedTarget === null) document.body.classList.remove('dc-dragging'); });
  document.addEventListener('drop', function (e) {
    document.body.classList.remove('dc-dragging');
    if (!document.getElementById('chat-scroll')) return;
    var files = e.dataTransfer && e.dataTransfer.files;
    if (files && files.length) {
      for (var i = 0; i < files.length; i++) {
        if (files[i].type && files[i].type.indexOf('image/') === 0) { _waspIngestImageFile(files[i]); break; }
      }
      e.preventDefault();
    }
  });

  function _waspHydrate() {
    _wireEvents();
    _waspPersistRestore();
    _waspScanBindings();
    _waspChatScrollWatch();
    _waspChatDecorate();
    _reactivityPoll();
    _waspPollStores();
    _waspOnlineSetup();
    _waspTypingPoll();
    _waspUnreadPoll();
  }
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', _waspHydrate);
  } else {
    _waspHydrate();
  }
})();
