# WASP Bzzz — E2EE Threat Model (Phase 0)

Status: Phase-0 deliverable. Honest baseline. No overstatement.

## Purpose

WASP Bzzz is a Discord × X × forum super-app built as a single .NET NativeAOT→wasm
Internet Computer canister (`razoronic`) plus an injected vanilla-JS browser client.
This document states, plainly, what is and is not protected **today**, who the
adversaries are, where the trust boundaries lie, and what the Phase-0 hardening pass
does and does not change. It exists so that no claim we make to users exceeds what
the code actually delivers. If a sentence here reads like marketing, it is a bug.

The single most important fact: **the Internet Computer gives integrity, not
confidentiality.** Everything else follows from that.

## Assets (what we protect)

- **Message bodies** — chat messages, direct messages (DMs), thread/reply text,
  forum post bodies.
- **Social graph** — who follows whom, reposts, votes, channel membership.
- **Identity directory** — the principal → display-name binding (`IdentityService`).
- **Account control** — a user's ability to act only as themselves (authenticity of
  writes), and the integrity of who-said-what.
- **Availability** — the canister keeps serving and is not drained of cycles or
  flooded into uselessness.

Of these, **only authenticity/integrity and (partially) availability are protected
today.** Confidentiality of bodies and of the graph/directory is **not**.

## Adversaries

### In scope

1. **Node / replica operators (passive).** Every subnet replica holds a full,
   replicated copy of the canister's stable memory. An operator can read it. This is
   inherent to ICP, not a flaw in our code.
2. **The canister controller — including a coerced or legally-compelled one.** The
   controller can dump full canister state at will and, critically, can **change the
   code that the canister serves to browsers** (see "The hard ceiling"). This is the
   strongest adversary and the one we cannot fully defend against today.
3. **Network / MITM attacker.** Mitigated by ICP's certified responses + TLS on the
   boundary nodes; signed update calls are certificate-verified by `@dfinity/agent`.
4. **XSS / malicious-script-in-page.** Our client is hand-written JS injected as C#
   string literals; an injection that runs in the page can read anything the page can,
   including any future session-held key material.
5. **A stolen Internet Identity delegation.** The II Ed25519 delegation has a **7-day
   TTL**. Anyone holding it within that window can act as the user.
6. **Spam / DoS / cycle-drain attacker.** Unauthenticated or cheap calls that bloat
   stable memory or burn cycles.

### Out of scope (today)

- A malicious **Internet Identity** itself, or compromise of the II canister / its
  subnet. We treat II as the identity anchor (root of authenticity).
- Compromise of the ICP protocol, NNS, or subnet consensus / certification.
- Endpoint compromise of the **user's own device** (a keylogger, a malicious
  extension, a rooted phone). Client-side E2EE cannot survive a compromised client.
- Traffic-analysis adversaries beyond the metadata we already admit is in cleartext
  (we do not claim metadata privacy — see below).
- Off-chain components introduced later (e.g. a Web2 calls/SFU service); they get
  their own threat model when built.

## Trust boundaries

- **Browser = trusted compute.** All cryptography must run here. The canister has no
  usable randomness (see below), so the browser (WebCrypto) is the only place we can
  generate keys or do AEAD/ECDH. We trust the browser to execute the code it was
  given — but note that the *code it was given* came from the canister.
- **Canister stable memory = honest-but-curious, integrity-certified store, but
  UNTRUSTED for confidentiality.** We rely on it to faithfully store and return data
  (integrity, guaranteed by ICP certification) and to enforce server-side caller
  checks. We must assume its contents are readable by node operators and the
  controller. **Do not put a plaintext secret in stable memory.**
- **Internet Identity = identity anchor.** It mints the Ed25519 session delegation;
  the canister reads the authenticated caller via `ic0.msg_caller` on signed
  `@dfinity/agent` update calls. We trust II for *authenticity*, not for any secrecy.

## What is protected vs. explicitly leaked — TODAY

