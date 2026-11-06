# Repository maintenance — 2026-11-06

This refresh makes the repository usable for collaborator onboarding. It does
not change application behavior, compile a new canister, or deploy anything.

## Changes

- Replaced outdated root README and contribution instructions with a source-based
  map of the SDK, examples, Bzzz, and Mono runtime experiment.
- Expanded the Bzzz guide and added architecture, testing, runtime, and document
  indexes. The two `BlazorWasp` trees are now explicitly distinguished.
- Marked phase reports and old design records as historical instead of deleting
  experimental evidence or silently rewriting earlier claims.
- Moved `umbracoClone.md` to `docs/plans/umbraco-cms.md`, labeled as a proposal.
- Moved `aot/seed_examples_forum.py` to `docs/archive/seed_examples_forum.py`
  without changing its contents. Its mainnet side effects are explained there.
- Removed `.claude/scheduled_tasks.lock` from version control and ignored it.
  The local process-state file is retained on disk; it is not project config.
- Started tracking the decoder test's `package-lock.json`, updating its
  transitive `ws` dependency to 7.5.13 after the old local lock triggered an
  npm advisory. A clean dependency install and the 11 decoder checks passed;
  the application packages were not changed. Generated `vectors.json` remains
  ignored and reproducible through the emitter.

## Intentionally retained

| Material | Reason |
| --- | --- |
| Bzzz's framework copies | Its standalone build depends on them; deduplication requires separate engineering |
| `Vendor/*.dll` | Rewritten framework inputs required by the AOT adapters |
| `runtime/inputs/` and patch helpers | Inputs and diagnostics for the unfinished runtime experiment |
| `Issue80Repro`, `_AspNetSmoke`, phase reports, sample results | Reproductions and historical engineering evidence |
| Mainnet canister IDs | Existing deployment metadata; do not confuse with local IDs or credentials |

Generated binaries, dependencies, caches, replica data, local keys, and backup
files remain outside Git according to the existing ignore rules. A source clone
is not a backup of a deployed canister's state or the developer's credentials.

## Follow-up work recorded, not silently fixed

- Decide the Wasp router's `BatchId` normalization contract and resolve the
  existing failing test. See [baseline and reproduction](TESTING.md).
- Make both RNG guard scripts resolve their own source roots; the Bzzz copy
  currently points at the outer developer checkout.
- Provide reproducible local Internet Identity/vetKD configuration for Bzzz and
  add application/browser/upgrade acceptance coverage.
- Extend CI beyond the original five AOT samples and BlazorChat.
- Review shared-framework synchronization and the inherited Bzzz vendor rebuild
  script before a compiler or .NET upgrade.

These are discovered limitations, not completed fixes or release commitments.
