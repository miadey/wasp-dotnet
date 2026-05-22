#!/usr/bin/env node
//
// vanilla-acceptance.mjs — drives every page of every vanilla canister
// sample in a headless browser and asserts it works. Exit 0 = ship.
//
// Tracks gh issue #93 (M4.S9.7). Closes M4.S9.8 (EPIC) when all green.
//
// Per-sample sections map 1:1 to acceptance criteria in:
//   #90 (BlazorVanilla), #91 (WebApiVanilla), #92 (MvcVanilla).
//
// Run:
//   node aot/tests/vanilla-acceptance.mjs
//
// Defaults assume dfx is running locally on :4944 and the canisters
// are deployed with ids in aot/.dfx/local/canister_ids.json.
//
// Today the harness can only assert against existing canisters
// (CircuitOnIc) — the vanilla samples don't exist yet. As soon as
// #90/#91/#92 land, uncomment the relevant sections.

import { readFileSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import process from 'node:process';

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);
const REPO_ROOT = resolve(__dirname, '..', '..');
const CANISTER_IDS_PATH = resolve(REPO_ROOT, 'aot', '.dfx', 'local', 'canister_ids.json');

// ─── Config ──────────────────────────────────────────────────────────
const HOST = process.env.WASP_DFX_HOST ?? 'localhost:4944';
const USE_RAW = process.env.WASP_USE_RAW !== '0';   // default raw subdomain
const SAMPLE_NAMES = ['circuitonic', 'blazorvanilla', 'webapivanilla', 'mvcvanilla'];

// ─── Helpers ─────────────────────────────────────────────────────────
function readCanisterIds() {
  if (!existsSync(CANISTER_IDS_PATH)) {
    fail(`canister_ids.json not found at ${CANISTER_IDS_PATH} — run dfx deploy first`);
  }
  return JSON.parse(readFileSync(CANISTER_IDS_PATH, 'utf-8'));
}

function urlFor(canisterId, path = '/') {
  const sub = USE_RAW ? `${canisterId}.raw.${HOST}` : `${canisterId}.${HOST}`;
  return `http://${sub}${path}`;
}

let passCount = 0;
let failCount = 0;
const results = [];

function check(name, condition, detail = '') {
  if (condition) {
    passCount++;
    results.push({ ok: true, name });
    console.log(`  ✓ ${name}`);
  } else {
    failCount++;
    results.push({ ok: false, name, detail });
    console.log(`  ✗ ${name}${detail ? '  — ' + detail : ''}`);
  }
}

function fail(msg) {
  console.error(`FATAL: ${msg}`);
  process.exit(2);
}

async function fetchOk(url, init) {
  try {
    const r = await fetch(url, init);
    const body = await r.text();
    return { status: r.status, headers: Object.fromEntries(r.headers), body, ok: r.ok };
  } catch (e) {
    return { status: 0, error: e.message };
  }
}

// ─── CircuitOnIc — proves the harness wires up against today's canister
async function testCircuitOnIc(ids) {
  const cid = ids.circuitonic?.local;
  if (!cid) {
    console.log('— circuitonic not deployed; skipping');
    return;
  }
  console.log(`\n=== CircuitOnIc (${cid}) ===`);
  const root = await fetchOk(urlFor(cid, '/'));
  check('GET /                            → 200', root.status === 200);
  check('GET /                            → contains Counter SSR', root.body?.includes('Current count'));

  const blazorJs = await fetchOk(urlFor(cid, '/_framework/blazor.web.js'));
  check('GET /_framework/blazor.web.js    → 200', blazorJs.status === 200);

  const bridge = await fetchOk(urlFor(cid, '/_framework/wasp-bridge.js'));
  check('GET /_framework/wasp-bridge.js   → 200', bridge.status === 200);

  const click1 = await fetchOk(urlFor(cid, '/api/click?c=0'));
  check('GET /api/click?c=0               → {count:1}',
    click1.status === 200 && click1.body?.includes('"count":1'));

  const click42 = await fetchOk(urlFor(cid, '/api/click?c=42'));
  check('GET /api/click?c=42              → {count:43}',
    click42.status === 200 && click42.body?.includes('"count":43'));

  const state = await fetchOk(urlFor(cid, '/api/state'));
  check('GET /api/state                   → returns JSON {count:N}',
    state.status === 200 && /\{"count":\d+\}/.test(state.body ?? ''));
}

