# WaspCMS — Per-Site IC-Native CMS (Umbraco Clone)

> **Proposal, not implemented product documentation.** Moved from
> `umbracoClone.md` during the 2026-11-06 cleanup. This preserves the original
> plan, estimates, paths, and assumptions; it does not establish a working CMS,
> current platform limits, or a completed site migration. Start with the
> [repository guide](../../README.md) for the implemented stack.

## Context

Migrate ~5-ish business sites (first target: `croisieresaml.com`, ~50–75 pages, image-heavy, multi-language) off Umbraco and onto the Internet Computer. Hosting Umbraco proper inside a canister is infeasible (plugin/reflection model + EF Core AOT issues + wasm size). The answer is an **Umbraco-flavoured CMS purpose-built for this stack** — same editorial UX, fraction of the feature set, fully on-chain.

Architecture (settled in prior discussions):
- **One canister per site**, fully self-contained: backoffice + content + delivery + media.
- **Blazor WebAssembly backoffice** mounted at `/admin`, modelled exactly on the existing `CrmWasm → BlazorWasp` embedding pattern.
- **Razor-rendered public site** via the existing `WaspRouter` machinery.
- **II-based editor identity** via the existing cosmetic-II pattern already shipping in BlazorWasp.
- **Stable memory** for everything durable (content nodes, doc-type definitions, media, identity bindings).

This plan starts a **clean new repo** (no fork of `dev/csharp`) that builds, AOTs, packages, and deploys to mainnet using the exact pipeline BlazorWasp uses today.

---

## Repo Layout

```
waspcms/                                          ← new git repo
├── .gitignore
├── README.md
├── dfx.json                                       ← canister registry
├── canister_ids.json                              ← committed empty; populated per-env
├── docker/
│   └── Dockerfile.build                           ← copy verbatim from aot/docker/Dockerfile.build
├── shared/
│   └── tools/                                     ← copy verbatim from /Users/miadey/dev/csharp/shared/tools/
│       ├── icp-publish/                           ← shell + wasm-tools + ic-wasm
│       ├── wasi-stub/                             ← Rust binary; prebuilt OK
│       ├── Wasp.CircuitHostWeaver/                ← Cecil rewriter
│       └── Wasp.RenderTreeWeaver/                 ← Cecil rewriter
├── framework/                                     ← Wasp framework, copied as-is
│   ├── Wasp.IcCdk/                                ← from aot/Wasp.IcCdk/
│   ├── Wasp.AspNetCore/                           ← from aot/Wasp.AspNetCore/
│   └── Wasp.AspNetCore.Blazor.Wasp/               ← from aot/Wasp.AspNetCore.Blazor.Wasp/
├── src/
│   ├── WaspCms.Canister/                          ← the host canister project
│   │   ├── WaspCms.Canister.csproj
│   │   ├── waspcms.did                            ← copy from BlazorWasp's blazorwasp.did
│   │   ├── Program.cs                             ← bootstrap, route wiring, page shell
│   │   ├── Services/
│   │   │   ├── CmsService.cs                      ← doc types + content tree + properties
│   │   │   ├── MediaService.cs                    ← extracted from ImageStore
│   │   │   ├── IdentityService.cs                 ← copy from BlazorWasp
│   │   │   ├── AdminService.cs                    ← copy from BlazorWasp
│   │   │   └── PresenceService.cs                 ← copy from BlazorWasp (collab indicators)
│   │   ├── Routing/
│   │   │   ├── CmsRouter.cs                       ← content-tree-path → doc-type → template
│   │   │   └── RedirectMap.cs                     ← old-URL → new-URL table (for migration)
│   │   ├── Templates/                             ← Razor views per doc type
│   │   │   ├── _Layout.cshtml
│   │   │   ├── HomePage.razor                     ← per-doc-type templates discovered by convention
│   │   │   ├── CruisePage.razor
│   │   │   ├── StandardPage.razor
│   │   │   └── NewsArticle.razor
│   │   ├── Endpoints/
│   │   │   ├── CmsApiEndpoints.cs                 ← /api/cms/* MapPost + RegisterQueryHandler
│   │   │   ├── MediaEndpoints.cs                  ← /_cms/img/{id}, /_cms/file/{id}
│   │   │   └── PublicAssets.cs                    ← embeds CmsWasm into static-asset table
│   │   ├── Static/
│   │   │   ├── styles.css                         ← site CSS (per-template overrides ok)
│   │   │   └── wasp-ii.js                         ← extracted from BlazorWasp Program.cs
│   │   └── BuildPage.cs                           ← extracted shell builder (top nav, body chrome)
│   └── WaspCms.Backoffice/                        ← Blazor WASM admin SPA
│       ├── WaspCms.Backoffice.csproj
│       ├── Program.cs                             ← DI: HttpClient, AuthService, CmsApiClient
│       ├── wwwroot/
│       │   ├── index.html
│       │   └── css/admin.css
│       ├── App.razor
│       ├── Layout/
│       │   ├── BackofficeLayout.razor             ← Umbraco-style left tree + main + properties rail
│       │   └── LoginScreen.razor
│       ├── Pages/
│       │   ├── Content.razor                      ← /content : content-tree + node editor
│       │   ├── Media.razor                        ← /media : media library
│       │   ├── DocTypes.razor                     ← /doctypes : schema editor
│       │   ├── Settings.razor
│       │   └── Login.razor
│       ├── PropertyEditors/
│       │   ├── TextEditor.razor
│       │   ├── RichTextEditor.razor               ← bundles Quill (vendored)
│       │   ├── ImagePicker.razor
│       │   ├── MediaListEditor.razor
│       │   ├── ContentPicker.razor
│       │   ├── LinkEditor.razor
│       │   ├── DateEditor.razor
│       │   ├── BooleanEditor.razor
│       │   ├── ListEditor.razor
│       │   └── PropertyEditorRegistry.cs          ← maps editor alias → component
│       ├── Services/
│       │   ├── CmsApiClient.cs                    ← typed wrapper over /api/cms/*
│       │   ├── AuthService.cs                     ← II sign-in flow (lazy-import dfinity bundle)
│       │   └── MediaUploader.cs                   ← chunked upload to /api/cms/media
│       └── Models/
│           ├── ContentNode.cs                     ← shared with canister via a tiny shared lib (optional)
│           ├── DocType.cs
│           └── PropertyDef.cs
├── tools/
│   └── WaspCms.MigrateUmbraco/                    ← console app (NET 10, NOT AOT)
│       ├── WaspCms.MigrateUmbraco.csproj
│       └── Program.cs                             ← scrapes/pulls Umbraco, posts to canister
└── scripts/
    ├── build-and-deploy.sh                        ← adapted from BlazorWasp's
    ├── build-docker-image.sh                      ← one-shot: `docker build -t wasp-dotnet-build:latest -f docker/Dockerfile.build .`
    └── deploy-mainnet.sh                          ← thin wrapper with DFX_WARNING + --network ic + upgrade
```

