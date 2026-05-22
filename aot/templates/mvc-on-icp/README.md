# MvcOnIcp

Stock `dotnet new mvc` template — controllers, Razor Views, layout —
running entirely inside an Internet Computer canister.

> **⚠ Pre-release — packages not yet on nuget.org**
> This template references `Wasp.AspNetCore` as a NuGet package, but
> the package is still pre-release and **not published**. `dotnet
> restore` will fail on a fresh machine. To use this shape today,
> clone the wasp-dotnet repo and use `aot/samples/MvcVanilla` as a
> reference; replace the `<PackageReference>` here with a
> `<ProjectReference>` to `Wasp.AspNetCore.csproj`. We'll bump the
> template version once the framework ships on nuget.org.

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
