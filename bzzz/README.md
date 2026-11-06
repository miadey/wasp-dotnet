# Bzzz

The **Bzzz** social super-app (Discord × X × forum) running natively on the
Internet Computer as a single C# canister, AOT-compiled to `wasm32-wasi`.

This repo was extracted from the [`wasp-dotnet`](https://github.com/miadey/wasp-dotnet)
monorepo so Bzzz can evolve independently of the framework's demo samples
(counter, place, chat, tetris, crm). The Wasp framework and build tooling are
**copied** into this repo (not submoduled) so it builds standalone.

## What's here

```
aot/
  Wasp.IcCdk/  Wasp.Http/  Wasp.WebSockets/      framework (copied verbatim)
  Wasp.AspNetCore/ (+ Vendor DLLs, .targets)     "
  Wasp.AspNetCore.Blazor.Wasp/                   "
  docker/Dockerfile.build                        AOT toolchain image def
  dfx.json  canister_ids.json
  samples/BlazorWasp/                            the Bzzz canister host project
shared/tools/                                    icp-publish, wasi-stub, weavers
```

Note: the host project / assembly is still named **`BlazorWasp`** internally.
That's intentional — the live mainnet canister `razoronic` was built from this
assembly, and keeping the assembly name (and every stable-memory offset/magic/
version) byte-format-identical is what makes `--mode upgrade` safe. The repo,
not the assembly, carries the new "Bzzz" identity. Renaming the assembly later
is possible but must update `UnmanagedEntryPointsAssembly` or the hand-written
`vetkd_pubkey`/`vetkd_derive` canister exports vanish.

## Services

- **Chat substrate** — `ChatService` (rooms = channels), `ImageStore`, `MessageHtml`
- **Servers/guilds** — `ServerService`, `ServerKindService`, `RoleService`, `ModService`, `MembershipService`
- **Forum** — `ForumService`, `ThreadService`, `VoteService`
- **Feed (X-style)** — `FollowService`, `UserFollowService`, `RepostService`, `RepostTimeService`
- **DMs + E2EE** — `DmService`, `KeyDirectoryService`, `VetkdService` (+ `VetkdCall`, `VetkdCandid`)
- **Shared infra** — `AdminService`, `IdentityService`, `PresenceService`, `RateService`, `StableExplorer`, `CanisterInterop`

## Build

Prerequisites (host): Docker (with the `wasp-dotnet-build:latest` image),
`dfx`, `wasm-opt` (binaryen), `dotnet` SDK 10, and the `wasi-stub` binary
(built once from `shared/tools/wasi-stub` via `cargo build --release` — the
`target/` dir is gitignored).

```bash
# build only (produces the wasm + prints its sha256; touches nothing else)
./aot/samples/BlazorWasp/build-and-deploy.sh
```

## Deploy

> `razoronic` (`4dcfc-hyaaa-aaaas-qdqbq-cai`) is **live and holds real user
> data** in stable memory. Deploys are **`--mode upgrade` only — never
> reinstall**, and never bump a service's `Version` constant, or that region's
> stable data is wiped.

```bash
WASP_DEPLOY=local ./aot/samples/BlazorWasp/build-and-deploy.sh   # local replica
WASP_DEPLOY=ic    ./aot/samples/BlazorWasp/build-and-deploy.sh   # UPGRADE razoronic
```

## Stable-memory layout (frozen)

Raw fixed-offset `stable64` regions, magic+version header per region. Do not
move offsets or bump versions against `razoronic`.

| Offset | Region | Magic |
|---|---|---|
| 900_000 | AdminService | ADMN |
| 1_000_000 | ImageStore | IMGS |
| 4096 | ChatService | CHAT (v3) |
| 5_500_000 | IdentityService | IDNT |
| 6_000_000 | ServerService | SRVR |
| 6_500_000 | DmService | DMSG |
| 16_000_000 | ThreadService | THRD/THR2 |
| 32_000_000 | RoleService | ROLE |
| 48_000_000 | ModService | MODS/MUTE |
| 64_000_000 | MembershipService | MEMB |
| 80_000_000 | ServerKindService | SKND |
| 81_000_000 | ForumService | FRUM |
| 90_000_000 | FollowService | FLLW |
| 95_000_000 | RepostService | RPST |
| 100_000_000 | UserFollowService | UFOL |
| 110_000_000 | RepostTimeService | RPTS |
| 120_000_000 | VoteService | VOTE |
| 140_000_000 | KeyDirectoryService | KEYS |
