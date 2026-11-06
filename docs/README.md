# Documentation index

## Current entry points

| Document | Use |
| --- | --- |
| [Project README](../README.md) | Repository map, first build, sample selection |
| [Contributing](../CONTRIBUTING.md) | Collaborator setup, tools, workflow, troubleshooting |
| [Architecture](ARCHITECTURE.md) | Execution model, source map, build pipeline |
| [Bzzz](../bzzz/README.md) | Social application, local setup, persistence, encryption limits |
| [Testing](TESTING.md) | Executable checks, dated results, CI coverage and gaps |
| [Maintenance](MAINTENANCE.md) | Cleanup choices and files intentionally retained |
| [Runtime guide](../runtime/README.md) | Experimental track and recorded blockers |

## Component references

- [ASP.NET unsupported/substituted APIs](../aot/Wasp.AspNetCore/UNSUPPORTED.md).
- [Wasp stable-memory persistence API](../aot/Wasp.OrthogonalPersistence/README.md).
- [Decoder cross-validation](../aot/Wasp.AspNetCore.Blazor.Server.JsDecode/README.md).
- [HTTP acceptance harness](../aot/tests/README.md).
- [vetKD C# proof of concept](../aot/samples/VetkdCsharpPoc/README.md).
- [Canister size-budget policy](../.github/workflows/SIZE_BUDGET.md).

## Historical design and investigation records

These documents explain decisions and experiments, not today's release status.
Their old paths, issue states, compatibility claims, deployment commands, and
measurements may have been superseded. Prefer current guides and source for work.

| Record | Context |
| --- | --- |
| [E2EE Phase 0 threat model](E2EE_THREAT_MODEL.md) | Baseline before current browser vetKD DM implementation |
| [E2EE Phase 1 design](E2EE_PHASE1.md) | Earlier plan; statements that key directory/vetKD files are absent are superseded |
| [M4 Blazor Server plan](m4-blazor-server-on-canister.md) | Circuit-based integration design, distinct from Bzzz's render-as-query path |
| [M4 progress](m4-progress.md) | Historical milestone results and outstanding work at that point |
| [M4 circuit coupling](m4-s2-circuit-coupling.md) | Implementation/design record |
| [Single-update click note](../aot/docs/m4-single-update-click.md) | Earlier interaction pipeline investigation |
| [Sample test record](../aot/samples/TESTING.md) | Historical manual checks; not a current aggregate pass |
| [Runtime phase reports](../runtime/README.md#historical-records) | Mono hosting diagnostics and superseded hypotheses |

## Proposals and archives

- [Umbraco-style CMS proposal](plans/umbraco-cms.md): future project design,
  not an implemented CMS in this repository; formerly `umbracoClone.md`.
- [Archived forum seeding script](archive/README.md): preserved one-off mainnet
  operation, not part of onboarding; formerly `aot/seed_examples_forum.py`.

`RESULTS.md` files next to individual experiments retain their original findings.
Preserve those findings when adding new validation; date and distinguish new runs.
