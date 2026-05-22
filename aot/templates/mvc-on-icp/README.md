# MvcOnIcp

Stock `dotnet new mvc` template — controllers, Razor Views, layout —
running entirely inside an Internet Computer canister.

## Quick start

```bash
dotnet build -c Release /p:IlcLlvmTarget=wasm32-wasi
dfx canister install MvcOnIcp --mode reinstall --yes \
  --wasm bin/Release/net10.0/wasi-wasm/publish/MvcOnIcp.wasm
open "http://$(dfx canister id MvcOnIcp).raw.localhost:4944/"
```

## Structure

- `Program.cs` — `builder.UseInternetComputer()`,
  `AddControllersWithViews()`, default route, `app.RunOnIC()`.
- `Controllers/HomeController.cs` — `Index` + `Privacy` actions.
- `Views/Home/{Index,Privacy}.cshtml` — Razor markup.
- `Views/Shared/_Layout.cshtml` — page chrome.

## What's IC-flavoured

- Views are rendered server-side inside `http_request_update`.
- Static asset routing via `app.MapStaticAssets()` works as usual
  (wwwroot content is bundled into the canister wasm).
- No PageModel state between requests — each render is a fresh
  controller invocation; persistent data should live in stable
  memory (see the BlazorWasp sample for the pattern).
