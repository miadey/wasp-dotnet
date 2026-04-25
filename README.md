# wasp-dotnet

[![status](https://img.shields.io/badge/status-alpha-orange)](https://github.com/miadey/wasp-dotnet)
[![license](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
[![dotnet](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com)
[![ic](https://img.shields.io/badge/Internet%20Computer-canister-29ABE2)](https://internetcomputer.org)

The first .NET CDK for the Internet Computer. Two stories under one roof: a
shipped, working **NativeAOT-LLVM** path that compiles your C# directly into
canister wasm today, and an in-progress **runtime** path that re-hosts
Microsoft's pre-built `dotnet.native.wasm` (the Blazor WebAssembly engine)
inside an ICP canister to give you the full BCL plus reflection. Pick AOT for
small, fast, simple canisters; pick the runtime path when you need ASP.NET
Core, full reflection, or `Reflection.Emit`.

> Alpha. APIs and on-disk layout will change. Pin a commit; expect breaking
> changes between releases.

---

## Status

| Package / sample              | Path                              | Status            |
|------------------------------ |-----------------------------------|-------------------|
| `Wasp.IcCdk`                  | `aot/Wasp.IcCdk/`                 | shipped (alpha)   |
| `Wasp.IcCdk.SourceGenerator`  | `aot/Wasp.IcCdk.SourceGenerator/` | shipped (alpha)   |
| `Wasp.Http`                   | `aot/Wasp.Http/`                  | shipped (alpha)   |
| `Wasp.Outcalls`               | `aot/Wasp.Outcalls/`              | shipped (alpha)   |
| `Wasp.WebSockets`             | `aot/Wasp.WebSockets/`            | shipped (alpha)   |
| `samples/HelloWorld`          | `aot/samples/HelloWorld/`         | shipped           |
| `samples/Counter`             | `aot/samples/Counter/`            | shipped           |
| `samples/HelloWeb`            | `aot/samples/HelloWeb/`           | shipped           |
| `samples/HelloFetch`          | `aot/samples/HelloFetch/`         | shipped           |
| `samples/HelloChat`           | `aot/samples/HelloChat/`          | shipped           |
| `samples/BlazorChat`          | `aot/samples/BlazorChat/`         | shipped           |
| `runtime/wasp_canister`       | `runtime/wasp_canister/`          | Phase A — WIP     |
| `runtime/WaspHost`            | `runtime/wasp_dotnet_app/`        | Phase B — planned |
| `Wasp.Runtime.AspNetCore`     | `runtime/` (TBD)                  | Phase C — planned |
| `shared/tools/wasi-stub`      | `shared/tools/wasi-stub/`         | shipped           |
| `shared/tools/icp-publish`    | `shared/tools/icp-publish/`       | shipped           |

---

## Quick-start (AOT story)

Prerequisites: Docker, [`dfx`](https://internetcomputer.org/docs/current/developer-docs/setup/install/),
[`wasm-tools`](https://github.com/bytecodealliance/wasm-tools), and the
`wasp-dotnet-build` Docker image (see `aot/docker/Dockerfile.build`).

```bash
git clone https://github.com/miadey/wasp-dotnet.git
cd wasp-dotnet/aot

# build the Docker image once
docker build -t wasp-dotnet-build:latest -f docker/Dockerfile.build .

# build the wasi-stub helper
(cd ../shared/tools/wasi-stub && cargo build --release)

# bring up a local replica and deploy HelloWorld
dfx start --background --clean
dfx canister create hello
./samples/HelloWorld/build-and-deploy.sh

# call the canister
dfx canister call hello hello
```

You should see `("Hello from C# compiled to wasm by .NET 10")`.

---

## Architecture

```
┌──────────────────────────┐    ┌───────────────────────────┐    ┌─────────────────────┐
│ inputs                   │    │ build pipeline            │    │ canister            │
├──────────────────────────┤    ├───────────────────────────┤    ├─────────────────────┤
│                          │    │                           │    │                     │
│ AOT story                │    │  dotnet publish           │    │                     │
│  ─ Program.cs            │───▶│   /p:IlcLlvmTarget=       │───▶│  HelloWorld         │
│  ─ Wasp.IcCdk            │    │   wasm32-wasi             │    │   .canister.wasm    │
│                          │    │                           │    │                     │
│                          │    │  icp-publish.sh           │    │  ┌──────────────┐   │
│                          │    │   (rename exports)        │    │  │ ic0 imports  │   │
│                          │    │                           │    │  └──────────────┘   │
│                          │    │  wasi-stub                │    │                     │
│                          │    │   (no-op WASI)            │    │                     │
│                          │    │                           │    │                     │
│                          │    │  dfx canister install     │    │                     │
│                          │    │                           │    │                     │
├──────────────────────────┤    ├───────────────────────────┤    ├─────────────────────┤
│                          │    │                           │    │                     │
│ runtime story (WIP)      │    │  cargo build              │    │                     │
│  ─ WaspHost.dll          │    │   wasp_canister.wasm      │    │  merged.canister    │
│  ─ MS dotnet.native.wasm │───▶│                           │───▶│   .wasm             │
│  ─ wasp_canister (Rust)  │    │  wasm-merge               │    │                     │
│  ─ corelib + refpack     │    │   ─ resolve dotnet's      │    │  ┌──────────────┐   │
│                          │    │     env+wasi imports      │    │  │ Mono engine  │   │
│                          │    │                           │    │  ├──────────────┤   │
│                          │    │  icp-publish + wasi-stub  │    │  │ Rust shim    │   │
│                          │    │                           │    │  ├──────────────┤   │
│                          │    │  upload corelib + dlls    │    │  │ ic0 imports  │   │
│                          │    │   to stable memory        │    │  └──────────────┘   │
└──────────────────────────┘    └───────────────────────────┘    └─────────────────────┘
```

---

## When to pick which story

| Concern                         | `aot/`                               | `runtime/`                                       |
|---------------------------------|--------------------------------------|--------------------------------------------------|
| Status                          | shipped, alpha                       | Phase A in progress                              |
| Canister wasm size              | small (~1–3 MB per canister)         | larger (~6–8 MB shared engine + uploaded dlls)   |
| Cold start                      | fast                                 | slower (engine init + assembly load)             |
| Hot-path instructions / call    | low                                  | higher (interp; jiterpreter disabled)            |
| Reflection (read-only)          | very limited (trim-safe only)        | full                                             |
| `Reflection.Emit` / dynamic IL  | not supported                        | full                                             |
| Full BCL                        | partial (NativeAOT trimmed)          | full                                             |
| Managed exceptions              | yes                                  | v0.1: ban / trap (Emscripten C++ EH stubbed)     |
| ASP.NET Core minimal API        | not realistic                        | Phase C target                                   |
| EF Core / dynamic ORMs          | no (relies on `Reflection.Emit`)     | yes (Phase C onward)                             |
| Crypto via `System.Security.*`  | mostly `PlatformNotSupported`        | works (full BCL)                                 |
| Best fit                        | tight, performance-sensitive logic   | porting existing .NET code, dynamic frameworks   |

---

## Known limitations of the AOT story today

- **No `Reflection.Emit`.** NativeAOT explicitly forbids it. Frameworks that
  rely on runtime IL generation (EF Core, Newtonsoft.Json's reflection path,
  many DI containers) will not work.
- **No full BCL.** Trimming is aggressive; many APIs link out. Source-gen
  alternatives exist for JSON (`System.Text.Json` with the source generator)
  and similar.
- **`System.Security.Cryptography.SHA256.HashData()` is `PlatformNotSupported`
  under wasi-wasm.** `Wasp.IcCdk` ships a hand-rolled SHA-256 to work around
  this. The same applies to most of `System.Security.Cryptography`.
- **No threading.** The IC canister model is single-threaded; user code must
  be too.
- **macOS arm64 hosts cannot build directly.** NativeAOT-LLVM has no Mac
  arm64 host package yet, so all wasm builds run inside the
  `wasp-dotnet-build` Linux x64 Docker image. Linux x64 hosts can build
  natively (see `aot/docker/Dockerfile.build` for the toolchain set).
- **C++ exception ABI / Emscripten SJLJ** is stubbed; managed code that
  throws will trap the canister. Catch at boundaries you control.

---

## The five sample canisters + Blazor front-end

| Sample                      | Role                                                                 |
|-----------------------------|----------------------------------------------------------------------|
| `aot/samples/HelloWorld`    | Smallest possible canister: one query method, no state.              |
| `aot/samples/Counter`       | `[CanisterQuery]` / `[CanisterUpdate]` + `StableCell<ulong>`.        |
| `aot/samples/HelloWeb`      | `http_request` handler — serves HTML over the IC HTTP gateway.       |
| `aot/samples/HelloFetch`    | Outbound HTTPS via `Wasp.Outcalls` — IC HTTPS outcalls from C#.      |
| `aot/samples/HelloChat`     | `Wasp.WebSockets` server — IC-WebSocket protocol implementation.     |
| `aot/samples/BlazorChat`    | Blazor WebAssembly front-end talking to `HelloChat`. Browser side.   |

`BlazorChat` is also the source of the pre-built `dotnet.native.wasm` the
runtime story re-hosts.

---

## Roadmap

Tracked on GitHub.

- [Issues](https://github.com/miadey/wasp-dotnet/issues)
- [Milestones](https://github.com/miadey/wasp-dotnet/milestones)
  - **P0: Bootstrap** — repo, docs, CI for the AOT story
  - **Phase A: Hello-world via wasm-merge** — Rust shim + `wasm-merge` spike
  - **Phase B: Friendly C# CDK** — same `[CanisterQuery]` ergonomics, runtime-side
  - **Phase C: ASP.NET Core integration** — `IcServer : IServer`, asyncify chunking
  - **Phase D: Polish + mainnet** — perf table, NuGet 0.1.0-preview, mainnet IDs

---

## Repo layout

```
wasp-dotnet/
  aot/              # shipped CDK + samples (NativeAOT-LLVM)
  runtime/          # in-progress runtime path (wasm-merge dotnet.native.wasm)
  shared/tools/     # wasi-stub, icp-publish — used by both stories
  docs/
  .github/workflows/
```

---

## License

[MIT](LICENSE) — copyright 2026 miadey.

## Author

[miadey](https://github.com/miadey)

## Acknowledgements

Standing on the shoulders of:

- [`dotnet/runtime`](https://github.com/dotnet/runtime) — .NET 10, NativeAOT-LLVM, and Mono's WASM build
- [`dfinity/sdk`](https://github.com/dfinity/sdk) — `dfx`, the Internet Computer SDK
- [`omnia-network/ic-websocket-cdk-rs`](https://github.com/omnia-network/ic-websocket-cdk-rs) — the IC-WebSocket protocol that `Wasp.WebSockets` ports
- [`bytecodealliance/wasm-tools`](https://github.com/bytecodealliance/wasm-tools) — wasm parsing, printing, component tooling
- [`WebAssembly/binaryen`](https://github.com/WebAssembly/binaryen) — `wasm-merge`, the linker that makes the runtime story possible
- [`walrus`](https://github.com/rustwasm/walrus) — wasm rewriter behind our `wasi-stub` tool
