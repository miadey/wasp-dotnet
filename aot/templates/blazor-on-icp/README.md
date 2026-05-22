# BlazorOnIcp

A Blazor app that runs entirely inside an Internet Computer canister
via Wasp's render-as-query architecture.

> **⚠ Pre-release — packages not yet on nuget.org**
> This template references `Wasp.AspNetCore.Blazor.Wasp` as a NuGet
> package, but the package is still pre-release and **not published**.
> `dotnet restore` will fail on a fresh machine. To use this shape
> today, clone the wasp-dotnet repo and copy these files into a
> sample under `aot/samples/` with `<ProjectReference>` paths instead
> of `<PackageReference>`. Once the framework packages ship on
> nuget.org we'll bump the template version and `restore` will work
> end-to-end.

## Quick start

```bash
# Build the canister
dotnet build -c Release /p:IlcLlvmTarget=wasm32-wasi

# Deploy locally (requires `dfx start --background`)
dfx canister install BlazorOnIcp --mode reinstall --yes \
  --wasm bin/Release/net10.0/wasi-wasm/publish/BlazorOnIcp.wasm
```

## What's stock-Blazor here

- `Components/Pages/Counter.razor` — `<button @onclick="Method">`
  works exactly like stock Blazor Server. The bridge translates
  `@onclick` to a `data-wasp-evt-click` data attribute with a
  deterministic id; the server's renderer looks up the handler and
  invokes it inside an update call.
- `_Imports.razor`, `@inject`, `@page` — all standard.

## What's IC-flavoured

- **No long-lived circuit**. Each render is a fresh component
  instance. State that survives across calls lives in DI singletons
  (`CounterService`) or stable memory.
- **No SignalR**. Two HTTP endpoints behind the IC gateway:
  `GET /_wasp/render` runs in canister_query (~300 ms on mainnet);
  `POST /_wasp/event` runs in canister_update (~2 s consensus).
- **Cross-device reactivity** via a 3 s background poll (faster after
  a local event). Open the app on two devices — they stay in sync.

## Where to take it next

- Add another route: `router.AddRoute<MyPage>("/my-page");` in
  `Program.cs` + a `Components/Pages/MyPage.razor`.
- Persist state in stable memory: see `CounterService.cs` for the
  pattern.
- Multi-user data + form input: see the chat sample in the
  wasp-dotnet repo for the `WaspContext.FormArgs` pattern.
