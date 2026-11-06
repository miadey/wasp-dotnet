# Bzzz (Bzz) — social application

Bzzz combines chat, servers/channels, forums, a social feed, and private messages
in one C# canister. Its pages are rendered from Razor on ICP; a JavaScript client
handles navigation, signed calls, and DM encryption. It is distinct from the
framework demo at `aot/samples/BlazorWasp` in the outer repository.

## Where to work

Paths in this guide are relative to `bzzz/` unless stated otherwise.

| Path | Responsibility |
| --- | --- |
| [aot/samples/BlazorWasp/Program.cs](aot/samples/BlazorWasp/Program.cs) | Startup, service wiring, HTTP endpoints, page shell, embedded browser JS |
| [Components/Pages/](aot/samples/BlazorWasp/Components/Pages/) | Home, chat, forum, feed, stable explorer Razor pages |
| `ChatService`, `ThreadService`, `ImageStore` | Messages, replies, images |
| `ServerService`, `ServerKindService`, `MembershipService`, `RoleService`, `ModService` | Spaces, channels, membership, permissions, moderation |
| `ForumService`, `VoteService` | Topics, accepted answers, votes |
| `FollowService`, `UserFollowService`, `RepostService`, `RepostTimeService` | Feed subscriptions and reposts |
| `DmService`, `VetkdService`, `VetkdCall`, `VetkdCandid` | Private-message storage and dedicated vetKD calls |
| `IdentityService`, `AdminService`, `KeyDirectoryService` | Display names, administration, public device-key directory |
| `PresenceService`, `RateService`, `StableExplorer`, `CanisterInterop` | Presence, rate gates, diagnostics, IC entrypoints |
| `aot/Wasp.*`, `shared/tools/` | Bzzz's copied framework and build tools |

Service source files are in [aot/samples/BlazorWasp/](aot/samples/BlazorWasp/).
The project/assembly remains named `BlazorWasp`; that name also appears in the
build output, Candid file, and export configuration. Keep it aligned with
`UnmanagedEntryPointsAssembly` when changing the project.

Bzzz was extracted into a separate local repository on 2026-06-04 and its sources
were included in `wasp-dotnet/bzzz/` on 2026-11-06. It is now a normal directory
in a fresh clone. Its copied framework does not automatically inherit changes
from the outer `aot/` tree. See [contribution workflow](../CONTRIBUTING.md).

## Request and data flow

1. The browser receives an HTML shell and JavaScript assets from the canister.
2. `WaspRouter` maps page paths to Razor components. `/_wasp/render` performs
   query-side rendering; `/_wasp/event` handles component events as updates.
3. Application endpoints in `Program.cs` read/write the services. Sensitive
   operations use signed agent calls and derive the caller from `msg_caller`.
   A display name or a browser-supplied principal is not authentication.
4. Services serialize persisted data into their assigned stable-memory regions.
   Heap-only state such as rate gates has a different lifetime.
5. vetKD requests use dedicated Candid update methods, because management-canister
   callbacks arrive in later messages and cannot simply be awaited inside the
   synchronous ASP.NET request pump.

Static asset certification and dynamic response certification are distinct.
Dynamic render/read routes registered as pass-through do not certify the body.
See [Architecture](../docs/ARCHITECTURE.md) and the source before changing these
trust boundaries.

## Build and run locally

