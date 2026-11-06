# WASP Bzzz — E2EE Phase 1: Crypto Foundations (design + the constraint that shapes everything)

> **Historical design record.** Statements below that the key directory, vetKD
> exports, or RNG guard do not exist are superseded by the current Bzzz source.
> The planned protocol is not a claim of full implementation. Start with the
> [current Bzzz guide](../bzzz/README.md).

Status: design record. Honest baseline, no overstatement (companion to
`docs/E2EE_THREAT_MODEL.md`). Captures the Phase 1 design and the architectural
constraint found while scoping it, so Phase 2 executes without re-deriving it.
If a sentence here reads like marketing, it is a bug.

Scope: per the threat model, the IC gives integrity, not confidentiality —
replica operators and the controller can read all stable memory. E2EE moves
confidentiality of 1:1 DM bodies into the browser so plaintext never reaches the
canister. Phase 1 lays the crypto plumbing; Phase 2 wires it.

## 0. Implementation status (what is actually in the tree, today)

Be precise about this — the rest of the doc is design, not shipped code.

- SHIPPED, in tree now: the two rate-limit buckets `key` and `keyfetch` in
  `aot/samples/BlazorWasp/RateService.cs` (a 2-line `Policy()` addition;
  `key` = cap 20 / 1-per-sec, `keyfetch` = cap 10 / 0.5-per-sec). RateService is
  HEAP-ONLY (resets on upgrade) by design; it has no stable region.
- DESIGNED, NOT yet coded (this doc is the spec): `KeyDirectoryService` + its
  stable `KEYS` region; the `raw_rand` / vetKD management-call exports; the
  weaver RNG-guard + `check-no-rng.sh` CI guard; the ciphertext envelope; the
  browser key-store. None of these files exist yet. The DM endpoints
  (`/api/dm/*`) and `DmService` carry plaintext today.

So: the "key" rate buckets are real foundation; everything else below is the
plan that Phase 2 implements. Do not describe the directory or vetKD as shipped.

## 1. THE CRITICAL CONSTRAINT — async management calls cannot live in MapPost

This is the single fact that reshapes the whole plan.

The canister serves every browser request synchronously inside ONE
`http_request` / `http_request_update` message. ASP.NET middleware runs under
`Wasp.AspNetCore`'s `IcSyncContext.RunUntilComplete`
(`aot/Wasp.AspNetCore/src/IcSyncContext.cs`): a single-threaded
`SynchronizationContext` that pumps async continuations to completion **within
that one message**. If the continuation queue drains before the awaited task
finishes, something blocked on real I/O that resolves in a *later* message —
forbidden. `IcSyncContext` throws
`"non-deterministic await detected — middleware blocked on real I/O"`.

Inter-canister calls are exactly that forbidden shape. `raw_rand` and
`vetkd_derive_key` are calls to the management canister (`aaaaa-aa`): `call_new`
+ `call_data_append` + `call_perform` return immediately; the IC delivers the
reply/reject in a **fresh callback message**. Awaiting that inside a MapPost
handler would trap.

Consequence: management-canister async calls **cannot** be folded into the
existing ASP.NET `MapPost` handlers. They require a dedicated manual export that
issues the call and replies *from the reply callback*, exactly like the
`HelloFetch` sample (`aot/samples/HelloFetch/Program.cs`):

```csharp
[CanisterUpdate(Manual = true)]   // Manual: skip auto Candid decode + auto reply
public static void fetch() {
    WaspOutcall.Get(url,
        onReply: resp => Reply.Bytes(...),   // reply happens HERE, in a later msg
        onReject: rej  => Reply.Bytes(...));
}
```

`WaspOutcall` (`aot/Wasp.Outcalls/src/WaspOutcall.cs`) is the working template:
it stashes the continuation under a token, passes `[UnmanagedCallersOnly]`
reply/reject thunks as the call's `reply_fun`/`reject_fun`, and resolves the
continuation when the IC re-enters in the callback message. `call_perform != 0`
traps; the continuation calls `Reply.Bytes`/`Reply.Trap` exactly once.

