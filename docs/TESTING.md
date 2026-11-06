# Testing and validation

Run commands from the outer repository root unless stated otherwise. Distinguish
host tests, Wasm builds, replica tests, and browser acceptance: they prove
different things.

## Host unit tests

Requires .NET SDK 10. These commands do not deploy canisters:

```bash
dotnet test aot/Wasp.AspNetCore.Tests/Wasp.AspNetCore.Tests.csproj -c Release --nologo
dotnet test aot/Wasp.AspNetCore.Blazor.Server.Tests/Wasp.AspNetCore.Blazor.Server.Tests.csproj -c Release --nologo
dotnet test aot/Wasp.AspNetCore.Blazor.Wasp.Tests/Wasp.AspNetCore.Blazor.Wasp.Tests.csproj -c Release --nologo
dotnet test aot/Wasp.OrthogonalPersistence.Tests/Wasp.OrthogonalPersistence.Tests.csproj -c Release --nologo
```

### Observed baseline — 2026-11-06

Run with host SDK `10.0.202`, against the application/framework source published
at `a6e26c1` (before documentation-only maintenance):

| Suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| `Wasp.AspNetCore.Tests` | 7 | 0 | 0 |
| `Wasp.AspNetCore.Blazor.Server.Tests` | 126 | 0 | 0 |
| `Wasp.AspNetCore.Blazor.Wasp.Tests` | 10 | 1 | 0 |
| `Wasp.OrthogonalPersistence.Tests` | 4 | 0 | 0 |
| **Total** | **147** | **1** | **0** |

Existing failure:
[`WaspRouterTests.Path_normalisation_strips_trailing_slash_and_query`](../aot/Wasp.AspNetCore.Blazor.Wasp.Tests/WaspRouterTests.cs).
The test expects the same `BatchId` for `/counter`, `/counter/`, and
`/counter?x=1`; the current renderer hashes the original request path plus HTML.
Its comment explains that query differences are intended to distinguish rooms.
Resolve that contract explicitly before changing the implementation or expected
result. The documentation refresh does not fix or suppress the failure.

These suites exercise root framework code. They do not automatically test the
copied Bzzz framework, actual `ic0` imports, deployment persistence, or a browser.
Some builds emit existing nullable/internal-Blazor-API warnings.

## JavaScript decoder cross-check

```bash
cd aot/Wasp.AspNetCore.Blazor.Server.JsDecode
npm ci
dotnet run --project Emit -- vectors.json
npm test
```

`package-lock.json` is versioned so dependency resolution is reproducible.
`vectors.json` is generated and ignored. On 2026-11-06 the emitter generated
11 vectors and `npm test` decoded **11/11** successfully, including after a clean
`npm ci --ignore-scripts` with the committed lockfile and a temporary npm cache.
The lockfile includes `ws` 7.5.13; the earlier local lock used a version flagged
by npm audit. The final install reported zero known vulnerabilities in this
test package at that time. This is not a security audit of Bzzz.
This checks the writer against the SignalR MessagePack
decoder; it is not full Blazor browser-session acceptance.

## Basic AOT build validation

After [toolchain setup](../CONTRIBUTING.md#toolchain):

```bash
SAMPLES=HelloWorld bash .github/workflows/build-canisters.sh
# Or the five samples selected by the script:
bash .github/workflows/build-canisters.sh
```

The default list is `HelloWorld Counter HelloWeb HelloFetch HelloChat`. The
script compiles, post-processes, validates Wasm, and applies a 10 MiB budget
(code-section estimate with a total-file-size fallback). It does not deploy.
This budget is a repository policy, not a statement of the target subnet's
current protocol limits.

[GitHub CI](../.github/workflows/ci.yml) runs this build and publishes
`BlazorChat`. Host xUnit suites, the decoder check, Bzzz, and the Mono runtime
experiment are not currently included in that workflow. A green workflow is
therefore not full-repository acceptance.

## Local HTTP acceptance harness

After deploying the intended samples locally:

```bash
node aot/tests/vanilla-acceptance.mjs
```

It reads `aot/.dfx/local/canister_ids.json`. Sections cover `circuitonic`,
`blazorvanilla`, `webapivanilla`, and `mvcvanilla`; a missing canister ID causes
that section to skip. Report skips explicitly: an exit code of zero with
undeployed sections does not prove those samples work.

The harness uses Node `fetch` to check HTTP responses and SSR markers; despite
its old header comment, it does not drive a browser. Use `WASP_DFX_HOST` to
change the host and `WASP_USE_RAW=0` for canonical local subdomains. Raw-endpoint
checks do not validate certified responses. Exit `2` denotes a fatal setup
failure, `1` failed assertions, and `0` no failed assertions among executed checks.

## Bzzz acceptance

Follow [Bzzz's setup](../bzzz/README.md#build-and-run-locally). There is no dedicated
automated Bzzz end-to-end suite in the current workflow. For an application change,
record the source commit, Wasm hash, local target, and results of relevant flows:

- Public home/chat/forum/feed rendering and navigation.
- Identity sign-in, signed writes, rejection of anonymous or unauthorized actions.
- Private server membership and DM access with two identities and a non-participant.
- Encrypted-message send/read plus legacy-message behavior, using a deliberately
  configured vetKD test environment.
- Data created before a local `upgrade` still readable afterward, including
  messages, membership, and permissions.

The default source uses a mainnet vetKD key and remote Internet Identity provider;
local page rendering alone does not validate these flows. Do not borrow a
production canister to compensate for incomplete local configuration.

## What was not revalidated during the documentation refresh

No Docker AOT build, canister deployment, browser flow, mainnet liveness check,
or Mono-runtime boot was performed. Historical `RESULTS.md` and phase reports
remain historical evidence. The commands and limits above were checked against
source; a new environment must still run its own build and integration checks.