### Protected today

- **Write authenticity.** Writes (post, DM-send, follow, vote) arrive as **signed
  `@dfinity/agent` update calls**. The canister reads the real caller from
  `ic0.msg_caller` (`MessageContext` → `Ic0.msg_caller_copy`). Anonymous principal
  `2vxsx-fae` (empty / `0x04` raw blob) is **rejected** before any storage access on
  writes and on private reads (`DmService.RequireAuthed`). You cannot post or read a
  DM as someone else without their delegation.
- **Private-read access control.** DM / private-channel reads are gated server-side:
  `DmService.ReadThread` derives a canonical `{A,B}` key and returns empty unless the
  authenticated caller is a participant. **This is an access check, not encryption.**
  It stops other *users*. It does **not** stop a node operator or the controller, who
  read stable memory directly and bypass the check entirely.
- **Transport / response integrity.** ICP certified responses + agent verification.

### Explicitly leaked today (cleartext in stable memory)

- **All bodies — NONE are encrypted yet.** Chat, DMs, thread/reply text, forum posts
  are stored as plaintext.
- **The social graph.** Follows, reposts, votes, and channel/DM membership are
  plaintext relationships.
- **Metadata.** Who-DMs-whom, who-follows-whom, who-voted-what, display names, and
  message timing are all in the clear. We make **no** metadata-privacy claim.
- **The principal → name directory** (`IdentityService`) is plaintext and
  query-readable.

Anyone able to read stable memory (node operator, controller, anyone the controller
shows it to, anyone who compels the controller) sees all of the above today.

## The hard ceiling: controller-as-code-delivery-root

This is the limit that bounds even our *future* E2EE roadmap, and we state it bluntly.

**The canister serves all of its own client JavaScript.** The client is built from C#
string literals in `Program.cs` plus the self-hosted `@dfinity` bundle at
`/_wasp/dfinity.js`. There is no separate, independently-served frontend.

Therefore a **malicious or legally-coerced controller can ship key-stealing
JavaScript** to a target's browser on their next load — code that exfiltrates a private
key the instant WebCrypto generates it, or that quietly disables encryption. No amount
of in-browser crypto defeats an attacker who controls the delivery of the crypto code
itself.

Consequence for honesty: **even fully-implemented client-side E2EE would be "E2EE
against passive node-operator state dumps, NOT against a malicious controller"** until
the delivery root is constrained. The *only* real mitigations for the ceiling are:

- **Controller hardening:** blackhole the canister or move control to SNS / NNS
  governance, so no single party can swap the served code.
- **Reproducible builds + public verification:** so the served code can be checked
  against source by third parties.
- **CSP / SRI:** pin and integrity-check served assets so silent substitution is
  detectable (a *defense in depth*, not a cure — the controller controls the policy
  too, but tamper becomes loud rather than silent, and pinned third-party deps can't
  be swapped without a visible code change).

Until those exist, we do not claim protection against the controller. Full stop.

## Why all crypto must run in the browser (no usable canister randomness)

The canister cannot do crypto on its own:

- `ic0.raw_rand` is **async, update-only** — unusable in synchronous query/render
  paths and awkward everywhere else.