---

## Build Pipeline (verbatim from BlazorWasp, no surprises)

### Prerequisites (host machine)
- `dotnet` SDK 10.0 (host-side Blazor WASM publish)
- Docker (linux/amd64 — for the AOT compile)
- `dfx` (≥0.20)
- `wasm-tools`, `ic-wasm`, `wasm-opt` (binaryen) — same as BlazorWasp expects
- A wallet identity with cycles for mainnet (or a controller identity inheriting from existing canister)

### `scripts/build-docker-image.sh` (one-time)
```bash
#!/usr/bin/env bash
set -euo pipefail
docker build --platform linux/amd64 \
  -t wasp-dotnet-build:latest \
  -f docker/Dockerfile.build .
```

### `scripts/build-and-deploy.sh` (clone of BlazorWasp's)
Order is critical (the WASM sub-app must publish before the host csproj builds — host embeds its output):

```bash
#!/usr/bin/env bash
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO"

# 1. Publish Blazor WASM backoffice on host (Docker doesn't ship Blazor SDK).
dotnet publish src/WaspCms.Backoffice/WaspCms.Backoffice.csproj -c Release --nologo

# 2. AOT-compile host canister inside Docker.
docker run --rm --platform linux/amd64 \
  -v "$REPO:/work" -v wasp-nuget:/nuget \
  wasp-dotnet-build:latest \
  bash -c "cd /work/src/WaspCms.Canister && dotnet build -c Release /p:IlcLlvmTarget=wasm32-wasi"

# 3. Post-process (icp-publish → wasi-stub → wasm-opt -Oz).
RAW=src/WaspCms.Canister/bin/Release/net10.0/wasi-wasm/publish/WaspCms.Canister.wasm
OUT=src/WaspCms.Canister/WaspCms.Canister.wasm
TMP1=$(mktemp); TMP2=$(mktemp)
shared/tools/icp-publish/icp-publish.sh "$RAW" "$TMP1"
shared/tools/wasi-stub/target/release/wasi-stub "$TMP1" "$TMP2"
wasm-opt -Oz --enable-bulk-memory --enable-multivalue \
  --enable-reference-types --enable-simd \
  --enable-nontrapping-float-to-int --enable-sign-ext \
  "$TMP2" -o "$OUT"

# 4. Deploy locally (default) or mainnet via WASP_NETWORK env.
MODE="${WASP_DEPLOY_MODE:-upgrade}"
NETWORK="${WASP_NETWORK:-local}"
if [[ "$NETWORK" == "ic" ]]; then
  DFX_WARNING=-mainnet_plaintext_identity dfx canister --network ic install waspcms-aml \
    --mode "$MODE" --yes --wasm "$OUT"
else
  dfx canister create waspcms-aml 2>/dev/null || true
  dfx canister install waspcms-aml --mode "$MODE" --yes --wasm "$OUT"
fi
```

