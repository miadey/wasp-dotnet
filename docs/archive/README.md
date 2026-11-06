# Archived operational helpers

## `seed_examples_forum.py`

[Source](seed_examples_forum.py), formerly `aot/seed_examples_forum.py`.
This was a one-off script to create a private Examples forum with demonstration
topics and reactions on the original `wasp` canister alias.

It invokes `dfx` with `--network ic`, uses the selected controller identity, and
calls `main()` unconditionally. Importing it executes its mutations. **Do not run
or import it as an onboarding or validation step.** Moving it here preserves
its content without presenting it as a normal setup tool.

The script depends on the working directory's `dfx.json`; Bzzz's current
configuration does not define the `wasp` alias. Any future reuse needs an explicit
local target, current endpoint review, and appropriate credentials. It has not
been executed during the documentation cleanup.
