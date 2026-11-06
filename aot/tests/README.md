# Local HTTP acceptance harness

For all test layers and the dated baseline, see [Testing](../../docs/TESTING.md).
This folder contains `vanilla-acceptance.mjs` and its `run.sh` convenience wrapper.

From the outer repository root, after deploying the intended samples locally:

```bash
node aot/tests/vanilla-acceptance.mjs
```

The harness reads `aot/.dfx/local/canister_ids.json` and uses Node's `fetch`.
It checks HTTP responses/SSR markers, not a headless browser or user clicks.

| Canister alias | Checks |
| --- | --- |
| `circuitonic` | Shell, framework assets, query/RPC responses |
| `blazorvanilla` | Expected rendered page markers |
| `webapivanilla` | API responses and writes |
| `mvcvanilla` | Rendered views, assets, and form response |

Missing canister IDs cause individual sections to skip. Report the skipped
sections: exit zero does not mean every sample was deployed and accepted.

Configuration:

- `WASP_DFX_HOST`: defaults to `localhost:4944`.
- `WASP_USE_RAW`: defaults to raw subdomains; set `0` for canonical subdomains.
  A pass against a raw endpoint does not validate certification.
- Exit `0`: no assertions failed in executed sections.
- Exit `1`: at least one assertion failed.
- Exit `2`: fatal setup failure.

The GitHub workflow does not currently deploy these samples or run this harness.
The current Bzzz application requires separate validation; this harness does not
cover its identity, permissions, private messages, or upgrade compatibility.
