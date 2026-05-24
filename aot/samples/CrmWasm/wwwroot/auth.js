// Internet Identity bootstrap for the CRM. Loads @dfinity/auth-client as
// an ES module from esm.sh (which produces a working ESM bundle including
// transitive deps — jsdelivr's `+esm` shim has been flaky for this package
// because the agent-js tree pulls in @noble/hashes which doesn't always
// re-export cleanly).
//
// Anonymous browsing still works; II login is gated by the user clicking
// the button. After login we hand the principal text back to .NET via
// IJSRuntime; ApiClient attaches it as X-Wasp-Principal on every request.
//
// Trust model — server-trusted header for the demo. Production-grade
// auth would use Candid-signed ingress.

const LOG = "[crm-auth]";
let client = null;
let modulePromise = null;
let lastError = null;

// Install eager stubs so window.crmAuth is *always* defined the instant
// any Blazor JS interop call lands. Without these, a click that races
// the ESM import throws "Cannot read properties of undefined" and Blazor
// swallows it via the try/catch in AuthService.LoginAsync — looking to
// the user like a dead button.
window.crmAuth = {
    init: async () => { await ensureLoaded(); return doInit(); },
    login: async () => { await ensureLoaded(); return doLogin(); },
    logout: async () => { await ensureLoaded(); return doLogout(); },
    lastError: () => lastError,
};

async function ensureLoaded() {
    if (modulePromise) return modulePromise;
    modulePromise = (async () => {
        // Pinning EVERY transitive @dfinity/* dep to the same 2.2.0 version
        // avoids esm.sh resolving (e.g.) @dfinity/candid to a newer version
        // that doesn't yet export the symbols @dfinity/auth-client@2.2.0
        // imports (`bufFromBufLike` etc). Without ?deps= the CDN picks
        // the latest matching version range and the bundles disagree.
        const urls = [
            "https://esm.sh/@dfinity/auth-client@2.2.0?deps=@dfinity/agent@2.2.0,@dfinity/candid@2.2.0,@dfinity/identity@2.2.0,@dfinity/principal@2.2.0&bundle",
            // Fallback: older line of the SDK; if 2.x has CDN trouble we
            // fall back to a self-consistent 1.4.x tree.
            "https://esm.sh/@dfinity/auth-client@1.4.0?deps=@dfinity/agent@1.4.0,@dfinity/candid@1.4.0,@dfinity/identity@1.4.0,@dfinity/principal@1.4.0&bundle",
        ];
        let lastErr = null;
        for (const url of urls) {
            try {
                console.log(LOG, "loading", url);
                const mod = await import(url);
                console.log(LOG, "loaded auth-client module from", url);
                return mod;
            } catch (e) {
                console.error(LOG, "import failed:", url, e);
                lastErr = e;
            }
        }
        lastError = "all CDNs failed: " + (lastErr?.message ?? lastErr);
        throw lastErr;
    })();
    return modulePromise;
}

async function doInit() {
    try {
        const { AuthClient } = await ensureLoaded();
        if (!client) client = await AuthClient.create();
        if (await client.isAuthenticated()) {
            const id = client.getIdentity();
            const p = id.getPrincipal().toText();
            console.log(LOG, "already authenticated as", p);
            return p;
        }
        return null;
    } catch (e) {
        console.error(LOG, "init failed", e);
        lastError = "init failed: " + (e?.message ?? e);
        return null;
    }
}

async function doLogin() {
    try {
        const { AuthClient } = await ensureLoaded();
        if (!client) client = await AuthClient.create();
        console.log(LOG, "opening II popup…");
        return await new Promise((resolve) => {
            client.login({
                identityProvider: "https://identity.ic0.app",
                maxTimeToLive: BigInt(24 * 60 * 60 * 1_000_000_000),  // 24h
                windowOpenerFeatures: "width=520,height=720",
                onSuccess: () => {
                    const id = client.getIdentity();
                    const p = id.getPrincipal().toText();
                    console.log(LOG, "login success", p);
                    resolve(p);
                },
                onError: (err) => {
                    console.error(LOG, "login error", err);
                    lastError = "login error: " + (err ?? "unknown");
                    resolve(null);
                },
            });
        });
    } catch (e) {
        console.error(LOG, "login threw", e);
        lastError = "login threw: " + (e?.message ?? e);
        return null;
    }
}

async function doLogout() {
    try {
        if (!client) return;
        await client.logout();
        client = null;
        console.log(LOG, "logged out");
    } catch (e) {
        console.error(LOG, "logout failed", e);
    }
}

// ── Global hotkeys: Cmd-K / Ctrl-K opens the search palette. ──
let hotkeyHandler = null;
window.crmHotkeys = {
    install(dotnetRef) {
        if (hotkeyHandler) document.removeEventListener("keydown", hotkeyHandler);
        hotkeyHandler = (e) => {
            const k = e.key?.toLowerCase();
            if ((e.metaKey || e.ctrlKey) && k === "k") {
                e.preventDefault();
                dotnetRef.invokeMethodAsync("OpenSearch");
            } else if (e.key === "/" && !["INPUT","TEXTAREA","SELECT"].includes(document.activeElement?.tagName)) {
                e.preventDefault();
                dotnetRef.invokeMethodAsync("OpenSearch");
            }
        };
        document.addEventListener("keydown", hotkeyHandler);
    },
    uninstall() {
        if (hotkeyHandler) {
            document.removeEventListener("keydown", hotkeyHandler);
            hotkeyHandler = null;
        }
    },
};

// Eager-warm the import so the user's click doesn't have to wait for the
// ~300 KB module download (it can already be cached / ready).
ensureLoaded().catch(() => { /* logged inside */ });

// ── Online presence ────────────────────────────────────────────────
// Heartbeat to the same /api/online-ping endpoint the BlazorWasp shell
// uses, and update any [data-online-count] element on the CRM page. The
// /crm SPA doesn't load wasp.js so we replicate that logic here. Same
// principal id is shared in localStorage so refreshing /crm doesn't
// double-count.
(function() {
    let p = localStorage.getItem('wasp-online-id');
    if (!p) {
        p = 'web-' + Math.random().toString(36).slice(2, 10);
        localStorage.setItem('wasp-online-id', p);
    }
    const n = localStorage.getItem('wasp-online-name') || 'CRM user';
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