Follow the outer [toolchain guide](../CONTRIBUTING.md#toolchain). These commands
start from the **outer `wasp-dotnet` repository root**:

```bash
docker build --platform linux/amd64 -t wasp-dotnet-build:latest \
  -f bzzz/aot/docker/Dockerfile.build bzzz/aot/docker
docker volume create wasp-nuget
cargo build --release --manifest-path bzzz/shared/tools/wasi-stub/Cargo.toml

# Build only: does not install or upgrade any canister.
WASP_DEPLOY= bash bzzz/aot/samples/BlazorWasp/build-and-deploy.sh
```

Output: `bzzz/aot/samples/BlazorWasp/BlazorWasp.canister.wasm`. The script prints
its SHA-256. Bzzz has no embedded Tetris/CRM WASM sub-apps, so its build has no
host-side `dotnet publish` step for those applications.

First install to a new local canister (from the outer root):

```bash
(cd bzzz/aot && dfx start --background)
WASP_DEPLOY=local WASP_DEPLOY_MODE=install \
  bash bzzz/aot/samples/BlazorWasp/build-and-deploy.sh
```

The script creates `blazorwasp` locally and prints its URL. For subsequent local
updates retaining state:

```bash
WASP_DEPLOY=local WASP_DEPLOY_MODE=upgrade \
  bash bzzz/aot/samples/BlazorWasp/build-and-deploy.sh
```

Open the printed URL and inspect `/`, `/chat`, `/forum`, and `/feed`. This is an
initial rendering check, not proof that authentication, private access, or
cryptographic flows work. Local authenticated testing needs Internet Identity
configuration suitable for the chosen origin. The source currently points to
`https://identity.ic0.app`; it does not provision a local identity canister.

[Seeded demo data](../docs/archive/README.md) is historical, not an onboarding
step. The old script targets mainnet and its `wasp` alias is absent from Bzzz's
`dfx.json`.

## DM encryption: implemented path and limits

The browser code in `Program.cs` uses vetKD to derive a per-conversation AES-GCM
key and stores encrypted bodies using an `E2E1` envelope. `VetkdService` binds
key derivation to the signed caller plus the named peer. The Candid interface
returns blobs for `vetkd_pubkey` and `vetkd_derive`; use
[blazorwasp.did](aot/samples/BlazorWasp/blazorwasp.did) rather than older comments
showing record replies.

- The server still accepts a text field and the browser supports legacy plaintext
  messages. Do not infer that every stored message is encrypted or that the
  server enforces ciphertext-only writes.
- Public chat/forum/feed content and conversation metadata are not made
  confidential by the DM feature.
- The controller can update the JavaScript served to browsers. Browser-side
  encryption does not remove this delivery trust boundary or prove an
  independently audited end-to-end security guarantee.
- The public key directory is present; that alone does not mean the older
  planned X3DH/Double Ratchet design has been implemented.
- `VetkdService.KeyName` is currently `key_1`, with 30 billion cycles attached per
  derivation in the source. Local vetKD configuration is different; the build
  script does not switch the key name automatically. Local encrypted-DM testing
  requires a deliberate test configuration, not a change to the production key.
- Changing the key name, derivation context (`wasp-bzzz/dm/v1`), or canonical
  conversation separator can make existing ciphertext unreadable.

The [Phase 0 threat model](../docs/E2EE_THREAT_MODEL.md) and
[Phase 1 design](../docs/E2EE_PHASE1.md) are preserved historical records. Their
statements that vetKD or the key directory do not exist are superseded by the
current Bzzz source; their broader trust-boundary discussion remains useful.

## Persistence and upgrades

The checked-in [canister IDs](aot/canister_ids.json) associate `razoronic` with
`4dcfc-hyaaa-aaaas-qdqbq-cai` on mainnet. This is existing deployment metadata,
not a fresh verification of the live service or a default test destination.

The build script interprets `WASP_DEPLOY=ic` as deployment to `razoronic`; the
mode defaults to `upgrade` but can be overridden by `WASP_DEPLOY_MODE`. Before
any approved production update, verify the network, controller identity,
compiled artifact, and persisted-data compatibility. **Never reinstall the
existing production canister or reset its stable memory as a test.**

Fixed offsets, header magic, versions, and serialization order are part of the
stored-data contract. Changing a `Version` constant is not a migration; some
loaders treat mismatches as empty state. Test upgrade compatibility on disposable
local data first and plan recovery before a production data-format change.

Offsets below are the service constants, not a guarantee that all headers start
at those exact bytes: several services keep headers immediately before them.
Consult each service's reader/writer before editing a layout.

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

## Validation

See [Testing](../docs/TESTING.md). The outer host suites exercise the root
framework, not automatically Bzzz's copied libraries. The existing CI does not
build or browser-test Bzzz. Record build hashes and separate rendering,
authentication, private-read, DM-crypto, and upgrade results when testing it.
