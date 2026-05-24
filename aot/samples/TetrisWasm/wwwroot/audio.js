// Korobeiniki (the Tetris A theme — the underlying Russian folk
// melody is public domain) played with a Web Audio square wave.
// Exposed as window.tetrisAudio = { start, stop, mute }.
(function () {
  var NOTES = [
    { f: 659.25, ms: 400 }, { f: 493.88, ms: 200 }, { f: 523.25, ms: 200 },
    { f: 587.33, ms: 400 }, { f: 523.25, ms: 200 }, { f: 493.88, ms: 200 },
    { f: 440.00, ms: 400 }, { f: 440.00, ms: 200 }, { f: 523.25, ms: 200 },
    { f: 659.25, ms: 400 }, { f: 587.33, ms: 200 }, { f: 523.25, ms: 200 },
    { f: 493.88, ms: 600 }, { f: 523.25, ms: 200 }, { f: 587.33, ms: 400 },
    { f: 659.25, ms: 400 }, { f: 523.25, ms: 400 }, { f: 440.00, ms: 400 },
    { f: 440.00, ms: 400 }, { f: 0,      ms: 400 },
  ];

  var ctx = null;
  var master = null;
  var nextLoopTimer = null;
  var running = false;

  function ensureCtx() {
    if (ctx) return ctx;
    var AC = window.AudioContext || window.webkitAudioContext;
    if (!AC) return null;
    ctx = new AC();
    master = ctx.createGain();
    master.gain.value = 0.08;       // soft — square waves get harsh at full
    master.connect(ctx.destination);
    return ctx;
  }

  function scheduleLoop(at) {
    if (!running) return;
    var t = at;
    NOTES.forEach(function (n) {
      if (n.f > 0) {
        var osc = ctx.createOscillator();
        osc.type = 'square';
        osc.frequency.value = n.f;
        var g = ctx.createGain();
        // Quick fade in/out to avoid clicks at note boundaries.
        g.gain.setValueAtTime(0.0001, t);
        g.gain.exponentialRampToValueAtTime(1.0, t + 0.012);
        g.gain.setValueAtTime(1.0, t + n.ms / 1000 - 0.02);
        g.gain.exponentialRampToValueAtTime(0.0001, t + n.ms / 1000);
        osc.connect(g);
        g.connect(master);
        osc.start(t);
        osc.stop(t + n.ms / 1000 + 0.02);
      }
      t += n.ms / 1000;
    });
    var loopDurationMs = NOTES.reduce(function (s, n) { return s + n.ms; }, 0);
    nextLoopTimer = setTimeout(function () { scheduleLoop(ctx.currentTime); },
                               loopDurationMs - 50);
  }

  window.tetrisAudio = {
    start: function () {
      if (running) return;
      var c = ensureCtx();
      if (!c) return;
      if (c.state === 'suspended') c.resume();
      running = true;
      scheduleLoop(c.currentTime + 0.05);
    },
    stop: function () {
      running = false;
      if (nextLoopTimer) { clearTimeout(nextLoopTimer); nextLoopTimer = null; }
      if (ctx && master) {
        // Soft fade to silence to avoid abrupt cuts.
        master.gain.cancelScheduledValues(ctx.currentTime);
        master.gain.setValueAtTime(master.gain.value, ctx.currentTime);
        master.gain.linearRampToValueAtTime(0.0001, ctx.currentTime + 0.1);
        setTimeout(function () {
          if (master) master.gain.value = 0.08;
        }, 200);
      }
    },
  };
})();

// ── Online presence ────────────────────────────────────────────────
// Heartbeat to /api/online-ping every 10s and refresh [data-online-count]
// every 5s. Same mechanism the BlazorWasp shell and CRM use, so the
// Tetris start screen shows the canister-wide active-user count.
(function() {
    let p = localStorage.getItem('wasp-online-id');
    if (!p) {
        p = 'web-' + Math.random().toString(36).slice(2, 10);
        localStorage.setItem('wasp-online-id', p);
    }
    const n = localStorage.getItem('wasp-online-name') || 'Tetris player';
    const ping = () => {
        fetch('/api/online-ping?p=' + encodeURIComponent(p) + '&n=' + encodeURIComponent(n),
            { method: 'POST', body: '{}', headers: { 'content-type': 'application/json' } })
            .catch(() => {});
    };
    const refresh = () => {
        fetch('/api/online-count').then(r => r.ok ? r.json() : null).then(j => {
            if (!j) return;
            document.querySelectorAll('[data-online-count]').forEach(el => {
                el.textContent = String(j.count || 0);
            });
        }).catch(() => {});
    };
    ping(); refresh();
    setInterval(ping, 10000);
    setInterval(refresh, 5000);
})();
