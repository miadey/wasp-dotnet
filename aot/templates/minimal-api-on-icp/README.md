# MinimalApiOnIcp

Single-file Program.cs with top-level `app.MapGet` / `app.MapPost`
handlers — the lightest possible HTTP backend canister.

> **⚠ Pre-release — packages not yet on nuget.org**
> This template references `Wasp.AspNetCore` as a NuGet package, but
> the package is still pre-release and **not published**. `dotnet
> restore` will fail on a fresh machine. To use this shape today,
> clone the wasp-dotnet repo and use `aot/samples/AspNetCoreHello` or
> `aot/samples/AspNetCoreEndpoints` as a reference; replace the
> `<PackageReference>` with a `<ProjectReference>` to
> `Wasp.AspNetCore.csproj`. We'll bump the template version once the
> framework ships on nuget.org.

## Quick start

```bash
dotnet build -c Release /p:IlcLlvmTarget=wasm32-wasi
dfx canister install MinimalApiOnIcp --mode reinstall --yes \
  --wasm bin/Release/net10.0/wasi-wasm/publish/MinimalApiOnIcp.wasm
CID=$(dfx canister id MinimalApiOnIcp)
curl http://$CID.raw.localhost:4944/
curl http://$CID.raw.localhost:4944/echo/hello
curl -X POST -d '{"title":"buy milk","priority":1}' -H 'content-type: application/json' \
     http://$CID.raw.localhost:4944/note
```

## Structure

- `Program.cs` — everything. Builder, JSON source-gen context, route
  handlers, IC adapter.

## What's IC-flavoured

- `Ic0.time()` for canister wall clock (nanoseconds, deterministic).
- `app.RunOnIC()` instead of `app.Run()` — non-blocking startup that
  hands control to the canister thunks.
- Source-gen JSON via `[JsonSerializable]` — pure reflection
  serialisation doesn't survive AOT trimming. Add new types to
  `JsonCtx` when you accept/return them.