- `RandomNumberGenerator` is **zeroed by `Wasp.CircuitHostWeaver`**: its `Fill` /
  `GetBytes` call sites are rewritten to produce all-zero buffers (this was required
  to make Blazor's circuit host link under wasm; see CircuitHostWeaver `Program.cs`).
  A "random" key derived on the canister would be **all zeros**.
- `System.Security.Cryptography` AES / ECDH throw `PlatformNotSupported` on
  wasm32-wasi.

So: **all key generation, ECDH, and AEAD must happen in the browser via WebCrypto.**
The canister is a ciphertext mailbox, never a key holder.

## Transport reality

Delivery is **poll-only** — there is no native push to a canister. The client polls
(`/_wasp/render` and event endpoints). This shapes latency and the future delivery
design (P3), and means "real-time" is best-effort polling, not server push.

## vetKeys / vetKD

`vetKD` is the ICP-native key-derivation primitive and is the natural long-term
foundation for server-assisted key derivation. **It is mainnet-test-grade only**
(proposal 136589, available since May 2025). We may prototype against it but must
**not** depend on it for any production confidentiality claim yet.

## What Phase 0 hardening changes — and what it does NOT

### Phase 0 DOES

- **CSP (report-only) + SRI + security headers.** Report-only first to find
  violations without breaking the app; SRI to pin the self-hosted `@dfinity` bundle so
  third-party code can't be silently swapped; standard hardening headers
  (`X-Content-Type-Options`, `Referrer-Policy`, frame-ancestors, etc.).
- **Per-principal rate limiting** on writes, to blunt spam and cheap abuse.
- **`canister_inspect_message` size gate** — reject oversized / malformed ingress
  before it costs cycles or bloats stable memory.
- **Cycle monitoring** — alert before the canister can be drained into freezing.
- **Remove the unauthenticated `/api/identity/bind`.** It is the legacy
  "anyone-can-claim-any-name" cosmetic bind. The signed `/api/account/bind` (binds
  `ic0.msg_caller`, not a client-supplied principal) replaces it. Until removal,
  directory bindings are spoofable.
- **Stable-memory tail-zeroing on delete** — when a record is deleted, zero the freed
  bytes so deleted plaintext does not linger in a state dump.

### Phase 0 does NOT

- **It adds NO encryption.** After Phase 0, bodies, the graph, and the directory are
  still plaintext in stable memory. Phase 0 hardens authenticity, abuse-resistance,
  and tamper-visibility — it does **not** add confidentiality. Encryption is Phase 1+.

## The honest claim — what we MAY and MAY NOT say today

**We MAY say:**

- "Writes are cryptographically authenticated via Internet Identity; you cannot post
  or DM as another user without their session delegation."
- "Anonymous callers cannot post or read private messages."
- "Responses are integrity-certified by the Internet Computer."
- "Private DMs and channels are access-controlled so other *users* cannot read them."

**We MAY NOT say:**

- "Your messages are private / encrypted / end-to-end encrypted." (Nothing is
  encrypted today.)
- "Node operators / the host / we cannot read your messages." (They can.)
- "Your DMs are confidential." (They are access-controlled against users, plaintext to
  operators and the controller.)
- "Metadata is private." (Who-talks-to-whom, the graph, names, and timing are
  cleartext.)

Note also: a stolen II delegation grants full impersonation for up to its **7-day
TTL**; we should communicate session lifetime honestly and consider shorter TTLs /
re-auth for sensitive actions.

## Roadmap pointer — phased E2EE

Confidentiality is delivered in phases. Each phase carries the ceiling caveat above
until controller/delivery hardening lands.

- **P1 — Key foundations.** Browser-generated (WebCrypto) per-user identity keys;
  public-key publication via the canister; key storage / unlock UX. No body encryption
  yet.
- **P2 — 1:1 DM E2EE.** ECDH (WebCrypto) → AEAD on DM bodies; canister stores only
  ciphertext for DMs. First real confidentiality win — explicitly framed as "E2EE vs.
  passive node-operator state dumps, not vs. a malicious controller."
- **P3 — Groups + delivery.** Group/channel key agreement, member add/remove and key
  rotation, and a delivery model compatible with poll-only transport.
- **P4 — Recovery, backup, ephemeral, abuse, metadata.** Key recovery / backup,
  ephemeral / disappearing messages, abuse handling under encryption, and any feasible
  metadata-minimization. Real-time **calls remain off-chain (Web2)** and get their own
  threat model.

Throughout, the controller-as-code-delivery-root ceiling is the gating concern; SNS /
blackhole governance + reproducible builds + CSP/SRI are prerequisites for upgrading
any E2EE claim from "vs. passive operators" to "vs. the host."
