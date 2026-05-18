# Wasp acceptance tests

Tracks gh issue [#93](https://github.com/miadey/wasp-dotnet/issues/93) (M4.S9.7).
Closes [#94](https://github.com/miadey/wasp-dotnet/issues/94) (EPIC) when all
sections green.

## Layout

```
aot/tests/
  vanilla-acceptance.mjs   # the main harness
  README.md                # this file
  run.sh                   # convenience launcher (assumes dfx is up)
```

## Running

```bash
cd /Users/miadey/dev/csharp
node aot/tests/vanilla-acceptance.mjs
# or
bash aot/tests/run.sh
```

Set `WASP_USE_RAW=0` to hit the standard (cert-required) subdomain
instead of `.raw.localhost`. Cert tree isn't shipped yet so this
will fail until issue [#61](https://github.com/miadey/wasp-dotnet/issues/61)
closes.

## What it asserts

Per sample (each section runs only if the canister id exists in
`aot/.dfx/local/canister_ids.json` — missing canisters skip):

| Sample | Section | Tracking issue |
|---|---|---|
| `circuitonic` | static assets + query-RPC + state endpoints | (live today) |
| `blazorvanilla` | every page in `Components/Pages/*.razor` returns 200 with expected SSR marker | [#90](https://github.com/miadey/wasp-dotnet/issues/90) |
| `webapivanilla` | `GET /weatherforecast` returns 5 rows; `POST` round-trips | [#91](https://github.com/miadey/wasp-dotnet/issues/91) |
| `mvcvanilla` | views render with layout; static `/css/site.css` serves; form post round-trips | [#92](https://github.com/miadey/wasp-dotnet/issues/92) |

Click-flow / NavLink active-class / SignalR hydration assertions belong
in a Playwright spec — the harness here asserts the SSR shell layer
only. The Playwright layer lands alongside [#90](https://github.com/miadey/wasp-dotnet/issues/90).

## Exit codes

- `0` — all sections that had a deployed canister passed.
- `1` — at least one assertion failed.
- `2` — fatal precondition (canister_ids.json missing, dfx not running, etc.).

## CI hook (future)

```yaml
- name: vanilla acceptance
  run: |
    dfx start --background --clean
    cd aot/samples/BlazorVanilla && ./build-and-deploy.sh
    cd ../WebApiVanilla        && ./build-and-deploy.sh
    cd ../MvcVanilla           && ./build-and-deploy.sh
    node aot/tests/vanilla-acceptance.mjs
```