### `docker/Dockerfile.build`
**Copy verbatim** from `/Users/miadey/dev/csharp/aot/docker/Dockerfile.build`. Provides .NET 10 SDK + wasi-sdk-24.0 + wasm-ld shim that strips `--component-type` flags from ILC.

### `shared/tools/`
**Copy verbatim** from `/Users/miadey/dev/csharp/shared/tools/`. Pre-built `wasi-stub` binary is fine; only rebuild if you modify it.

---

## Canister Host Project (`WaspCms.Canister.csproj`)

Mirror `BlazorWasp.csproj` exactly. Key parts:
- `Microsoft.NET.Sdk.Razor`
- `OutputType=Library`, `NativeLib=Shared`, `TargetFramework=net10.0`, `RuntimeIdentifier=wasi-wasm`, `IlcLlvmTarget=wasm32-wasi`
- `PublishTrimmed=true`, `TrimMode=full`, `InvariantGlobalization=true`
- Runtime feature disables: EventSource, Metrics, Debugger, COM, native HTTP handler (full list lines 20–33 of BlazorWasp.csproj)
- Package refs: `BytecodeAlliance.Componentize.DotNet.Wasm.SDK` v0.7.0-preview00010, `runtime.linux-x64.Microsoft.DotNet.ILCompiler.LLVM` v10.0.0-alpha.1.25162.1
- `ProjectReference` to the three framework projects under `framework/`
- TrimmerRootAssembly for all four projects (host + 3 framework)
- `UnmanagedEntryPointsAssembly` for `WaspCms.Canister` and `Wasp.AspNetCore`
- `Import Project="..\..\framework\Wasp.AspNetCore\Wasp.AspNetCore.targets"`
- **EmbeddedResource** for the backoffice publish output:
```xml
<EmbeddedResource Include="..\WaspCms.Backoffice\bin\Release\net10.0\publish\wwwroot\admin\**\*"
                  Exclude="..\WaspCms.Backoffice\bin\Release\net10.0\publish\wwwroot\admin\**\*.br;..\WaspCms.Backoffice\bin\Release\net10.0\publish\wwwroot\admin\**\*.gz">
  <LogicalName>admin/%(RecursiveDir)%(FileName)%(Extension)</LogicalName>
</EmbeddedResource>
```

---

## Backoffice Project (`WaspCms.Backoffice.csproj`)

Mirror `CrmWasm.csproj`:
- `Microsoft.NET.Sdk.BlazorWebAssembly`
- `StaticWebAssetBasePath=admin`
- `BlazorWebAssemblyJiterpreter=true`, `InvariantGlobalization=true`, `PublishTrimmed=true`, `TrimMode=partial`
- `Microsoft.AspNetCore.Components.WebAssembly` v10.0
- **UI library choice**: skip FluentUI for v1 (heavier, slower trim). Hand-rolled CSS modelled on Umbraco's backoffice look. Revisit when needed.
- Vendor **Quill 2.x** (~50KB minified) into `wwwroot/lib/quill/` for the rich-text editor — single drop-in JS file. License MIT.

---

## Core Data Model (`Services/CmsService.cs`)

Persisted in stable memory, single offset block per logical section, magic+version on each:

