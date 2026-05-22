# Sample test plan

Four samples — one per `Wasp.Templates` shape. All four build to wasm
via the same docker pipeline (`build-and-deploy.sh` in each
directory), deploy through `dfx`, and respond on the boundary node's
`.raw.localhost:4944` subdomain.

```bash
dfx start --background           # if not already running
# Build & deploy any sample:
aot/samples/<Name>/build-and-deploy.sh
```

Below: one canonical test per shape. Times shown are from a fresh
local dfx replica on macOS arm64.

## 1. `blazor-on-icp` — `samples/BlazorWasp`

Render-as-query Blazor with Home / Counter / Weather / Chat pages.

```bash
CID=$(dfx canister id blazorwasp)
# Home page (SSR, query path)
curl -sS http://$CID.raw.localhost:4944/                           # ~30 ms, 200
# /counter renders with deterministic data-wasp-evt-click id
curl -sS http://$CID.raw.localhost:4944/counter | grep data-wasp   # ~10 ms
# Click event — POST update call, returns post-event render inline
HID=$(curl -sS "http://$CID.raw.localhost:4944/_wasp/render?path=/counter" \
      | python3 -c 'import json,sys,re; d=json.load(sys.stdin); print(re.search(r"data-wasp-evt-click=\"([a-f0-9]+)\"", d["html"]).group(1))')
curl -sS -X POST -H "content-type: application/json" \
     --data "{\"path\":\"/counter\",\"handlerId\":\"$HID\",\"args\":{}}" \
     http://$CID.raw.localhost:4944/_wasp/event                    # ~1.2 s, count++
# Chat page (composer + scrollable messages + sidebar)
curl -sS http://$CID.raw.localhost:4944/chat | grep dc-composer-input
```

## 2. `webapi-on-icp` — `samples/WebApiVanilla`

Stock `dotnet new webapi` controllers with source-gen JSON.

```bash
CID=$(dfx canister id webapivanilla)
curl -sS http://$CID.raw.localhost:4944/WeatherForecast | jq .
# → [{date:..., temperatureC:..., summary:...}, ...]  (5 records, ~1.5 s)
```

## 3. `mvc-on-icp` — `samples/MvcVanilla`

Stock `dotnet new mvc` controllers + Razor Views.

```bash
CID=$(dfx canister id mvcvanilla)
curl -sS http://$CID.raw.localhost:4944/         | grep '<h1>'   # → "Welcome…"
curl -sS http://$CID.raw.localhost:4944/Home/Privacy | grep '<h1>' # → "Privacy Policy"
```

## 4. `minimal-api-on-icp` — `samples/AspNetCoreEndpoints`

Top-level `app.MapGet` / `app.MapPost` endpoints, no controllers.

```bash
CID=$(dfx canister id aspnetcoreendpoints)
curl -sS http://$CID.raw.localhost:4944/                 # "Hello from AspNetCoreEndpoints"
curl -sS http://$CID.raw.localhost:4944/echo/hello       # "echo: hello"
curl -sS -X POST -H 'content-type: application/json' \
     -d '{"Title":"buy milk","Priority":2}' \
     http://$CID.raw.localhost:4944/note                 # 200, body acknowledges note
```

## Verified end-to-end on local dfx

| Shape | Sample | Cold response | Mainnet equivalent |
|---|---|---|---|
| blazor-on-icp | BlazorWasp | 33 ms GET, 1.2 s click | https://4dcfc-hyaaa-aaaas-qdqbq-cai.icp0.io/ |
| webapi-on-icp | WebApiVanilla | 1.6 s | n/a (deploy via cycle top-up) |
| mvc-on-icp | MvcVanilla | 1.6 s | n/a |
| minimal-api-on-icp | AspNetCoreEndpoints | 1.5 s | n/a |

The mainnet canister we own (`4dcfc-…`) currently runs the BlazorWasp
sample. To swap in another sample for live mainnet testing:

```bash
dfx canister install 4dcfc-hyaaa-aaaas-qdqbq-cai \
    --network ic --mode reinstall --yes \
    --wasm aot/samples/<Name>/<Name>.canister.wasm
```

## What each sample proves

| File | Demonstrates |
|---|---|
| `samples/BlazorWasp/Components/Pages/Counter.razor` | Stock `@onclick="Method"` mapping to deterministic `data-wasp-evt-click` |
| `samples/BlazorWasp/Components/Pages/Chat.razor` | Form input + `data-wasp-persist` + cross-device 3 s polling |
| `samples/BlazorWasp/WeatherService.cs` | Persistent state in stable memory (offset 64, "WEAH" magic) |
| `samples/WebApiVanilla/Controllers/WeatherForecastController.cs` | Stock `[ApiController]` + `[HttpGet]`, source-gen JSON |
| `samples/MvcVanilla/Views/Home/Index.cshtml` | Razor View rendered server-side from update call |
| `samples/AspNetCoreEndpoints/Program.cs` | Top-level `app.MapGet` / `app.MapPost`, `Results.Json`, route-param binding |

All four use the same one-line IC adapter: `builder.UseInternetComputer()`
in the builder phase + `app.RunOnIC()` in place of `app.Run()` (or
`app.UseInternetComputerWasp()` for the Blazor render-as-query variant).