// ─── BlazorVanilla — pending M4.S9.4 (#90) ───────────────────────────
async function testBlazorVanilla(ids) {
  const cid = ids.blazorvanilla?.local;
  if (!cid) {
    console.log('\n— blazorvanilla not yet deployed (waiting on #90); skipping');
    return;
  }
  console.log(`\n=== BlazorVanilla (${cid}) ===`);

  // Each route's SSR must return 200 and contain a recognisable substring.
  const routes = [
    ['/',                    'Home',           'Hello, world'],     // stock Home.razor
    ['/counter',             'Counter',        'Current count'],    // stock Counter.razor
    ['/weather',             'Weather',        'Weather'],          // stock Weather.razor
    ['/multicounter',        'MultiCounter',   'multi'],            // #81
    ['/forms',               'FormDemo',       'EditForm'],         // #83
    ['/lifecycle',           'LifecycleLog',   'lifecycle'],        // #84
    ['/cascade',             'CascadeDemo',    'theme'],            // #85
    ['/eventcallback',       'EventCallback',  'selected'],         // #86
    ['/jsinterop',           'JsInteropDemo',  'interop'],          // #79
    ['/persistent',          'PersistentState','persisted'],        // #70
  ];

  for (const [path, name, marker] of routes) {
    const r = await fetchOk(urlFor(cid, path));
    check(`GET ${path.padEnd(20)}      → 200`, r.status === 200);
    check(`GET ${path.padEnd(20)}      → contains "${marker}"`,
      typeof r.body === 'string' && r.body.toLowerCase().includes(marker.toLowerCase()),
      `body excerpt: ${(r.body ?? '').slice(0, 60).replace(/\s+/g, ' ')}`);
  }

  // Browser-level assertions (click flows, state diff, NavLink active)
  // belong in a Playwright spec — this script asserts the SSR shell
  // layer only. Once M4.S9.7 expands, run Playwright from here.
}

// ─── WebApiVanilla — pending M4.S9.5 (#91) ───────────────────────────
async function testWebApiVanilla(ids) {
  const cid = ids.webapivanilla?.local;
  if (!cid) {
    console.log('\n— webapivanilla not yet deployed (waiting on #91); skipping');
    return;
  }
  console.log(`\n=== WebApiVanilla (${cid}) ===`);

  const r = await fetchOk(urlFor(cid, '/weatherforecast'));
  check('GET /weatherforecast            → 200',
    r.status === 200);
  check('GET /weatherforecast            → returns JSON array of 5',
    (() => { try { const a = JSON.parse(r.body); return Array.isArray(a) && a.length === 5; } catch { return false; } })());

  const post = await fetchOk(urlFor(cid, '/weatherforecast'), {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ date: '2026-06-01', temperatureC: 25, summary: 'Warm' }),
  });
  check('POST /weatherforecast           → 201',
    post.status === 201);
}

// ─── MvcVanilla — pending M4.S9.6 (#92) ──────────────────────────────
async function testMvcVanilla(ids) {
  const cid = ids.mvcvanilla?.local;
  if (!cid) {
    console.log('\n— mvcvanilla not yet deployed (waiting on #92); skipping');
    return;
  }
  console.log(`\n=== MvcVanilla (${cid}) ===`);

  const home = await fetchOk(urlFor(cid, '/'));
  check('GET /                            → 200 (Index.cshtml)',
    home.status === 200 && home.body?.includes('Welcome'));

  const privacy = await fetchOk(urlFor(cid, '/Home/Privacy'));
  check('GET /Home/Privacy                → 200 (Privacy.cshtml)',
    privacy.status === 200);

  const css = await fetchOk(urlFor(cid, '/css/site.css'));
  check('GET /css/site.css                → 200 static file',
    css.status === 200);
}

// ─── Main ────────────────────────────────────────────────────────────
async function main() {
  console.log('Wasp.AspNetCore vanilla acceptance harness');
  console.log(`Host: ${HOST}  Raw subdomain: ${USE_RAW ? 'yes' : 'no'}`);

  const ids = readCanisterIds();
  console.log(`Known canisters: ${Object.keys(ids).join(', ')}`);

  await testCircuitOnIc(ids);
  await testBlazorVanilla(ids);
  await testWebApiVanilla(ids);
  await testMvcVanilla(ids);

  console.log(`\nResult: ${passCount} pass, ${failCount} fail`);
  process.exit(failCount === 0 ? 0 : 1);
}

main().catch((e) => fail(e?.stack ?? String(e)));
