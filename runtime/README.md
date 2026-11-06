# Experimental Mono runtime hosting

This directory explores hosting a .NET/Mono WebAssembly runtime inside an ICP
canister. It is separate from the NativeAOT stack used by Bzzz and the root AOT
examples. New contributors should start with the [root guide](../README.md).

## State of the work

The checked-in [Phase C status](PHASE_C_STATUS.md) reports unresolved Mono boot
and core-library registration problems, including failures around
`mono_assembly_load_corlib` and the diagnostic merge/patch pipeline. Earlier
records contain different hypotheses; none should be treated as proof of a
currently functioning general-purpose .NET runtime.

The old top-level roadmap advertised eventual reflection, a full BCL, and broader
framework compatibility. Those are research goals, not capabilities established
by this documentation refresh. No runtime boot was rerun for that refresh.

## Source map

| Path | Purpose |
| --- | --- |
| [wasp_canister/](wasp_canister/) | Rust host/shim and IC entrypoints |
| [wasp_dotnet_app/WaspHost/](wasp_dotnet_app/WaspHost/) | Managed host application |
| [inputs/](inputs/README.md) | Runtime binaries, BCL assemblies, build inputs |
| [scripts/](scripts/) | Build, merge, upload/boot, instrumentation and patch helpers |
| [dfx.json](dfx.json) | Separate local canister configuration |

`20_build_canister.sh`, `30_merge.sh`, and `40_upload_and_boot.sh` describe the
experimental sequence. Read them before running: they operate on artifacts and
canister state, and historical commands may reinstall a local target. They are
not part of the Bzzz build or a generic onboarding command set.

## Historical records

- [Phase A retrospective](PHASE_A_RETRO.md)
- [Phase B retrospective](PHASE_B_RETRO.md)
- [Phase B handoff](PHASE_B_RESUME.md)
- [Phase C status and diagnostics](PHASE_C_STATUS.md)

Keep binary inputs and diagnostic scripts until a reproducible replacement is
validated. Some are deliberately checked in to preserve the investigation; an
unusual filename or old date is not sufficient reason to delete them.