Build wiring this needs (and that `BlazorWasp.csproj` does NOT have yet):

- Add the `Wasp.IcCdk.SourceGenerator` analyzer reference (it generates the
  C-ABI export for `[CanisterUpdate]` methods). Pattern from
  `aot/samples/HelloFetch/HelloFetch.csproj`:
  `<ProjectReference Include="..\..\Wasp.IcCdk.SourceGenerator\Wasp.IcCdk.SourceGenerator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />`
- `BlazorWasp.csproj` already has `UnmanagedEntryPointsAssembly Include="BlazorWasp"`
  and `...Wasp.AspNetCore` — good. It also needs the analyzer (above) and, if
  the helper lands in `Wasp.Outcalls`, a `ProjectReference` +
  `UnmanagedEntryPointsAssembly` for that assembly.

This is why `raw_rand`/vetKD are **DEFERRED** to a focused follow-up: they pull
in a new export surface + analyzer wiring that touches the build and the exported
method set. The synchronous stable-memory work (the key directory) needs none of
that and ships independently.

## 2. WHAT THIS SLICE COVERS (foundation, not UI-wired)

- `RateService` `key`/`keyfetch` buckets — SHIPPED (see section 0). The only
  in-tree change.
- `KeyDirectoryService` — DESIGNED below (section 6). New synchronous stable
  region `KEYS` at offset 140M, RegionEnd 160M; additive (does not touch any
  existing region); stores **public keys only**; signed publish / replenish /
  fetch with atomic consume-once of one-time prekeys (OTPK), plus an anonymous
  devices lookup. No `await`, no management call — pure stable memory, so it
  drops straight into a MapPost handler.
- Weaver RNG-guard + `check-no-rng.sh` — DESIGNED. A weaver/CI invariant that
  logs and fails the build if managed RNG (`System.Random`,
  `RandomNumberGenerator`, etc.) appears in the canister, since on-replica RNG
  is non-deterministic across replicas and only `raw_rand` is legitimate.

All of these are **backend foundation, NOT wired to any UI.** Phase 2 wires them.

## 3. RAW_RAND DESIGN (next, deferred per section 1)

Helper modeled on `WaspOutcall` but targeting the management canister method
`raw_rand`:

- `call_new` to `aaaaa-aa` (empty principal, `callee_size = 0`), method name
  `"raw_rand"`.
- Candid args: empty — `raw_rand : () -> (blob)`. The argument blob is the bare
  DIDL magic with zero types/values: `DIDL\0\0` (4 magic + 1 type-count 0 +
  1 arg-count 0).
- Reply: decode `(blob)` = `vec nat8` via `Candid.Reader.ReadBlob`
  (`aot/Wasp.IcCdk/src/Candid.cs` has `ReadBlob()` already). `raw_rand` returns
  32 bytes.
- Delivery: a `[UnmanagedCallersOnly]` reply thunk runs in the later callback
  message and resolves the continuation, surfaced through a dedicated
  `[CanisterUpdate(Manual = true)] rand32` export.

Usage rule: use `raw_rand` **only** for values that must be *agreed across all
replicas*. Per-client nonces and key material are generated **in the browser**
with `crypto.getRandomValues` — never on the canister. Keeps the canister
deterministic and keeps secret-bearing RNG off the replicated machine entirely.

## 4. VETKD DESIGN (next, deferred per section 1)

Management-canister methods:

- `vetkd_public_key` — free; returns the (derived) public key for a key id +
  context, used to verify the encrypted key the canister returns.
- `vetkd_derive_key` — costs cycles (~26G for `key_1`, ~10G for `test_key_1`).

Candid shapes (both wrapped in records):

- `key_id : record { curve : variant { bls12_381_g2 }; name : text }`
- `context : blob`, `input : blob`, `transport_public_key : blob`
- reply: `record { encrypted_key : blob }` (derive) / `{ public_key : blob }`
  (public_key).

Canister-side policy:

- `input` = the **caller's principal bytes** (`msg_caller`). Per-principal,
  deterministic, and recoverable by that principal — the canister binds the
  derived key to *who is asking*, so a caller can only ever derive *their own*
  key.
