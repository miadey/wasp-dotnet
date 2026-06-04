# VetkdCsharpPoc — C# vetKeys (vetKD) client decryption, proven against mainnet

A proof-of-concept showing that **the IC vetKeys client-side crypto can be done entirely in C#**
(no JavaScript), and that it reproduces the `@dfinity/vetkeys` reference **byte-for-byte** against a
**real `encrypted_key` derived on mainnet** by the WASP Bzzz canister `razoronic`
(`4dcfc-hyaaa-aaaas-qdqbq-cai`).

## What it proves

`Program.cs` implements vetKD `decryptAndVerify` + the AES-key derivation in C# and checks it against
a golden vector (`vetkd_golden.json`) captured from mainnet. Expected output:

```
consistency e(c1,g2)==e(g1,c2): True
verify      e(k,g2)==e(H,dpk):  True
vetkey  C# == vetkey  JS   ✅
aes_okm C# == aes_okm JS   ✅
✅ C# vetKD decrypt reproduces the mainnet vetKey + AES key EXACTLY.
```

It uses **`Nethermind.Crypto.Bls`** — a binding over the audited **`blst`** library (the same BLS12-381
implementation the IC ecosystem uses). **No hand-rolled pairing crypto.**

## The algorithm (read from `@dfinity/vetkeys` v0.4.0 `utils.ts`)

- `encrypted_key` (192 B) = `c1` (G1, 48 B) ‖ `c2` (G2, 96 B) ‖ `c3` (G1, 48 B)
- consistency: `e(c1, g2) == e(g1, c2)`  (`PT.FinalVerify(PT(g2,c1), PT(c2,g1))`)
- unblind: `k = c3 - c1·tsk`  (transport secret `tsk` parsed **big-endian**)
- verify: `e(k, g2) == e(H, dpk)`, where `H = hashToG1(dpk‖input,
  DST="BLS_SIG_BLS12381G1_XMD:SHA-256_SSWU_RO_AUG_")`
- `vetkey = compress(k)` (48 B)
- AES key material = `HKDF-SHA256(ikm=vetkey, salt=∅, info="ic-vetkd-bls12-381-g2-aes-gcm-v2-" ‖ domainSep, 32)`

Because razoronic bakes the DM context into `vetkd_public_key`, the returned key is already fully
derived, so this port **skips** the offline canister-key / sub-key derivation step.

**Critical byte-match:** the vetKD `input` is `utf8(canonical(me, peer))` where `canonical` ordinal-sorts
the two principal texts and joins them with the **unit separator `0x1F`** — identical to the canister's
`DmService.CanonicalKey` and `VetkdService`. A wrong/empty separator yields "Invalid VetKey".

## Run it

```bash
dotnet run -c Release --project aot/samples/VetkdCsharpPoc
```

## Why this is NOT (yet) shipped in the browser

The crypto is correct in C#, but running it **in the browser** requires the .NET/mono WASM runtime
(**>1 MB brotli**) vs the `@dfinity/vetkeys` JS bundle (**~25 KB brotli**) — ~40× larger — and the
canister still ships the client code either way, so the **security ceiling is unchanged**. The web
client therefore uses the JS lib. The natural home for this C# crypto is a **native .NET client**
(desktop/MAUI), where `blst` is a native binary (no WASM penalty) and a client *outside* the canister's
control genuinely escapes the "canister ships the web code" trust ceiling.

## Note on the golden vector

`vetkd_golden.json` contains a **throwaway** ephemeral transport secret and the `encrypted_key` for the
conversation `(7mpgp…, dvl2u…)` — a canister principal that has no real DMs. It is a test vector only.