```
DocType { Alias, Name, AllowedChildren[], Properties[PropertyDef], TemplateAlias }
PropertyDef { Alias, Name, EditorAlias, Required, Config(json) }
ContentNode {
    Id: i64,
    ParentId: i64,
    DocTypeAlias: string,
    SortOrder: i32,
    Path: i64[],            // ancestor chain
    Slug: string,
    UrlSegment: string,
    CreatedAtMs, UpdatedAtMs,
    PublishState: { Draft, Published, Unpublished },
    Cultures: dict<culture, {
        Name: string,
        Properties: dict<alias, json-value>,
        PublishedAtMs?, UpdatedAtMs
    }>
}
RedirectEntry { FromPath, ToContentId, StatusCode }
```

Stable memory regions (avoiding existing BlazorWasp offsets so you can copy services without conflicts):
- `CmsService` → `5_500_000` (free in current layout)
- `MediaService` → `1_000_000` (existing ImageStore offset; rename module)
- `IdentityService` → `5_900_000`
- `AdminService` → `900_000`

Public API surface of `CmsService` (matches the WASM-frontend → host-backend pattern of `CrmService`):
- **Doc types**: `AllDocTypes()`, `FindDocType(alias)`, `UpsertDocType(d)`, `DeleteDocType(alias)`
- **Tree**: `Root(culture?)`, `Children(parentId, culture?)`, `Find(id, culture?)`, `FindByPath(path, culture)`
- **Content**: `CreateNode(parentId, docType, culture, initialName)`, `UpdateProperty(id, culture, alias, value)`, `Move(id, newParentId, sortOrder)`, `Publish(id, culture)`, `Unpublish(id, culture)`, `DeleteNode(id)`
- **Redirects**: `AddRedirect(from, to)`, `ResolveRedirect(path)`, `AllRedirects()`
- **Search** (v2 — flag for now): `Search(q, docType?, culture?)`

---

## HTTP Endpoints

### Backoffice REST (all `/api/cms/*`, modelled on `/api/crm/*`)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/cms/doctypes` | list doc types |
| POST | `/api/cms/doctypes` | upsert |
| DELETE | `/api/cms/doctypes/{alias}` | delete |
| GET | `/api/cms/tree?parent={id}&culture={c}` | children of parent (or root) |
| GET | `/api/cms/node?id={id}&culture={c}` | full node + property values |
| POST | `/api/cms/nodes` | create draft |
| PUT | `/api/cms/nodes/{id}` | update properties (multi-culture body) |
| POST | `/api/cms/nodes/{id}/publish` | publish a culture |
| POST | `/api/cms/nodes/{id}/unpublish` | unpublish |
| POST | `/api/cms/nodes/{id}/move` | reparent / reorder |
| DELETE | `/api/cms/nodes/{id}` | delete |
| GET | `/api/cms/media` | list media |
| POST | `/api/cms/media` | upload (multipart) |
| GET | `/api/cms/redirects` | list |
| POST | `/api/cms/redirects` | add |

**Auth**: every request includes `X-Wasp-Principal` header (set by backoffice from the II identity). `AdminService` gates writes. Pattern is exactly what `CrmWasm/ApiClient.cs` does today.

### Public delivery
- `/` and `/<path/to/page>` → `CmsRouter` resolves to a `ContentNode` + culture, picks the matching `Razor` template, server-renders via `WaspRouter`.
- `/_cms/img/{id}?w={width}` → query handler streaming from `MediaService`, with on-the-fly resize cached in stable memory keyed by `(id, width)`.
- `/_cms/file/{id}` → original-blob serve for non-image media.
- `/sitemap.xml`, `/robots.txt` → query handlers generated from published tree.

### Backoffice serving
- `/admin` and `/admin/*` → static-asset serve from embedded `admin/` resources (pattern of `CrmAssets.Register()`).

---

## Multi-Language

Localization is **per-property, per-culture** — Umbraco's native model. `ContentNode.Cultures` is a dict; templates read via a `ICultureContext.Get<T>(alias)` helper. Default culture configured per site (FR for AML). URL strategy: `/fr/...` vs `/en/...` prefix; root culture omits prefix optionally (config flag).

Cultures registered as a site-level setting (stored in `CmsService` alongside doc types), default `["fr-CA", "en-CA"]` for AML.

---

## Template Discovery