- `context` = a fixed domain separator: `"wasp-bzzz/e2ee/v1"`. Versioned so a
  future scheme bump doesn't collide with v1-derived keys.

Both are wired as `[CanisterUpdate(Manual = true)]` exports (same callback shape
as section 1 — they are management calls).

Browser side: `@dfinity/vetkeys`
(`TransportSecretKey` / `EncryptedVetKey` / `DerivedPublicKey`,
`decryptAndVerify`, then `asDerivedKeyMaterial` → AES-GCM, or IBE). The
transport secret key never leaves the browser; the canister only sees the
*transport public key* and returns an *encrypted* key blob.

Maturity caveat (be honest with users): vetKD has been **mainnet-test** since
proposal 136589 (May 2025). Use `test_key_1` until `key_1` reaches GA. Test keys
**may be wiped** — store nothing under a test key you cannot re-derive or afford
to lose. This is why vetKD is a deliberate follow-up, not a launch dependency:
the X25519/X3DH envelope (section 5) does not need it.

## 5. CIPHERTEXT ENVELOPE (for Phase 2 1:1 DM E2EE)

Goal: encrypt DM bodies in the browser with **no DMSG schema change**. The
existing `DmService` (`aot/samples/BlazorWasp/DmService.cs`) stores each message
as `WriteString(text)` in the `DMSG` region (magic `0x444D5347`, v1, offset
6.5M). We ride that same `text` field — no magic bump, no version bump.

Transport: an ASCII sentinel prefix on the stored text.

```
E2E1<base64url(envelopeBytes)>      ← encrypted message
<anything else>                      ← legacy plaintext, rendered as today
```

A message with the `E2E1` prefix is an envelope; anything without it is legacy
plaintext and renders exactly as it does now. Fully backward compatible: old
clients and old stored messages are untouched.

`envelopeBytes` (little-endian; built and parsed **only in the browser** — the
canister never interprets it):

| field            | type           | notes                                        |
|------------------|----------------|----------------------------------------------|
| envVersion       | u8 = 1         | envelope format version                      |
| cipherId         | u8 = 1         | X25519-X3DH + AES-256-GCM (crypto-agility id)|
| headerType       | u8             | 1 = X3DH-initial, 2 = ratchet                |
| senderDeviceId   | lp             | length-prefixed                              |
| recipientDeviceId| lp             |                                              |
| [type 1 only]    | lp ephemeralPub(32) + u32 usedOtpkId + lp ikPubSender(32) | X3DH initial msg |
| dhRatchetPub     | lp (32)        | Double-Ratchet sending key                   |
| pn               | u32            | previous-chain message count                 |
| n                | u32            | this-chain message count                     |
| nonce            | lp (12)        | AES-GCM nonce                                |
| ciphertext       | bytes          | plaintext + 16-byte GCM tag                  |

GCM AAD = **all bytes before `nonce`** (the whole header) — so a tampered header
fails authentication.

Crypto agility: `cipherId` and `envVersion` are the rotation points. A future
PQ-hybrid suite is `cipherId = 2` with **no DMSG change** — same `E2E1` rail,
different envelope interpretation in the browser.

OPEN ISSUE (Phase 2 must resolve): `DmService.MaxTextLen = 1000`
(`aot/samples/BlazorWasp/DmService.cs:53`) is enforced server-side by
`SanitiseText` (truncates to 1000 chars). Base64url of an envelope inflates by
~1.37x, so a long encrypted message will be **silently truncated and corrupted**.
Phase 2 must either raise `MaxTextLen` for E2E-prefixed payloads or chunk the
ciphertext across multiple DMSG messages. Truncation here is a correctness bug,
not a cosmetic one — flag it loudly.

## 6. KEY DIRECTORY CONTRACT (synchronous, ships in this slice's design)

`KeyDirectoryService` mirrors the established stable-region idiom in this
codebase (header-below-data magic, additive region, RegionEnd write-guard — see
`FollowService` / `DmService`). It is pure stable memory: no `await`, no
management call, so it lives directly in MapPost handlers.

