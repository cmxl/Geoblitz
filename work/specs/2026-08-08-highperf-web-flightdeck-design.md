# HighPerf.Web "Flight Deck" — Design Spec

**Date:** 2026-08-08
**Status:** Approved (design discussion 2026-08-08; visual direction chosen from draft artifact "Flight Deck")
**Predecessor:** HighPerf geo API (complete, merged to main — see `work/specs/2026-08-07-highperf-geo-api-design.md`)

## Goal

An Angular map console for the HighPerf geo API: real OpenStreetMap tiles, click-driven geo queries, and the API's microsecond performance surfaced as a first-class UI element (permanent latency HUD). Visual direction: **Flight Deck** — dark mission-control, glass panels, phosphor-cyan query state, signal-amber results (reference mockup: draft artifact 2026-08-08, Draft 01).

**Success criteria:**

- Click → nearest, alt-drag/slider → radius search, ruler mode → two-click distance: all three work against the live API with visible results on the map and in the panel.
- Every request updates the HUD: engine compute time (from a new `Server-Timing` header), HTTP round-trip (client-measured), cache HIT/MISS via `X-Compute-Count` replay semantics, rolling sparkline (~40 samples).
- Genuine OSM raster tiles with proper attribution, dark-filtered to match the theme.
- Store/service/pipe logic covered by Vitest; API header additions covered by xUnit integration tests.

## Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Visual direction | Flight Deck (dark HUD console) | User choice from three artifact drafts |
| Interactions v1 | nearest, within (radius), distance ruler | User choice; geohash overlay dropped (YAGNI) |
| Map library | Leaflet + OSM raster tiles, dark CSS filter on tile pane | Literal OpenStreetMap tiles, no provider account/key; `invert+hue-rotate+brightness` tuned to the palette. MapLibre GL vector rejected: needs third-party style/tiles |
| Frontend home | `web/` Angular workspace in the same repo | Single repo, single story |
| Angular shape | v20+ standalone, signals, OnPush, no SSR | Local console for a local API; per global stack |
| State | One NgRx SignalStore (`GeoQueryStore`) | Per global stack; single feature |
| Unit tests | Vitest (store, API service, pipes, component smoke) | Per global stack |
| E2E | None in v1 | YAGNI; map gestures manually smoke-tested |
| API changes | CORS (dev, localhost:4200) + `Server-Timing: engine;dur=<ms>` | Minimal; both integration-tested |

## Architecture

```
web/                          # Angular workspace (app: highperf-web)
  src/app/
    core/
      geo-api.service.ts      # typed fetch wrapper for the 4 endpoints + header extraction
      geo-query.store.ts      # SignalStore: mode, params, results, timing ring, error
      models.ts               # response/DTO types mirroring the API JSON
    map/
      map-shell.component.ts  # Leaflet host: tiles, layers, gesture wiring
      layers.ts               # pin/circle/line layer builders (pure functions)
    panel/
      query-panel.component.ts# mode switch, params, results list, inline errors
    hud/
      hud-bar.component.ts    # stats strip + sparkline (SVG)
    app.component.ts          # composition: map + panel + hud + gesture toast
src/HighPerf.Api/             # + CORS policy (Development only) + Server-Timing header
```

### Data flow

Gesture (map) or form change (panel) → store action method → `GeoApiService` → API →
store updates `results`, pushes `{engineMicros, httpMillis, computeCount, cacheHit}` into the timing ring →
map layers and HUD render from store signals. Hovering a result row sets `highlightedIndex` in the store;
map pin and row both react to it (two-way highlight, one source of truth).

### The three interactions