By convention. Each doc type has a `TemplateAlias` (defaults to its own alias). At canister startup:
- Walk Razor components in `Templates/` whose name matches a registered doc type alias (or is registered via `[CmsTemplate("homePage")]` attribute).
- Register each with `WaspRouter` as a dynamic-content endpoint.
- The router instantiates the component with a `[Parameter] ContentNode` plus `[Parameter] CultureContext` injected.

Adding a new page type = drop a `.razor` file, define the doc type in the backoffice. Zero re-wiring.

---

## Internet Identity Integration

**Verbatim** from BlazorWasp's current setup:
- Pull `IdentityService.cs` from `samples/BlazorWasp/IdentityService.cs` — already builds, persisting principal → display-name in stable memory.
- Pull the `IiClientScript` block from `BlazorWasp/Program.cs` and **extract it to `src/WaspCms.Canister/Static/wasp-ii.js`** (served as a certified static asset). Cleaner than inlining in the page shell.
- Backoffice `AuthService.cs` uses the same lazy-import pattern (esm.sh with pinned `?deps=` to avoid the `bufFromBufLike` regression you already hit).

Editor permission model:
- First principal to sign in → automatically claimed as **admin** if `AdminService` is empty (bootstrap).
- Admins can promote other principals via `/admin/settings`.
- Non-admin signed-in users get **read-only** preview access until promoted.

---

## Migration Tool (`tools/WaspCms.MigrateUmbraco/`)

A regular .NET 10 console app — **not** AOT, runs on your laptop. Steps:

1. Read source URL list (sitemap.xml or curated list).
2. For each URL:
   - Fetch HTML
   - Use AngleSharp to parse, extract `<title>`, `<meta>`, structured content blocks based on configurable selectors.
   - Map to a doc-type draft (config file: `aml.migration.yaml` maps URL patterns → doc type + property selector map).
   - POST to canister's `/api/cms/nodes` with culture-aware payloads.
3. Mirror the URL → new-content-id into the redirects table.
4. For images: download, POST to `/api/cms/media`, store the URL substitution map, run a pass through the rich-text bodies to rewrite `<img src>` references.

For AML specifically: pre-write the YAML config for the ~7–10 doc types you'll need (Home, Cruise, Schedule, Fleet, About, ContactPage, NewsArticle, etc.) — that's the migration's actual work.

---

## Custom Domain (`croisieresaml.com`)

Standard IC boundary-node flow:
1. After first mainnet deploy, fetch the canister principal.
2. Add CNAME `croisieresaml.com → <canister>.icp0.io` (or the `_canister-id.<domain>` TXT records per the IC custom-domain spec).
3. Submit a registration request via the IC's domain registration endpoint (or use `dfx canister --network ic call <registrations> register`).
4. Wait for boundary-node propagation (~hours).
5. Configure the canister to recognise the host header for site-root selection (`CmsRouter` picks "site root" by Host: header in v2 if you ever consolidate to multi-site).

For v1 (single-site canister), only one Host: matters — keep it simple.

---

## Phased Delivery

| Week | Output |
|---|---|
| 1 | Repo bootstrap: copy framework, docker image, build script. First canister build that boots, serves a hello-world Razor page on local replica. |
| 2 | `CmsService` schema + doc-type CRUD endpoints + backoffice shell with II login + content-tree page (list + create node, no editing yet). |
| 3 | Property editors: text, richtext (Quill bundled), image, link, content-picker. Save + publish flow. Template auto-discovery wired. First doc type rendering end-to-end. |
| 4 | Media library (browser + upload + alt-text + on-the-fly resize). Remaining property editors (list, date, bool). Backoffice polish. |
| 5 | Multi-culture model + URL routing for `/fr` vs `/en`. Migration tool against AML site (real content flowing). |
| 6 | Templating for AML's actual look (scrape current CSS/structure). SEO endpoints (sitemap, robots, meta). 404 + redirect table. |
| 7 | First mainnet deploy under temporary `<canister>.icp0.io`. Stability + cycle-cost characterisation. |
| 8 | Custom domain cutover. DNS propagation. Production go-live. |

Subsequent sites: ~1–2 weeks each, near-zero new code beyond a YAML migration config + a few site-specific templates.

---

## Critical Files to Author (new code)