Region layout (`KEYS`):

- Offset `140_000_000`, RegionEnd `160_000_000`. Highest currently-used region
  is `VoteService` ending at 130M, so [130M, 160M) is free; KEYS at 140M leaves a
  10M gap and 20M of room. ADDITIVE: touches no existing region.
- `MagicOffset = Offset - 8`: `uint32 magic "KEYS"`, `uint32 version 1`, then the
  directory body at Offset. RegionEnd guard refuses writes past 160M (same
  pattern as `DmService.Persist`).
- **Public keys only.** No private key or plaintext ever enters this region (or
  any canister memory). Replica operators already read all stable memory, so
  public-only storage makes that exposure a non-event.

Per-device record (sketch — finalize in Phase 2): identity public key (ikPub),
signed prekey + signature, a queue of one-time prekeys (OTPKs) each with an id,
and a device id, all bound to the owning principal text.

Endpoints (all UPDATE-only `MapPost`, signed-caller posture like the DM
endpoints; rate-limited via `RateService` `key` / `keyfetch` buckets):

- `POST /api/keys/publish` — caller publishes/updates their device bundle
  (ikPub, signed prekey + sig, initial OTPK batch). Authenticated caller bytes
  only (anonymous rejected, same `RequireAuthed` posture as `DmService`).
  Bucket `key`.
- `POST /api/keys/replenish` — caller tops up their OTPK queue. Bucket `key`.
- `POST /api/keys/fetch` — given a peer principal (+ device), returns their
  ikPub, signed prekey + sig, and **atomically consumes one OTPK** (the returned
  OTPK is removed in the same message, so two callers can never get the same
  one). Bucket `keyfetch` (stricter — resists OTPK-depletion abuse).
- `POST /api/keys/devices` — lookup a principal's published device ids; may be
  anonymous (public device ids only). Bucket `keyfetch`.

Client key-store plan (browser):

- Private keys live in **IndexedDB** in the browser only; never sent to the
  canister.
- `@noble/curves` x25519 for DH, `@noble/hashes` HKDF for the X3DH / ratchet KDF
  chain, `crypto.subtle` AES-GCM for the symmetric layer.
- All randomness from `crypto.getRandomValues` (per section 3 — never canister
  RNG).
- Safety numbers (out-of-band identity verification) = SHA-256 over both
  parties' identity public keys, rendered as a comparable fingerprint.

## 7. Phase 2 next steps (checklist)

- [ ] Add the `Wasp.IcCdk.SourceGenerator` analyzer reference to
      `BlazorWasp.csproj` (OutputItemType=Analyzer) and any
      `UnmanagedEntryPointsAssembly` needed for the new export surface.
- [ ] Implement `KeyDirectoryService` (KEYS region 140M/160M, additive, public
      keys only) + the four `/api/keys/*` MapPost endpoints, signed-caller +
      `key`/`keyfetch` rate-limited, atomic OTPK consume-once.
- [ ] Land the `rand32` `[CanisterUpdate(Manual=true)]` export (raw_rand helper)
      and the weaver RNG-guard + `check-no-rng.sh` CI guard.
- [ ] Land the vetKD exports (`vetkd_public_key` / `vetkd_derive_key`) on
      `test_key_1`, context `"wasp-bzzz/e2ee/v1"`, input = msg_caller bytes.
- [ ] Browser key-store: IndexedDB private keys, `@noble/curves`/`@noble/hashes`
      X3DH+ratchet, `crypto.subtle` AES-GCM, safety numbers.
- [ ] Envelope encode/decode + `E2E1` sentinel on the DM send/render path
      (backward compatible with legacy plaintext).
- [ ] RESOLVE the `MaxTextLen=1000` truncation issue (raise cap for E2E payloads
      or chunk) BEFORE shipping encrypted DMs — it corrupts long messages.
- [ ] Wire the directory + envelope into the DM UI (this is where E2EE becomes
      user-visible; nothing above is wired to UI yet).
- [ ] Update `docs/E2EE_THREAT_MODEL.md` to reflect what E2EE actually changes
      once shipped — claims must not exceed the code.
