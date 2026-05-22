# WebApiOnIcp

A REST API canister — stock `dotnet new webapi` shape running entirely
inside an Internet Computer canister via Wasp's ASP.NET Core hosting.

## Quick start

```bash
dotnet build -c Release /p:IlcLlvmTarget=wasm32-wasi
dfx canister install WebApiOnIcp --mode reinstall --yes \
  --wasm bin/Release/net10.0/wasi-wasm/publish/WebApiOnIcp.wasm
curl http://$(dfx canister id WebApiOnIcp).raw.localhost:4944/WeatherForecast
```

## Structure

- `Program.cs` — one-line IC adapter via `builder.UseInternetComputer()`,
  source-gen JSON context registration, `app.RunOnIC()`.
- `Controllers/WeatherForecastController.cs` — stock ASP.NET controller.
  Returns 5 random forecasts seeded from `Ic0.time()`.

## What's IC-flavoured

- All routes are served by the canister's `http_request_update` thunk.
  Query verbs (GET) get an automatic upgrade to update path; M5 will
  add v1/v2 certified-asset caching for true-query semantics.
- `Ic0.time()` is the canister-wall-clock — deterministic per replica
  call. There's no `DateTime.UtcNow` consensus.
- Stable memory is available for state; this template doesn't use any.