1. `src/WaspCms.Canister/Services/CmsService.cs` — main data model & persistence (largest single piece, ~800 LOC).
2. `src/WaspCms.Canister/Routing/CmsRouter.cs` — path → node → template resolution.
3. `src/WaspCms.Canister/Endpoints/CmsApiEndpoints.cs` — REST surface for backoffice.
4. `src/WaspCms.Canister/Program.cs` — service registration + endpoint wiring + page shell.
5. `src/WaspCms.Canister/Templates/*.razor` — per-doc-type templates (AML: 7–10).
6. `src/WaspCms.Backoffice/Pages/Content.razor` — content tree + editor (largest backoffice piece, ~600 LOC).
7. `src/WaspCms.Backoffice/PropertyEditors/*.razor` — ~10 components, ~80–150 LOC each.
8. `src/WaspCms.Backoffice/Services/CmsApiClient.cs` — typed wrapper.
9. `tools/WaspCms.MigrateUmbraco/Program.cs` + `aml.migration.yaml`.

## Critical Files to Copy Verbatim

From `/Users/miadey/dev/csharp/`:
- `aot/docker/Dockerfile.build` → `docker/Dockerfile.build`
- `shared/tools/` (entire tree) → `shared/tools/`
- `aot/Wasp.IcCdk/` → `framework/Wasp.IcCdk/`
- `aot/Wasp.AspNetCore/` → `framework/Wasp.AspNetCore/`
- `aot/Wasp.AspNetCore.Blazor.Wasp/` → `framework/Wasp.AspNetCore.Blazor.Wasp/`
- `aot/samples/BlazorWasp/IdentityService.cs` → `src/WaspCms.Canister/Services/IdentityService.cs`
- `aot/samples/BlazorWasp/AdminService.cs` → `src/WaspCms.Canister/Services/AdminService.cs`
- `aot/samples/BlazorWasp/PresenceService.cs` → `src/WaspCms.Canister/Services/PresenceService.cs`
- `aot/samples/BlazorWasp/ImageStore.cs` → `src/WaspCms.Canister/Services/MediaService.cs` (rename, extend with metadata)
- `aot/samples/BlazorWasp/blazorwasp.did` → `src/WaspCms.Canister/waspcms.did`

## Critical Files to Adapt (start from BlazorWasp, modify)

- `BlazorWasp.csproj` → `WaspCms.Canister.csproj` (paths, embedded-resource glob, project refs)
- `CrmWasm.csproj` → `WaspCms.Backoffice.csproj` (`StaticWebAssetBasePath=admin`, drop FluentUI ref)
- `BlazorWasp/build-and-deploy.sh` → `scripts/build-and-deploy.sh`
- `CrmAssets.cs` → `src/WaspCms.Canister/Endpoints/PublicAssets.cs` (rename `crm/` → `admin/`)
- `CrmWasm/Services/ApiClient.cs` → `src/WaspCms.Backoffice/Services/CmsApiClient.cs`

---

## Open Questions to Resolve Before Migration

These don't block the foundation but lock the migration scope. Capture answers before scraping AML:

1. **Languages**: confirm FR-only, FR+EN, or more.
2. **URL preservation**: keep existing AML URLs (redirect table populated from current sitemap) or accept break-and-resubmit?
3. **Booking flow**: external (link/iframe out to current provider) or in-CMS?
4. **Visual**: rebuild current AML look in Razor or redesign?

---

## Verification (end-to-end)

After Phase 1 of the roadmap:
```bash
scripts/build-docker-image.sh                    # one-time, ~5 min
scripts/build-and-deploy.sh                       # ~90s total build, ~3s deploy local
# expect: "Canister id: <local-id>"; visit http://<id>.localhost:4944 → "WaspCMS — hello"
```

After Phase 3 (first doc type end-to-end):
```bash
# 1. Sign in to /admin via II
# 2. Create a doc type "homePage" with properties: title (text), body (richtext), hero (image)
# 3. Create a content node, fill properties, publish
# 4. Visit / on the public face — see the page rendered
# 5. Re-edit, save, re-visit — see updated content (no full rebuild)
```

After Phase 7 (mainnet stability):
```bash
WASP_NETWORK=ic scripts/build-and-deploy.sh
# verify Module hash on chain matches local shasum -a 256 of the wasm
# verify Memory Size grows naturally; never resets across upgrades
# verify a sample published page renders at https://<canister>.raw.icp0.io/sample
```

After Phase 8 (production):
```bash
# DNS resolves croisieresaml.com → canister via boundary node
# All redirects from old URLs work (HTTP 301 to new content)
# Sitemap submitted to Google Search Console; coverage report clean
```