1. **Nearest (default mode):** map click → `GET /cities/nearest?lat&lon&count` (count from panel, default 10). Cyan query pin at click point, amber pins for results, ranked list in panel.
2. **Within:** alt-drag on the map grows a cyan circle live (local geometry only); on release → `GET /cities/within?lat&lon&radiusKm&minPopulation`. Radius also settable via panel slider (then centered on last query point). `radiusKm` clamped to the API's (0, 500]; `minPopulation` field in panel.
3. **Distance ruler:** mode switch or `D` key → next two clicks set endpoints; geodesic polyline between them; `GET /distance` result rendered as a floating label at the line midpoint. Third click starts a new measurement.

### HUD semantics (honesty rules)

- **engine µs**: value of the `Server-Timing: engine;dur=` header — measured inside the API around the compute call only. On a cache HIT the replayed header carries the ORIGINAL compute time; the HUD detects HIT (unchanged `X-Compute-Count`) and displays `cached · 0 µs engine` instead of the stale number, with the original in the tooltip. This distinction is a deliberate teaching artifact.
- **http ms**: client-measured `performance.now()` around fetch — includes network + Kestrel + serialization. Never conflated with engine time; both always visible.
- **alloc 0 B**: a static claim from the benchmark suite, labeled as such in a tooltip (linking to docs/benchmarks.md numbers), not a live measurement.
- Sparkline: last 40 http-ms samples, cyan bars, amber highlight for the max in window.

## API changes (HighPerf.Api)

1. **CORS**: `AddCors` with a named dev policy allowing `http://localhost:4200`, any header/method, and `WithExposedHeaders("X-Compute-Count", "Server-Timing")`. Applied only when `app.Environment.IsDevelopment()`. No production CORS (deployment story unchanged).
2. **Server-Timing**: in each of the five geo endpoints, `Stopwatch.GetTimestamp()` around the compute/serialize section; header `Server-Timing: engine;dur=<milliseconds with 3 decimals>` set BEFORE body writing (so OutputCache stores and replays it — replay behavior asserted in tests). Allocation impact: one small header string per request, same accepted class as `X-Compute-Count`; AllocationTests ceiling untouched but re-run to confirm.
3. Integration tests: header present with plausible value on all five endpoints; replayed unchanged on cache hit; CORS preflight succeeds from the dev origin in Development environment.

## Error handling

- API 400 (ProblemDetails): `detail` shown inline in the panel under the offending field area; no toast spam.
- Network/5xx: HUD flips to red state (`link down`), map dims 20%, retry on next gesture; last good results remain visible but marked stale.
- Tile failures: Leaflet default behavior (grey tiles); attribution/link always rendered.

## Testing

- **Vitest**: `GeoQueryStore` (mode transitions, param clamping mirrors API limits, timing ring eviction at 40, highlight logic); `GeoApiService` (URL building, header extraction incl. Server-Timing parsing and HIT detection, ProblemDetails parsing) with mocked fetch; formatting pipes (km with 3 decimals, µs/ms display). Component smoke tests: hud-bar renders stats from store; query-panel emits param changes.
- **xUnit (API)**: the new header + CORS tests above, added to HighPerf.Api.Tests.
- Map gesture wiring stays thin (delegates to store within ~1 line per event) and is manually smoke-tested; everything it calls is unit-tested.

## Out of scope (YAGNI)

- Geohash grid overlay (drops with its endpoint usage; API endpoints remain)
- SSR/prerendering, auth/BFF (local tool, no sensitive data), i18n
- Deployment assets for the web app (can join the API's story later via /noobit:deploy-setup)
- E2E tests, mobile-specific layout (desktop-first; must not break at 1280×800+)

## Visual reference

The committed reference for implementers is Draft 01 "Flight Deck" in the design artifact (2026-08-08): palette (deep blue-black #0b1118/#0d141c ground, cyan #54d7ff query state, amber #ffd166 results, muted #6f8ba0 labels), glass panels (`rgba(13,21,30,.82)` + blur), mono for all data values, HUD strip composition, and the gesture toast. Fonts: bundle locally (no CDN) — a monospace face for data (e.g. JetBrains Mono, OFL) and the system UI stack for chrome.
