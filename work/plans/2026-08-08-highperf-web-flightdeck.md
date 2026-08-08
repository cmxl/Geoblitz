# HighPerf.Web Flight Deck Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An Angular "Flight Deck" map console for the HighPerf geo API — real OSM tiles, click-driven nearest/within/distance queries, and a permanent latency HUD fed by a new `Server-Timing` header.

**Architecture:** Angular 21 standalone app at `web/` (signals, OnPush, zoneless, NgRx SignalStore) with Leaflet + dark-filtered OSM raster tiles. One store (`GeoQueryStore`) owns all query state; the map and panel are thin views over it. The API gains two dev-facing features only: a CORS dev policy and per-request `Server-Timing: engine;dur=` measurement. Spec: `work/specs/2026-08-08-highperf-web-flightdeck-design.md`.

**Tech Stack:** Angular 21 (CLI, Vitest test runner), @ngrx/signals, Leaflet 1.9 + OSM raster tiles, @fontsource/jetbrains-mono, .NET 10 API (existing).

## Global Constraints

- Frontend lives in `web/` (Angular workspace, app name `highperf-web`). Node v24 is installed. All `npm`/`ng` commands run from `web/`.
- Angular: standalone components only, `input()`/`output()` functions, signals + `computed`, `ChangeDetectionStrategy.OnPush` on every component, native `@if/@for` control flow, `inject()` (no constructor injection). Zoneless (CLI default) — never reintroduce zone.js.
- State: NgRx SignalStore (`@ngrx/signals`) — no RxJS state, no plain services holding mutable state.
- No font/style/script CDNs: JetBrains Mono comes from `@fontsource/jetbrains-mono` (bundled). OSM tile requests and their attribution are the only external references.
- Palette (Flight Deck, fixed): ground `#0b1118`/`#0d141c`, panel glass `rgba(13,21,30,.82)`, line `rgba(94,200,255,.16)`, cyan (query state) `#54d7ff`, amber (results/values) `#ffd166`, muted label `#6f8ba0`, text `#dfe9f0`, error/red state `#ff5d5d`. All data values render in JetBrains Mono; UI chrome in system stack.
- API limits mirrored in the UI (clamp before sending): `count` 1..100, `radiusKm` (0, 500], `minPopulation` ≥ 0. API base URL default `http://localhost:5235`.
- HUD honesty rules (spec): engine µs from `Server-Timing` only; http ms measured client-side; cache HIT shown as `cached` (not the replayed stale µs); `alloc 0 B` labeled as a benchmark-suite claim.
- .NET side: net10.0, warnings-as-errors, xUnit v3; CORS only in Development; `Server-Timing` measures the compute call only, header set BEFORE body write so OutputCache replays it.
- All code, comments, commits in English. Commit per task on a feature branch (executor creates `feature/web-flightdeck`).
- Executor note (user's standing mode): maximum parallelism, milestone verification. Suggested batches: W1=[T1,T2] parallel; W2=[T3,T4,T5] parallel (after T2); W3=[T6]; W4=[T7,T8,T9] parallel; W5=[T10]; W6=[T11] + final whole-branch review. Milestone reviews after W2 (core logic) and W5 (working app).

## File Structure (locked in)

```
src/HighPerf.Api/ServerTiming.cs             # header helper (new)
src/HighPerf.Api/Program.cs                  # + CORS dev policy, + timing calls in 5 endpoints
tests/HighPerf.Api.Tests/ServerTimingTests.cs
tests/HighPerf.Api.Tests/CorsTests.cs
web/                                         # Angular workspace (ng new, app highperf-web)
  src/styles.css                             # Flight Deck tokens + global chrome
  src/app/core/models.ts                     # DTOs, timing types, GeoApiError
  src/app/core/geo-api.service.ts            # typed fetch wrapper + header extraction
  src/app/core/geo-api.service.spec.ts
  src/app/core/geo-query.store.ts            # SignalStore: mode/params/results/timings/highlight
  src/app/core/geo-query.store.spec.ts
  src/app/core/format.pipes.ts               # KmPipe, EngineTimePipe (+ specs in format.pipes.spec.ts)
  src/app/hud/hud-bar.component.ts           # stats strip + SVG sparkline
  src/app/hud/hud-bar.component.spec.ts
  src/app/panel/query-panel.component.ts     # mode switch, params, results, inline errors
  src/app/panel/query-panel.component.spec.ts
  src/app/map/layers.ts                      # pure Leaflet layer builders (tested)
  src/app/map/layers.spec.ts
  src/app/map/map-shell.component.ts         # Leaflet host: tiles, gestures → store (thin)
  src/app/app.component.ts                   # composition + gesture toast
docs/frontend.md                             # new doc; index/architecture updated
```

---

### Task 1: API — Server-Timing header + CORS dev policy

**Files:**
- Create: `src/HighPerf.Api/ServerTiming.cs`, `tests/HighPerf.Api.Tests/ServerTimingTests.cs`, `tests/HighPerf.Api.Tests/CorsTests.cs`
- Modify: `src/HighPerf.Api/Program.cs`

**Interfaces:**
- Produces: every geo endpoint (`/distance`, `/cities/nearest`, `/cities/within`, `/geohash/encode`, `/geohash/decode`) sets `Server-Timing: engine;dur=<ms, 3 decimals, invariant culture>` measuring ONLY the compute call (FindNearest/FindWithin/HaversineKm/Geohash). Cache replays return the original header value. In Development, CORS allows origin `http://localhost:4200` with any header/method and exposes `X-Compute-Count` + `Server-Timing`.
- Consumes: existing Program.cs endpoints (all take `HttpContext ctx`), existing `ApiFixture` (WebApplicationFactory, Development env by default).

- [ ] **Step 1: Write failing tests**

`tests/HighPerf.Api.Tests/ServerTimingTests.cs`:

```csharp
using System.Text.RegularExpressions;
using Xunit;

namespace HighPerf.Api.Tests;

[Collection("api")]
public partial class ServerTimingTests(ApiFixture fixture)
{
    [GeneratedRegex(@"^engine;dur=\d+\.\d{3}$")]
    private static partial Regex TimingPattern();

    [Theory]
    [InlineData("/distance?fromLat=52.52&fromLon=13.405&toLat=48.1374&toLon=11.5755")]
    [InlineData("/cities/nearest?lat=47.001&lon=15.001&count=3")]
    [InlineData("/cities/within?lat=47.002&lon=15.002&radiusKm=40")]
    [InlineData("/geohash/encode?lat=47.003&lon=15.003")]
    [InlineData("/geohash/decode?hash=u4pruydqqvj")]
    public async Task GeoEndpoints_EmitServerTimingHeader(string url)
    {
        using var client = fixture.CreateClient();
        var res = await client.GetAsync(url, TestContext.Current.CancellationToken);
        res.EnsureSuccessStatusCode();
        var value = Assert.Single(res.Headers.GetValues("Server-Timing"));
        Assert.Matches(TimingPattern(), value);
    }

    [Fact]
    public async Task CacheHit_ReplaysOriginalTimingValue()
    {
        using var client = fixture.CreateClient();
        const string url = "/cities/nearest?lat=46.501&lon=14.501&count=4";
        var first = await client.GetAsync(url, TestContext.Current.CancellationToken);
        var second = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(first.Headers.GetValues("Server-Timing").Single(),
                     second.Headers.GetValues("Server-Timing").Single());
        Assert.Equal(first.Headers.GetValues("X-Compute-Count").Single(),
                     second.Headers.GetValues("X-Compute-Count").Single()); // proves it was a replay
    }

    [Fact]
    public async Task Healthz_HasNoServerTiming()
    {
        using var client = fixture.CreateClient();
        var res = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);
        Assert.False(res.Headers.Contains("Server-Timing"));
    }
}
```

`tests/HighPerf.Api.Tests/CorsTests.cs`:

```csharp
using Xunit;

namespace HighPerf.Api.Tests;

[Collection("api")]
public class CorsTests(ApiFixture fixture)
{
    [Fact]
    public async Task Preflight_FromDevOrigin_IsAllowed()
    {
        using var client = fixture.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Options, "/cities/nearest?lat=1&lon=1");
        req.Headers.Add("Origin", "http://localhost:4200");
        req.Headers.Add("Access-Control-Request-Method", "GET");
        var res = await client.SendAsync(req, TestContext.Current.CancellationToken);
        Assert.True(res.StatusCode is System.Net.HttpStatusCode.NoContent or System.Net.HttpStatusCode.OK);
        Assert.Equal("http://localhost:4200", res.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task SimpleRequest_FromDevOrigin_ExposesTimingHeaders()
    {
        using var client = fixture.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/distance?fromLat=1&fromLon=1&toLat=2&toLon=2");
        req.Headers.Add("Origin", "http://localhost:4200");
        var res = await client.SendAsync(req, TestContext.Current.CancellationToken);
        res.EnsureSuccessStatusCode();
        var exposed = string.Join(",", res.Headers.GetValues("Access-Control-Expose-Headers"));
        Assert.Contains("X-Compute-Count", exposed);
        Assert.Contains("Server-Timing", exposed);
    }

    [Fact]
    public async Task ForeignOrigin_GetsNoCorsHeaders()
    {
        using var client = fixture.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        req.Headers.Add("Origin", "https://evil.example");
        var res = await client.SendAsync(req, TestContext.Current.CancellationToken);
        Assert.False(res.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
```

- [ ] **Step 2: Run tests, verify failure**

Run: `dotnet test tests/HighPerf.Api.Tests --nologo`
Expected: new tests FAIL (no Server-Timing header, no CORS headers). Pre-existing 93 stay green.

- [ ] **Step 3: Implement**

`src/HighPerf.Api/ServerTiming.cs`:

```csharp
using System.Diagnostics;
using System.Globalization;

namespace HighPerf.Api;

/// <summary>Emits Server-Timing for the engine compute section only. Must be called
/// BEFORE any body write so OutputCache stores and replays the header.</summary>
internal static class ServerTiming
{
    public static void Set(HttpContext ctx, long startTimestamp)
    {
        var ms = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        ctx.Response.Headers["Server-Timing"] =
            string.Create(CultureInfo.InvariantCulture, $"engine;dur={ms:F3}");
    }
}
```

`src/HighPerf.Api/Program.cs` changes:

1. After the existing `AddOutputCache` registration add:

```csharp
builder.Services.AddCors(o => o.AddPolicy("dev", p => p
    .WithOrigins("http://localhost:4200")
    .AllowAnyHeader()
    .AllowAnyMethod()
    .WithExposedHeaders("X-Compute-Count", "Server-Timing")));
```

2. After `app.UseExceptionHandler(...)` and BEFORE `app.UseOutputCache()`:

```csharp
if (app.Environment.IsDevelopment())
    app.UseCors("dev");
```

3. In each of the five geo endpoint handlers, bracket ONLY the compute call with a timestamp and set the header immediately after computing, before any body write. Patterns (apply the matching one in each handler, keeping everything else identical):

```csharp
// /distance — around the math call:
var start = Stopwatch.GetTimestamp();
var km = GeoMath.HaversineKm(fromLat, fromLon, toLat, toLon);
ServerTiming.Set(ctx, start);
return Results.Json(new DistanceResponse(km), AppJsonContext.Default.DistanceResponse);

// /cities/nearest — around FindNearest (header BEFORE WriteCities):
var start = Stopwatch.GetTimestamp();
var n = db.FindNearest(lat, lon, count, hits);
ServerTiming.Set(ctx, start);
CityJson.WriteCities(ctx.Response, db, hits[..n]);

// /cities/within — around FindWithin (header BEFORE WriteCities):
var start = Stopwatch.GetTimestamp();
var n = db.FindWithin(lat, lon, radiusKm, minPopulation, buffer.AsSpan(0, 1000));
ServerTiming.Set(ctx, start);
CityJson.WriteCities(ctx.Response, db, buffer.AsSpan(0, n));

// /geohash/encode — around Geohash.Encode; /geohash/decode — around Geohash.TryDecode
// (decode: take the timestamp before the TryDecode call inside the existing condition;
//  set the header only on the success path, before Results.Json).
```

Add `using System.Diagnostics;` to Program.cs. `Stopwatch.GetTimestamp()` is allocation-free; the single header string per request joins the same accepted-allocation class as `X-Compute-Count` (already documented).

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test tests/HighPerf.Api.Tests --nologo` then `dotnet test --nologo`
Expected: all green (93 + 6 new = 99 API; 89 Geo unchanged; total 188). Also re-run the allocation tripwire specifically and confirm it still passes under its 512 B ceiling: `dotnet test tests/HighPerf.Api.Tests -c Release --filter "FullyQualifiedName~AllocationTests"` — if the measured number rose, update the documented figure in AllocationTests comments + README.md + docs/benchmarks.md + docs/performance-techniques.md + benchmarks/RESULTS.md in THIS task (same rule as the M2 fix wave established).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(api): Server-Timing engine header + dev CORS for web console"
```

---

### Task 2: Angular workspace scaffold

**Files:**
- Create: `web/` (Angular CLI workspace, app `highperf-web`) + dependencies

**Interfaces:**
- Produces: `web/` workspace where `npm test` (Vitest) and `npm run build` succeed; dependencies installed: `leaflet`, `@ngrx/signals`, `@fontsource/jetbrains-mono`, dev `@types/leaflet`. Later tasks create files under `web/src/app/...`.

- [ ] **Step 1: Scaffold the workspace**

From the repo root:

```powershell
npx -y @angular/cli@latest new highperf-web --directory web --style css --ssr false --zoneless --skip-git --defaults
cd web
npm install leaflet @ngrx/signals @fontsource/jetbrains-mono
npm install -D @types/leaflet
```

Notes: Angular 21 CLI scaffolds Vitest as the unit-test runner by default. If the CLI prompts despite `--defaults`, answer: no SSR, CSS, AI tooling none. If `--zoneless` is not a recognized flag on the installed CLI version, omit it and verify the generated `app.config.ts` uses `provideZonelessChangeDetection()` (v21 default); if it doesn't, add it and remove any zone.js polyfill from `angular.json`.

- [ ] **Step 2: Verify test runner and build**

```powershell
npm test -- --watch=false
npm run build
```

Expected: the scaffolded app spec passes under Vitest; production build succeeds. If `npm test` opens watch mode regardless, use `ng test --watch=false`. Record the exact Angular/CLI versions in the commit body.

- [ ] **Step 3: Delete scaffold noise**

Remove the placeholder content from `web/src/app/app.component.ts` template (keep the component shell compiling: template `<p>highperf-web</p>` for now) and delete `app.component.html`/`.css` if generated separately (fold into inline template/styles; update the component decorator accordingly). Keep the generated `app.spec.ts` minimal and passing (assert the component creates).

- [ ] **Step 4: Verify clean state**

```powershell
npm test -- --watch=false && npm run build
```

Expected: green.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "chore(web): scaffold Angular 21 workspace with Vitest, Leaflet, SignalStore deps"
```

---

### Task 3: Models + GeoApiService

**Files:**
- Create: `web/src/app/core/models.ts`, `web/src/app/core/geo-api.service.ts`
- Test: `web/src/app/core/geo-api.service.spec.ts`

**Interfaces:**
- Produces (exact shapes later tasks rely on):

```typescript
// models.ts
export interface CityHit { name: string; country: string; population: number; lat: number; lon: number; distanceKm: number; }
export interface CitiesResponse { count: number; cities: CityHit[]; }
export interface DistanceResponse { kilometers: number; }
export interface ApiProblem { title: string; status: number; detail: string; }
export class GeoApiError extends Error { readonly problem: ApiProblem; constructor(problem: ApiProblem) { super(problem.detail); this.problem = problem; } }
export interface ApiResult<T> { data: T; engineMicros: number | null; computeCount: number | null; httpMillis: number; }
export type QueryMode = 'nearest' | 'within' | 'distance';
export interface RequestTiming { engineMicros: number | null; httpMillis: number; computeCount: number | null; cacheHit: boolean; at: number; }
export const GEO_API_BASE_URL = new InjectionToken<string>('GEO_API_BASE_URL', { factory: () => 'http://localhost:5235' });

// geo-api.service.ts — @Injectable({providedIn:'root'}) class GeoApiService
nearest(lat: number, lon: number, count: number): Promise<ApiResult<CitiesResponse>>
within(lat: number, lon: number, radiusKm: number, minPopulation: number): Promise<ApiResult<CitiesResponse>>
distance(fromLat: number, fromLon: number, toLat: number, toLon: number): Promise<ApiResult<DistanceResponse>>
```

- Behavior: builds `?key=value` URLs with `encodeURIComponent` and invariant number formatting (`String(n)`), measures `performance.now()` around `fetch`, parses `Server-Timing: engine;dur=<ms>` into MICROSECONDS (`dur * 1000`, null when header absent/unparseable), reads `X-Compute-Count` as number (null when absent), throws `GeoApiError` on non-2xx with `application/problem+json` body, throws plain `Error('geo-api: HTTP <status>')` for non-problem failures.

- [ ] **Step 1: Write failing tests**

`web/src/app/core/geo-api.service.spec.ts`:

```typescript
import { TestBed } from '@angular/core/testing';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { GeoApiService } from './geo-api.service';
import { GeoApiError } from './models';

function jsonResponse(body: unknown, headers: Record<string, string> = {}, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': status >= 400 ? 'application/problem+json' : 'application/json', ...headers },
  });
}

describe('GeoApiService', () => {
  let service: GeoApiService;
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    service = TestBed.inject(GeoApiService);
  });
  afterEach(() => vi.unstubAllGlobals());

  it('builds the nearest URL with all params', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ count: 0, cities: [] }));
    await service.nearest(48.1374, 11.5755, 10);
    expect(fetchMock).toHaveBeenCalledWith(
      'http://localhost:5235/cities/nearest?lat=48.1374&lon=11.5755&count=10');
  });

  it('builds the within URL including minPopulation', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ count: 0, cities: [] }));
    await service.within(48, 11, 25, 100000);
    expect(fetchMock).toHaveBeenCalledWith(
      'http://localhost:5235/cities/within?lat=48&lon=11&radiusKm=25&minPopulation=100000');
  });

  it('parses Server-Timing into microseconds and X-Compute-Count into a number', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ count: 0, cities: [] },
      { 'Server-Timing': 'engine;dur=0.009', 'X-Compute-Count': '42' }));
    const r = await service.nearest(1, 2, 3);
    expect(r.engineMicros).toBeCloseTo(9, 5);
    expect(r.computeCount).toBe(42);
    expect(r.httpMillis).toBeGreaterThanOrEqual(0);
  });

  it('returns nulls when timing headers are absent', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ kilometers: 504.2 }));
    const r = await service.distance(52.52, 13.405, 48.1374, 11.5755);
    expect(r.engineMicros).toBeNull();
    expect(r.computeCount).toBeNull();
    expect(r.data.kilometers).toBeCloseTo(504.2, 5);
  });

  it('throws GeoApiError with problem details on 400', async () => {
    fetchMock.mockResolvedValue(jsonResponse(
      { title: 'Invalid request', status: 400, detail: 'count must be an integer in [1, 100]' }, {}, 400));
    await expect(service.nearest(1, 2, 999)).rejects.toSatisfy((e: unknown) =>
      e instanceof GeoApiError && e.problem.status === 400 && e.problem.detail.includes('count'));
  });

  it('throws a plain error for non-problem failures', async () => {
    fetchMock.mockResolvedValue(new Response('boom', { status: 502 }));
    await expect(service.nearest(1, 2, 3)).rejects.toThrow('geo-api: HTTP 502');
  });
});
```

- [ ] **Step 2: Run tests, verify failure** — `npm test -- --watch=false` in `web/` → FAIL (module not found).

- [ ] **Step 3: Implement**

`web/src/app/core/models.ts`: exactly the shapes from Interfaces above (single file, `InjectionToken` imported from `@angular/core`).

`web/src/app/core/geo-api.service.ts`:

```typescript
import { Injectable, inject } from '@angular/core';
import {
  ApiResult, CitiesResponse, DistanceResponse, GEO_API_BASE_URL, GeoApiError,
} from './models';

@Injectable({ providedIn: 'root' })
export class GeoApiService {
  private readonly baseUrl = inject(GEO_API_BASE_URL);

  nearest(lat: number, lon: number, count: number): Promise<ApiResult<CitiesResponse>> {
    return this.get<CitiesResponse>('/cities/nearest', { lat, lon, count });
  }

  within(lat: number, lon: number, radiusKm: number, minPopulation: number): Promise<ApiResult<CitiesResponse>> {
    return this.get<CitiesResponse>('/cities/within', { lat, lon, radiusKm, minPopulation });
  }

  distance(fromLat: number, fromLon: number, toLat: number, toLon: number): Promise<ApiResult<DistanceResponse>> {
    return this.get<DistanceResponse>('/distance', { fromLat, fromLon, toLat, toLon });
  }

  private async get<T>(path: string, params: Record<string, number>): Promise<ApiResult<T>> {
    const query = Object.entries(params)
      .map(([k, v]) => `${k}=${encodeURIComponent(String(v))}`)
      .join('&');
    const started = performance.now();
    const res = await fetch(`${this.baseUrl}${path}?${query}`);
    const httpMillis = performance.now() - started;

    if (!res.ok) {
      if (res.headers.get('content-type')?.includes('application/problem+json')) {
        throw new GeoApiError(await res.json());
      }
      throw new Error(`geo-api: HTTP ${res.status}`);
    }

    return {
      data: (await res.json()) as T,
      engineMicros: parseEngineMicros(res.headers.get('Server-Timing')),
      computeCount: parseCount(res.headers.get('X-Compute-Count')),
      httpMillis,
    };
  }
}

function parseEngineMicros(header: string | null): number | null {
  const match = header?.match(/engine;dur=([0-9.]+)/);
  return match ? Number(match[1]) * 1000 : null;
}

function parseCount(header: string | null): number | null {
  if (header === null) return null;
  const n = Number(header);
  return Number.isFinite(n) ? n : null;
}
```

- [ ] **Step 4: Run tests, verify pass** — `npm test -- --watch=false` → all green.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(web): typed geo API client with Server-Timing extraction"
```

---

### Task 4: GeoQueryStore

**Files:**
- Create: `web/src/app/core/geo-query.store.ts`
- Test: `web/src/app/core/geo-query.store.spec.ts`

**Interfaces:**
- Consumes: `GeoApiService` (Task 3 signatures), models.
- Produces (SignalStore, `providedIn: 'root'` via `{ providedIn: 'root' }` option):

```typescript
export const GeoQueryStore = signalStore({ providedIn: 'root' },
  withState<GeoQueryState>(...), withComputed(...), withMethods(...));

interface GeoQueryState {
  mode: QueryMode;                       // 'nearest' initial
  count: number;                         // 10
  radiusKm: number;                      // 50
  minPopulation: number;                 // 0
  queryPoint: { lat: number; lon: number } | null;
  rulerPoints: { lat: number; lon: number }[];   // max 2
  results: CityHit[];
  distanceKm: number | null;
  timings: RequestTiming[];              // ring, newest LAST, max 40
  maxComputeCount: number;               // for cache-HIT detection
  error: string | null;
  busy: boolean;
  linkDown: boolean;
  highlightedIndex: number | null;
}
// computed: lastTiming (RequestTiming | null)
// methods: setMode(m), setCount(n), setRadiusKm(n), setMinPopulation(n), highlight(i: number | null),
//          queryNearest(lat, lon): Promise<void>, queryWithin(lat, lon, radiusKm?): Promise<void>,
//          addRulerPoint(lat, lon): Promise<void>, clearError()
```

- Behavior rules: setters clamp (count → integer 1..100; radiusKm → 0.1..500; minPopulation → integer ≥ 0). `setMode` clears `results`, `distanceKm`, `rulerPoints`, `error`, `highlightedIndex` (fresh slate per mode). Query methods: set `busy` true + `queryPoint`; on success write results + push timing + `linkDown` false; on `GeoApiError` set `error` to `problem.detail` (results untouched); on any other error set `linkDown` true (results untouched, marked stale by virtue of linkDown); always clear `busy`. Timing push: `cacheHit = computeCount !== null && computeCount <= maxComputeCount`; update `maxComputeCount = max(...)` when not a hit; evict oldest beyond 40. `addRulerPoint`: first click stores point; second click stores + calls `distance` + writes `distanceKm`; third click resets to the new single point.

- [ ] **Step 1: Write failing tests**

`web/src/app/core/geo-query.store.spec.ts`:

```typescript
import { TestBed } from '@angular/core/testing';
import { describe, it, expect, beforeEach, vi } from 'vitest';
import { GeoQueryStore } from './geo-query.store';
import { GeoApiService } from './geo-api.service';
import { ApiResult, CitiesResponse, GeoApiError } from './models';

function citiesResult(n: number, computeCount: number | null): ApiResult<CitiesResponse> {
  const cities = Array.from({ length: n }, (_, i) => ({
    name: `C${i}`, country: 'DE', population: 1000, lat: 48 + i, lon: 11, distanceKm: i,
  }));
  return { data: { count: n, cities }, engineMicros: 5, computeCount, httpMillis: 1.5 };
}

describe('GeoQueryStore', () => {
  let api: { nearest: ReturnType<typeof vi.fn>; within: ReturnType<typeof vi.fn>; distance: ReturnType<typeof vi.fn> };
  let store: InstanceType<typeof GeoQueryStore>;

  beforeEach(() => {
    api = { nearest: vi.fn(), within: vi.fn(), distance: vi.fn() };
    TestBed.configureTestingModule({ providers: [{ provide: GeoApiService, useValue: api }] });
    store = TestBed.inject(GeoQueryStore);
  });

  it('clamps parameters to API limits', () => {
    store.setCount(999); expect(store.count()).toBe(100);
    store.setCount(0); expect(store.count()).toBe(1);
    store.setCount(7.9); expect(store.count()).toBe(7);
    store.setRadiusKm(9999); expect(store.radiusKm()).toBe(500);
    store.setRadiusKm(-3); expect(store.radiusKm()).toBe(0.1);
    store.setMinPopulation(-5); expect(store.minPopulation()).toBe(0);
  });

  it('queryNearest stores results and the query point', async () => {
    api.nearest.mockResolvedValue(citiesResult(3, 1));
    await store.queryNearest(48.1, 11.5);
    expect(api.nearest).toHaveBeenCalledWith(48.1, 11.5, 10);
    expect(store.results().length).toBe(3);
    expect(store.queryPoint()).toEqual({ lat: 48.1, lon: 11.5 });
    expect(store.busy()).toBe(false);
  });

  it('detects cache hits via non-increasing compute count', async () => {
    api.nearest.mockResolvedValueOnce(citiesResult(1, 5));
    await store.queryNearest(1, 1);
    expect(store.lastTiming()!.cacheHit).toBe(false);
    api.nearest.mockResolvedValueOnce(citiesResult(1, 6));
    await store.queryNearest(2, 2);
    expect(store.lastTiming()!.cacheHit).toBe(false);
    api.nearest.mockResolvedValueOnce(citiesResult(1, 5)); // replayed older count
    await store.queryNearest(1, 1);
    expect(store.lastTiming()!.cacheHit).toBe(true);
  });

  it('keeps at most 40 timings, evicting the oldest', async () => {
    for (let i = 1; i <= 45; i++) {
      api.nearest.mockResolvedValueOnce(citiesResult(0, i));
      await store.queryNearest(i, i);
    }
    expect(store.timings().length).toBe(40);
    expect(store.timings()[0].computeCount).toBe(6);
  });

  it('surfaces problem details as inline error and keeps results', async () => {
    api.nearest.mockResolvedValueOnce(citiesResult(2, 1));
    await store.queryNearest(1, 1);
    api.nearest.mockRejectedValueOnce(new GeoApiError({ title: 'Invalid request', status: 400, detail: 'radiusKm is required' }));
    await store.queryNearest(2, 2);
    expect(store.error()).toBe('radiusKm is required');
    expect(store.results().length).toBe(2);
    expect(store.linkDown()).toBe(false);
  });

  it('flips linkDown on network errors', async () => {
    api.nearest.mockRejectedValueOnce(new TypeError('fetch failed'));
    await store.queryNearest(1, 1);
    expect(store.linkDown()).toBe(true);
    api.nearest.mockResolvedValueOnce(citiesResult(1, 9));
    await store.queryNearest(1, 1);
    expect(store.linkDown()).toBe(false);
  });

  it('ruler: two points trigger distance, third restarts', async () => {
    api.distance.mockResolvedValue({ data: { kilometers: 504.2 }, engineMicros: 1, computeCount: 2, httpMillis: 1 });
    store.setMode('distance');
    await store.addRulerPoint(52.52, 13.405);
    expect(store.distanceKm()).toBeNull();
    await store.addRulerPoint(48.1374, 11.5755);
    expect(store.distanceKm()).toBeCloseTo(504.2, 5);
    await store.addRulerPoint(50, 10);
    expect(store.rulerPoints().length).toBe(1);
    expect(store.distanceKm()).toBeNull();
  });

  it('setMode clears transient state', async () => {
    api.nearest.mockResolvedValue(citiesResult(2, 1));
    await store.queryNearest(1, 1);
    store.highlight(1);
    store.setMode('within');
    expect(store.results().length).toBe(0);
    expect(store.highlightedIndex()).toBeNull();
    expect(store.error()).toBeNull();
  });
});
```

- [ ] **Step 2: Run tests, verify failure** — module not found.

- [ ] **Step 3: Implement `web/src/app/core/geo-query.store.ts`**

```typescript
import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { GeoApiService } from './geo-api.service';
import { ApiResult, CityHit, GeoApiError, QueryMode, RequestTiming } from './models';

interface GeoQueryState {
  mode: QueryMode;
  count: number;
  radiusKm: number;
  minPopulation: number;
  queryPoint: { lat: number; lon: number } | null;
  rulerPoints: { lat: number; lon: number }[];
  results: CityHit[];
  distanceKm: number | null;
  timings: RequestTiming[];
  maxComputeCount: number;
  error: string | null;
  busy: boolean;
  linkDown: boolean;
  highlightedIndex: number | null;
}

const initialState: GeoQueryState = {
  mode: 'nearest', count: 10, radiusKm: 50, minPopulation: 0,
  queryPoint: null, rulerPoints: [], results: [], distanceKm: null,
  timings: [], maxComputeCount: 0, error: null, busy: false, linkDown: false,
  highlightedIndex: null,
};

const MAX_TIMINGS = 40;

export const GeoQueryStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withComputed(({ timings }) => ({
    lastTiming: computed<RequestTiming | null>(() => timings().at(-1) ?? null),
  })),
  withMethods((store, api = inject(GeoApiService)) => {

    function pushTiming(r: ApiResult<unknown>): void {
      const cacheHit = r.computeCount !== null && r.computeCount <= store.maxComputeCount();
      const timing: RequestTiming = {
        engineMicros: r.engineMicros, httpMillis: r.httpMillis,
        computeCount: r.computeCount, cacheHit, at: Date.now(),
      };
      patchState(store, {
        timings: [...store.timings(), timing].slice(-MAX_TIMINGS),
        maxComputeCount: cacheHit ? store.maxComputeCount() : Math.max(store.maxComputeCount(), r.computeCount ?? 0),
      });
    }

    async function run<T>(call: () => Promise<ApiResult<T>>, apply: (r: ApiResult<T>) => void): Promise<void> {
      patchState(store, { busy: true, error: null });
      try {
        const r = await call();
        apply(r);
        pushTiming(r);
        patchState(store, { linkDown: false });
      } catch (e) {
        if (e instanceof GeoApiError) patchState(store, { error: e.problem.detail });
        else patchState(store, { linkDown: true });
      } finally {
        patchState(store, { busy: false });
      }
    }

    return {
      setMode(mode: QueryMode): void {
        patchState(store, {
          mode, results: [], distanceKm: null, rulerPoints: [],
          error: null, highlightedIndex: null,
        });
      },
      setCount(n: number): void {
        patchState(store, { count: Math.min(100, Math.max(1, Math.trunc(n))) });
      },
      setRadiusKm(n: number): void {
        patchState(store, { radiusKm: Math.min(500, Math.max(0.1, n)) });
      },
      setMinPopulation(n: number): void {
        patchState(store, { minPopulation: Math.max(0, Math.trunc(n)) });
      },
      highlight(index: number | null): void {
        patchState(store, { highlightedIndex: index });
      },
      clearError(): void {
        patchState(store, { error: null });
      },
      queryNearest(lat: number, lon: number): Promise<void> {
        patchState(store, { queryPoint: { lat, lon } });
        return run(() => api.nearest(lat, lon, store.count()),
          r => patchState(store, { results: r.data.cities, highlightedIndex: null }));
      },
      queryWithin(lat: number, lon: number, radiusKm?: number): Promise<void> {
        if (radiusKm !== undefined) patchState(store, { radiusKm: Math.min(500, Math.max(0.1, radiusKm)) });
        patchState(store, { queryPoint: { lat, lon } });
        return run(() => api.within(lat, lon, store.radiusKm(), store.minPopulation()),
          r => patchState(store, { results: r.data.cities, highlightedIndex: null }));
      },
      async addRulerPoint(lat: number, lon: number): Promise<void> {
        const points = store.rulerPoints();
        if (points.length >= 2) {
          patchState(store, { rulerPoints: [{ lat, lon }], distanceKm: null });
          return;
        }
        const next = [...points, { lat, lon }];
        patchState(store, { rulerPoints: next });
        if (next.length === 2) {
          await run(() => api.distance(next[0].lat, next[0].lon, next[1].lat, next[1].lon),
            r => patchState(store, { distanceKm: r.data.kilometers }));
        }
      },
    };
  }),
);
```

- [ ] **Step 4: Run tests, verify pass** — `npm test -- --watch=false` → all green.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(web): GeoQueryStore — modes, clamped params, timing ring, cache-hit detection"
```

---

### Task 5: Format pipes

**Files:**
- Create: `web/src/app/core/format.pipes.ts`
- Test: `web/src/app/core/format.pipes.spec.ts`

**Interfaces:**
- Produces: `KmPipe` (name `km`): number → `"0.612 km"` (3 decimals, `'—'` for null/undefined). `EngineTimePipe` (name `engineTime`): microseconds → `"9.1 µs"` (1 decimal) below 1000, `"1.24 ms"` (2 decimals) at/above 1000, `'—'` for null. Both `standalone`, pure.

- [ ] **Step 1: Write failing tests**

`web/src/app/core/format.pipes.spec.ts`:

```typescript
import { describe, it, expect } from 'vitest';
import { KmPipe, EngineTimePipe } from './format.pipes';

describe('format pipes', () => {
  const km = new KmPipe();
  const et = new EngineTimePipe();

  it('formats kilometers to 3 decimals', () => {
    expect(km.transform(0.6123)).toBe('0.612 km');
    expect(km.transform(504.2)).toBe('504.200 km');
    expect(km.transform(null)).toBe('—');
  });

  it('formats engine time in µs below 1000, ms above', () => {
    expect(et.transform(9.1)).toBe('9.1 µs');
    expect(et.transform(999.94)).toBe('999.9 µs');
    expect(et.transform(1240)).toBe('1.24 ms');
    expect(et.transform(null)).toBe('—');
  });
});
```

- [ ] **Step 2: Run, verify failure.**

- [ ] **Step 3: Implement `web/src/app/core/format.pipes.ts`**

```typescript
import { Pipe, PipeTransform } from '@angular/core';

@Pipe({ name: 'km' })
export class KmPipe implements PipeTransform {
  transform(value: number | null | undefined): string {
    return value == null ? '—' : `${value.toFixed(3)} km`;
  }
}

@Pipe({ name: 'engineTime' })
export class EngineTimePipe implements PipeTransform {
  transform(micros: number | null | undefined): string {
    if (micros == null) return '—';
    return micros < 1000 ? `${micros.toFixed(1)} µs` : `${(micros / 1000).toFixed(2)} ms`;
  }
}
```

- [ ] **Step 4: Run tests, verify pass.**

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(web): km and engine-time formatting pipes"
```

---

### Task 6: Flight Deck theme + app shell composition

**Files:**
- Modify: `web/src/styles.css`, `web/src/app/app.component.ts`, `web/src/app/app.spec.ts` (whatever the CLI named the root component spec)

**Interfaces:**
- Produces: global CSS custom properties every later component uses: `--fd-ground:#0b1118; --fd-map:#0d141c; --fd-glass:rgba(13,21,30,.82); --fd-line:rgba(94,200,255,.16); --fd-cyan:#54d7ff; --fd-amber:#ffd166; --fd-muted:#6f8ba0; --fd-text:#dfe9f0; --fd-red:#ff5d5d; --fd-mono:'JetBrains Mono',Consolas,monospace;` — plus the app shell grid with three named slots later tasks fill: `<app-hud-bar/>` (top, 44px), `<app-query-panel/>` (left overlay, 296px), `<app-map-shell/>` (full-bleed underneath). Until Tasks 7-9 land, the shell renders placeholder `<div>`s with the same element selectors commented in.
- Single-theme by design: Flight Deck commits to dark (spec); paint every color explicitly from the tokens, no `prefers-color-scheme` handling.

- [ ] **Step 1: Write the theme `web/src/styles.css`**

```css
@import '@fontsource/jetbrains-mono/400.css';
@import '@fontsource/jetbrains-mono/600.css';
@import 'leaflet/dist/leaflet.css';

:root {
  --fd-ground: #0b1118;
  --fd-map: #0d141c;
  --fd-glass: rgba(13, 21, 30, .82);
  --fd-line: rgba(94, 200, 255, .16);
  --fd-cyan: #54d7ff;
  --fd-amber: #ffd166;
  --fd-muted: #6f8ba0;
  --fd-text: #dfe9f0;
  --fd-red: #ff5d5d;
  --fd-mono: 'JetBrains Mono', Consolas, monospace;
}

* { box-sizing: border-box; }
html, body { height: 100%; }
body {
  margin: 0;
  background: var(--fd-ground);
  color: var(--fd-text);
  font: 14px/1.5 'Segoe UI', system-ui, sans-serif;
  overflow: hidden;
}

/* dark-filter the OSM raster tiles into the Flight Deck palette */
.leaflet-tile-pane { filter: invert(1) hue-rotate(200deg) saturate(.55) brightness(.72) contrast(.9); }
.leaflet-container { background: var(--fd-map); font: inherit; }
.leaflet-control-attribution {
  background: var(--fd-glass) !important;
  color: var(--fd-muted) !important;
  font: 10px/1.6 var(--fd-mono) !important;
}
.leaflet-control-attribution a { color: var(--fd-cyan) !important; }

.fd-card {
  background: var(--fd-glass);
  backdrop-filter: blur(8px);
  border: 1px solid var(--fd-line);
  border-radius: 10px;
}
.fd-label {
  font: 600 11px/1 var(--fd-mono);
  letter-spacing: .16em;
  text-transform: uppercase;
  color: var(--fd-muted);
}
:focus-visible { outline: 2px solid var(--fd-cyan); outline-offset: 2px; }
@media (prefers-reduced-motion: reduce) { * { animation: none !important; transition: none !important; } }
```

- [ ] **Step 2: Compose the shell in `web/src/app/app.component.ts`**

```typescript
import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="shell">
      <div class="slot-map"><!-- <app-map-shell/> Task 9 --></div>
      <div class="slot-hud"><!-- <app-hud-bar/> Task 7 --></div>
      <aside class="slot-panel"><!-- <app-query-panel/> Task 8 --></aside>
      <div class="toast fd-card">
        click → nearest · <b>alt-drag</b> → radius · <b>D</b> + two clicks → distance
      </div>
    </div>
  `,
  styles: `
    .shell { position: fixed; inset: 0; }
    .slot-map { position: absolute; inset: 0; }
    .slot-hud { position: absolute; top: 0; left: 0; right: 0; height: 44px; z-index: 1000; }
    .slot-panel { position: absolute; top: 56px; left: 14px; bottom: 14px; width: 296px; z-index: 1000; display: flex; flex-direction: column; gap: 12px; }
    .toast { position: absolute; right: 14px; bottom: 14px; z-index: 1000; padding: 10px 14px; font: 11px/1.6 var(--fd-mono); color: var(--fd-muted); }
    .toast b { color: var(--fd-cyan); font-weight: 600; }
  `,
})
export class AppComponent {}
```

(If the CLI generated the root component under a different class name like `App`, keep the generated name and file; apply this template/styles to it.)

- [ ] **Step 3: Update the root spec** to assert the shell renders the toast text (replace the scaffold assertion):

```typescript
import { TestBed } from '@angular/core/testing';
import { describe, it, expect } from 'vitest';
import { AppComponent } from './app.component';

describe('AppComponent', () => {
  it('renders the shell with the gesture toast', async () => {
    await TestBed.configureTestingModule({ imports: [AppComponent] }).compileComponents();
    const fixture = TestBed.createComponent(AppComponent);
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('nearest');
  });
});
```

- [ ] **Step 4: Verify** — `npm test -- --watch=false && npm run build` → green. Run `npm start` briefly and eyeball: dark ground, toast bottom-right. Stop it.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(web): Flight Deck theme tokens, dark tile filter, app shell layout"
```

---

### Task 7: HUD bar component

**Files:**
- Create: `web/src/app/hud/hud-bar.component.ts`
- Test: `web/src/app/hud/hud-bar.component.spec.ts`

**Interfaces:**
- Consumes: `GeoQueryStore` (`lastTiming`, `timings`, `busy`, `linkDown` signals), `EngineTimePipe`.
- Produces: `<app-hud-bar/>` (selector `app-hud-bar`). Displays: brand, engine time (or `cached` when `lastTiming().cacheHit`), http ms (1 decimal), `alloc 0 B*` with `title` tooltip "benchmark-suite claim — see docs/benchmarks.md", cache state `HIT/MISS · #<computeCount>`, link-down state, SVG sparkline of `timings()` httpMillis (max 40 bars, amber max-bar highlight).

- [ ] **Step 1: Write failing tests**

`web/src/app/hud/hud-bar.component.spec.ts`:

```typescript
import { TestBed } from '@angular/core/testing';
import { describe, it, expect, beforeEach, vi } from 'vitest';
import { HudBarComponent } from './hud-bar.component';
import { GeoQueryStore } from '../core/geo-query.store';
import { GeoApiService } from '../core/geo-api.service';

describe('HudBarComponent', () => {
  let api: { nearest: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    api = { nearest: vi.fn() };
    TestBed.configureTestingModule({ providers: [{ provide: GeoApiService, useValue: api }] });
  });

  function render(): HTMLElement {
    const fixture = TestBed.createComponent(HudBarComponent);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows idle placeholders before any request', () => {
    const el = render();
    expect(el.textContent).toContain('engine —');
    expect(el.textContent).toContain('alloc 0 B');
  });

  it('shows engine time and MISS after a computed request', async () => {
    api.nearest.mockResolvedValue({ data: { count: 0, cities: [] }, engineMicros: 9.1, computeCount: 7, httpMillis: 1.53 });
    const store = TestBed.inject(GeoQueryStore);
    await store.queryNearest(1, 1);
    const el = render();
    expect(el.textContent).toContain('9.1 µs');
    expect(el.textContent).toContain('MISS · #7');
    expect(el.querySelectorAll('rect.bar').length).toBe(1);
  });

  it('labels cache hits as cached instead of the stale engine time', async () => {
    api.nearest
      .mockResolvedValueOnce({ data: { count: 0, cities: [] }, engineMicros: 9.1, computeCount: 7, httpMillis: 1.5 })
      .mockResolvedValueOnce({ data: { count: 0, cities: [] }, engineMicros: 9.1, computeCount: 7, httpMillis: 0.4 });
    const store = TestBed.inject(GeoQueryStore);
    await store.queryNearest(1, 1);
    await store.queryNearest(1, 1);
    const el = render();
    expect(el.textContent).toContain('cached');
    expect(el.textContent).toContain('HIT · #7');
  });

  it('shows link-down state', async () => {
    api.nearest.mockRejectedValue(new TypeError('fetch failed'));
    const store = TestBed.inject(GeoQueryStore);
    await store.queryNearest(1, 1);
    const el = render();
    expect(el.textContent).toContain('LINK DOWN');
  });
});
```

- [ ] **Step 2: Run, verify failure.**

- [ ] **Step 3: Implement `web/src/app/hud/hud-bar.component.ts`**

```typescript
import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { GeoQueryStore } from '../core/geo-query.store';
import { EngineTimePipe } from '../core/format.pipes';

@Component({
  selector: 'app-hud-bar',
  imports: [EngineTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="hud">
      <span class="brand">HIGH<em>PERF</em>/GEO</span>
      @if (store.linkDown()) {
        <span class="stat down">LINK DOWN</span>
      } @else if (store.lastTiming(); as t) {
        <span class="stat cyan">engine <b>@if (t.cacheHit) { cached } @else { {{ t.engineMicros | engineTime }} }</b></span>
        <span class="stat">http <b>{{ t.httpMillis.toFixed(1) }} ms</b></span>
        <span class="stat" title="benchmark-suite claim — see docs/benchmarks.md">alloc <b>0 B*</b></span>
        <span class="stat cyan">cache <b>{{ t.cacheHit ? 'HIT' : 'MISS' }} · #{{ t.computeCount ?? '—' }}</b></span>
      } @else {
        <span class="stat">engine —</span>
        <span class="stat" title="benchmark-suite claim — see docs/benchmarks.md">alloc <b>0 B*</b></span>
      }
      <svg class="spark" [attr.viewBox]="'0 0 ' + 40 * 5 + ' 16'" preserveAspectRatio="none" aria-hidden="true">
        @for (bar of bars(); track bar.i) {
          <rect class="bar" [class.max]="bar.isMax"
                [attr.x]="bar.i * 5" [attr.y]="16 - bar.h" width="3" [attr.height]="bar.h" />
        }
      </svg>
    </div>
  `,
  styles: `
    .hud { height: 44px; display: flex; align-items: center; gap: 22px; padding: 0 18px;
           background: var(--fd-glass); backdrop-filter: blur(6px);
           border-bottom: 1px solid var(--fd-line);
           font: 12px/1 var(--fd-mono); color: var(--fd-muted); }
    .brand { color: var(--fd-text); font-weight: 600; letter-spacing: .1em; }
    .brand em { color: var(--fd-cyan); font-style: normal; }
    .stat b { color: var(--fd-amber); font-weight: 600; }
    .stat.cyan b { color: var(--fd-cyan); }
    .stat.down { color: var(--fd-red); font-weight: 600; }
    .spark { margin-left: auto; height: 16px; width: 200px; }
    .bar { fill: #2d5d78; }
    .bar.max { fill: var(--fd-amber); }
  `,
})
export class HudBarComponent {
  protected readonly store = inject(GeoQueryStore);

  protected readonly bars = computed(() => {
    const timings = this.store.timings();
    const max = Math.max(1, ...timings.map(t => t.httpMillis));
    return timings.map((t, i) => ({
      i,
      h: Math.max(2, Math.round((t.httpMillis / max) * 16)),
      isMax: t.httpMillis === max && timings.length > 1,
    }));
  });
}
```

- [ ] **Step 4: Run tests, verify pass.**

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(web): HUD bar — honest engine/http/cache stats + sparkline"
```

---

### Task 8: Query panel component

**Files:**
- Create: `web/src/app/panel/query-panel.component.ts`
- Test: `web/src/app/panel/query-panel.component.spec.ts`

**Interfaces:**
- Consumes: `GeoQueryStore` (mode/count/radiusKm/minPopulation/results/error/busy/highlightedIndex/queryPoint/distanceKm + setters/highlight), `KmPipe`.
- Produces: `<app-query-panel/>`. Mode switch (3 buttons NEAREST/WITHIN/DIST → `setMode`), parameter fields per mode (nearest: count; within: radiusKm slider 1..500 + minPopulation input; distance: hint text "click two points"), inline error line (from `store.error()`), results list rows (rank, name, country chip, `distanceKm | km`) with mouseenter/mouseleave → `highlight(i)`/`highlight(null)` and `.hot` class when `highlightedIndex() === i`, distance mode shows `distanceKm | km` as the single result.

- [ ] **Step 1: Write failing tests**

`web/src/app/panel/query-panel.component.spec.ts`:

```typescript
import { TestBed } from '@angular/core/testing';
import { describe, it, expect, beforeEach, vi } from 'vitest';
import { QueryPanelComponent } from './query-panel.component';
import { GeoQueryStore } from '../core/geo-query.store';
import { GeoApiService } from '../core/geo-api.service';

describe('QueryPanelComponent', () => {
  let api: { nearest: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    api = { nearest: vi.fn() };
    TestBed.configureTestingModule({ providers: [{ provide: GeoApiService, useValue: api }] });
  });

  function render() {
    const fixture = TestBed.createComponent(QueryPanelComponent);
    fixture.detectChanges();
    return fixture;
  }

  it('switches store mode from the mode buttons', () => {
    const fixture = render();
    const store = TestBed.inject(GeoQueryStore);
    const buttons = fixture.nativeElement.querySelectorAll('button.mode');
    expect(buttons.length).toBe(3);
    (buttons[1] as HTMLButtonElement).click();
    expect(store.mode()).toBe('within');
  });

  it('renders results with rank, country and distance', async () => {
    api.nearest.mockResolvedValue({
      data: { count: 2, cities: [
        { name: 'München', country: 'DE', population: 1471508, lat: 48.13, lon: 11.57, distanceKm: 0.612 },
        { name: 'Pasing', country: 'DE', population: 40000, lat: 48.14, lon: 11.46, distanceKm: 7.918 },
      ] },
      engineMicros: 5, computeCount: 1, httpMillis: 1,
    });
    await TestBed.inject(GeoQueryStore).queryNearest(48.137, 11.576);
    const el = render().nativeElement as HTMLElement;
    expect(el.textContent).toContain('München');
    expect(el.textContent).toContain('0.612 km');
    expect(el.querySelectorAll('.row').length).toBe(2);
  });

  it('shows inline problem-details errors', async () => {
    const store = TestBed.inject(GeoQueryStore);
    const { GeoApiError } = await import('../core/models');
    api.nearest.mockRejectedValue(new GeoApiError({ title: 'Invalid request', status: 400, detail: 'count must be an integer in [1, 100]' }));
    await store.queryNearest(1, 1);
    const el = render().nativeElement as HTMLElement;
    expect(el.querySelector('.error')!.textContent).toContain('count must be');
  });

  it('hovering a row sets the store highlight', async () => {
    api.nearest.mockResolvedValue({
      data: { count: 1, cities: [{ name: 'X', country: 'DE', population: 1, lat: 1, lon: 1, distanceKm: 1 }] },
      engineMicros: 1, computeCount: 1, httpMillis: 1,
    });
    const store = TestBed.inject(GeoQueryStore);
    await store.queryNearest(1, 1);
    const fixture = render();
    const row = fixture.nativeElement.querySelector('.row') as HTMLElement;
    row.dispatchEvent(new MouseEvent('mouseenter'));
    expect(store.highlightedIndex()).toBe(0);
    row.dispatchEvent(new MouseEvent('mouseleave'));
    expect(store.highlightedIndex()).toBeNull();
  });
});
```

- [ ] **Step 2: Run, verify failure.**

- [ ] **Step 3: Implement `web/src/app/panel/query-panel.component.ts`**

```typescript
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { GeoQueryStore } from '../core/geo-query.store';
import { KmPipe } from '../core/format.pipes';
import { QueryMode } from '../core/models';

@Component({
  selector: 'app-query-panel',
  imports: [KmPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="fd-card section">
      <h3 class="fd-label">Query</h3>
      <div class="modes">
        @for (m of modes; track m.value) {
          <button type="button" class="mode" [class.on]="store.mode() === m.value"
                  (click)="store.setMode(m.value)">{{ m.label }}</button>
        }
      </div>
      @switch (store.mode()) {
        @case ('nearest') {
          <label class="field"><span>count</span>
            <input type="number" min="1" max="100" [value]="store.count()"
                   (change)="store.setCount(toNumber($event))" /></label>
        }
        @case ('within') {
          <label class="field"><span>radius {{ store.radiusKm().toFixed(0) }} km</span>
            <input type="range" min="1" max="500" [value]="store.radiusKm()"
                   (input)="store.setRadiusKm(toNumber($event))" /></label>
          <label class="field"><span>min population</span>
            <input type="number" min="0" [value]="store.minPopulation()"
                   (change)="store.setMinPopulation(toNumber($event))" /></label>
        }
        @case ('distance') {
          <p class="hint">Click two points on the map.</p>
          @if (store.distanceKm() !== null) {
            <p class="distance">{{ store.distanceKm() | km }}</p>
          }
        }
      }
      @if (store.queryPoint(); as q) {
        <div class="field ro"><span>lat</span><b>{{ q.lat.toFixed(6) }}</b></div>
        <div class="field ro"><span>lon</span><b>{{ q.lon.toFixed(6) }}</b></div>
      }
      @if (store.error(); as err) { <p class="error">{{ err }}</p> }
    </div>

    @if (store.mode() !== 'distance') {
      <div class="fd-card section results">
        <h3 class="fd-label">Results · {{ store.results().length }}</h3>
        <div class="list">
          @for (city of store.results(); track $index) {
            <div class="row" [class.hot]="store.highlightedIndex() === $index"
                 (mouseenter)="store.highlight($index)" (mouseleave)="store.highlight(null)">
              <span class="rank">{{ ($index + 1).toString().padStart(2, '0') }}</span>
              <span class="name">{{ city.name }}</span>
              <span class="cc">{{ city.country }}</span>
              <span class="km">{{ city.distanceKm | km }}</span>
            </div>
          }
        </div>
      </div>
    }
  `,
  styles: `
    .section { padding: 14px 16px; }
    .fd-label { margin: 0 0 10px; }
    .modes { display: flex; gap: 6px; margin-bottom: 10px; }
    .mode { flex: 1; padding: 8px 0; font: 600 11px/1 var(--fd-mono); text-transform: uppercase;
            background: transparent; color: var(--fd-muted); border: 1px solid var(--fd-line);
            border-radius: 6px; cursor: pointer; }
    .mode.on { background: rgba(84, 215, 255, .14); color: var(--fd-cyan); border-color: rgba(84, 215, 255, .5); }
    .field { display: flex; justify-content: space-between; align-items: center; gap: 10px;
             font: 12px/1.9 var(--fd-mono); color: var(--fd-muted); }
    .field input { background: rgba(84, 215, 255, .06); color: var(--fd-text);
                   border: 1px solid var(--fd-line); border-radius: 4px; padding: 4px 8px;
                   font: 12px var(--fd-mono); width: 110px; }
    .field input[type=range] { width: 140px; accent-color: var(--fd-cyan); }
    .field.ro b { color: var(--fd-text); font-weight: 500; }
    .hint { color: var(--fd-muted); font-size: 12.5px; margin: 4px 0; }
    .distance { font: 600 22px/1.2 var(--fd-mono); color: var(--fd-amber); margin: 6px 0 2px; }
    .error { color: var(--fd-red); font: 12px/1.5 var(--fd-mono); margin: 8px 0 0; }
    .results { flex: 1; min-height: 0; display: flex; flex-direction: column; }
    .list { overflow-y: auto; }
    .row { display: flex; align-items: baseline; gap: 8px; padding: 8px 2px;
           border-bottom: 1px solid rgba(94, 200, 255, .08); font-size: 13.5px; cursor: default; }
    .row.hot { background: rgba(84, 215, 255, .08); }
    .rank { font: 600 10px/1 var(--fd-mono); color: var(--fd-cyan); width: 18px; }
    .name { color: var(--fd-text); font-weight: 600; }
    .cc { font: 600 9px/1 var(--fd-mono); color: var(--fd-muted);
          border: 1px solid var(--fd-line); padding: 2px 4px; border-radius: 3px; }
    .km { margin-left: auto; font: 12.5px var(--fd-mono); color: var(--fd-amber); }
  `,
})
export class QueryPanelComponent {
  protected readonly store = inject(GeoQueryStore);
  protected readonly modes: { value: QueryMode; label: string }[] = [
    { value: 'nearest', label: 'Nearest' },
    { value: 'within', label: 'Within' },
    { value: 'distance', label: 'Dist' },
  ];

  protected toNumber(event: Event): number {
    return Number((event.target as HTMLInputElement).value);
  }
}
```

- [ ] **Step 4: Run tests, verify pass.**

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(web): query panel — modes, clamped params, results list, inline errors"
```

---

### Task 9: Map layers + map shell

**Files:**
- Create: `web/src/app/map/layers.ts`, `web/src/app/map/layers.spec.ts`, `web/src/app/map/map-shell.component.ts`

**Interfaces:**
- Consumes: `GeoQueryStore` (results/queryPoint/rulerPoints/radiusKm/mode/highlightedIndex/distanceKm + queryNearest/queryWithin/addRulerPoint/setMode/highlight), Leaflet (`import * as L from 'leaflet'`).
- Produces: `<app-map-shell/>` and pure builders in `layers.ts`:

```typescript
export function buildQueryPin(lat: number, lon: number): L.CircleMarker;              // cyan, r=7
export function buildResultPin(city: CityHit, index: number, highlighted: boolean): L.CircleMarker; // amber, r=5 (8 when highlighted), tooltip "name · X.XXX km"
export function buildRadiusCircle(lat: number, lon: number, radiusKm: number): L.Circle;            // cyan, dashed, fill .06
export function buildRulerLine(points: { lat: number; lon: number }[]): L.Polyline;                 // amber dashed
export function distanceLabelHtml(km: number): string;                                              // "504.200 km" span for a DivIcon
```

- Gesture rules (map-shell): plain click → mode `nearest` ? `queryNearest` : mode `distance` ? `addRulerPoint` : (mode `within`) `queryWithin` at click with current radius. Alt+mousedown → switch to `within` mode if needed, drag draws a live L.Circle from anchor (radius = anchor→cursor haversine via `map.distance`), mouseup → `queryWithin(anchor.lat, anchor.lng, radiusKm)`. Keyboard `d` toggles distance mode. All Leaflet event handlers are one-liners into the store; rendering is a signal `effect` that clears and redraws a single `L.LayerGroup` from store state.

- [ ] **Step 1: Write failing tests for the pure builders**

`web/src/app/map/layers.spec.ts`:

```typescript
import { describe, it, expect } from 'vitest';
import { buildQueryPin, buildResultPin, buildRadiusCircle, buildRulerLine, distanceLabelHtml } from './layers';

const city = { name: 'München', country: 'DE', population: 1, lat: 48.137, lon: 11.575, distanceKm: 0.612 };

describe('layer builders', () => {
  it('query pin is cyan at the queried location', () => {
    const pin = buildQueryPin(48.1, 11.5);
    expect(pin.getLatLng()).toMatchObject({ lat: 48.1, lng: 11.5 });
    expect(pin.options.color).toBe('#54d7ff');
  });

  it('result pin grows and brightens when highlighted', () => {
    expect(buildResultPin(city, 0, false).options.radius).toBe(5);
    expect(buildResultPin(city, 0, true).options.radius).toBe(8);
    expect(buildResultPin(city, 0, false).options.color).toBe('#ffd166');
  });

  it('result pin tooltip carries name and distance', () => {
    const tooltip = buildResultPin(city, 0, false).getTooltip();
    expect(tooltip!.getContent()).toBe('München · 0.612 km');
  });

  it('radius circle converts km to meters', () => {
    expect(buildRadiusCircle(48, 11, 25).getRadius()).toBe(25000);
  });

  it('ruler line follows the points', () => {
    const line = buildRulerLine([{ lat: 1, lon: 2 }, { lat: 3, lon: 4 }]);
    expect(line.getLatLngs()).toHaveLength(2);
  });

  it('distance label renders formatted km', () => {
    expect(distanceLabelHtml(504.2)).toContain('504.200 km');
  });
});
```

- [ ] **Step 2: Run, verify failure.**

- [ ] **Step 3: Implement**

`web/src/app/map/layers.ts`:

```typescript
import * as L from 'leaflet';
import { CityHit } from '../core/models';

const CYAN = '#54d7ff';
const AMBER = '#ffd166';

export function buildQueryPin(lat: number, lon: number): L.CircleMarker {
  return L.circleMarker([lat, lon], {
    radius: 7, color: CYAN, weight: 2, fillColor: CYAN, fillOpacity: .5,
  });
}

export function buildResultPin(city: CityHit, index: number, highlighted: boolean): L.CircleMarker {
  const pin = L.circleMarker([city.lat, city.lon], {
    radius: highlighted ? 8 : 5, color: AMBER, weight: highlighted ? 3 : 1.5,
    fillColor: AMBER, fillOpacity: highlighted ? .8 : .5,
  });
  pin.bindTooltip(`${city.name} · ${city.distanceKm.toFixed(3)} km`, { direction: 'top' });
  return pin;
}

export function buildRadiusCircle(lat: number, lon: number, radiusKm: number): L.Circle {
  return L.circle([lat, lon], {
    radius: radiusKm * 1000, color: CYAN, weight: 1.5, dashArray: '6 5',
    fillColor: CYAN, fillOpacity: .06,
  });
}

export function buildRulerLine(points: { lat: number; lon: number }[]): L.Polyline {
  return L.polyline(points.map(p => [p.lat, p.lon] as [number, number]), {
    color: AMBER, weight: 2, dashArray: '8 6',
  });
}

export function distanceLabelHtml(km: number): string {
  return `<span class="fd-distance-label">${km.toFixed(3)} km</span>`;
}
```

`web/src/app/map/map-shell.component.ts`:

```typescript
import {
  ChangeDetectionStrategy, Component, ElementRef, effect, inject, viewChild,
  afterNextRender, DestroyRef,
} from '@angular/core';
import * as L from 'leaflet';
import { GeoQueryStore } from '../core/geo-query.store';
import {
  buildQueryPin, buildResultPin, buildRadiusCircle, buildRulerLine, distanceLabelHtml,
} from './layers';

@Component({
  selector: 'app-map-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<div #host class="host"></div>`,
  styles: `
    :host, .host { display: block; position: absolute; inset: 0; }
    ::ng-deep .fd-distance-label {
      font: 600 13px/1 var(--fd-mono); color: var(--fd-amber);
      background: var(--fd-glass); border: 1px solid var(--fd-line);
      border-radius: 6px; padding: 4px 8px; white-space: nowrap;
    }
  `,
})
export class MapShellComponent {
  private readonly host = viewChild.required<ElementRef<HTMLDivElement>>('host');
  private readonly store = inject(GeoQueryStore);
  private readonly destroyRef = inject(DestroyRef);

  private map: L.Map | null = null;
  private layers = L.layerGroup();
  private dragAnchor: L.LatLng | null = null;
  private dragPreview: L.Circle | null = null;

  constructor() {
    afterNextRender(() => this.initMap());
    effect(() => this.render()); // re-runs on any store signal read inside render()
  }

  private initMap(): void {
    const map = L.map(this.host().nativeElement, { zoomControl: false, attributionControl: true })
      .setView([48.1374, 11.5755], 6);
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
      maxZoom: 19,
      attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors',
    }).addTo(map);
    L.control.zoom({ position: 'bottomleft' }).addTo(map);
    this.layers.addTo(map);

    map.on('click', e => {
      if (this.dragAnchor) return; // alt-drag release handled on mouseup
      const { lat, lng } = e.latlng;
      switch (this.store.mode()) {
        case 'nearest': void this.store.queryNearest(lat, lng); break;
        case 'within': void this.store.queryWithin(lat, lng); break;
        case 'distance': void this.store.addRulerPoint(lat, lng); break;
      }
    });
    map.on('mousedown', e => {
      if (!e.originalEvent.altKey) return;
      e.originalEvent.preventDefault();
      map.dragging.disable();
      this.dragAnchor = e.latlng;
      if (this.store.mode() !== 'within') this.store.setMode('within');
    });
    map.on('mousemove', e => {
      if (!this.dragAnchor) return;
      const radiusM = this.dragAnchor.distanceTo(e.latlng);
      this.dragPreview?.remove();
      this.dragPreview = buildRadiusCircle(this.dragAnchor.lat, this.dragAnchor.lng, radiusM / 1000).addTo(map);
    });
    map.on('mouseup', e => {
      if (!this.dragAnchor) return;
      const anchor = this.dragAnchor;
      const radiusKm = anchor.distanceTo(e.latlng) / 1000;
      this.dragAnchor = null;
      this.dragPreview?.remove();
      this.dragPreview = null;
      map.dragging.enable();
      if (radiusKm > 0.05) void this.store.queryWithin(anchor.lat, anchor.lng, radiusKm);
    });

    const onKey = (e: KeyboardEvent) => {
      if (e.key.toLowerCase() === 'd' && !(e.target instanceof HTMLInputElement)) {
        this.store.setMode(this.store.mode() === 'distance' ? 'nearest' : 'distance');
      }
    };
    window.addEventListener('keydown', onKey);
    this.destroyRef.onDestroy(() => { window.removeEventListener('keydown', onKey); map.remove(); });

    this.map = map;
    this.render();
  }

  /** Redraws the single layer group from store state. Reads signals → effect dependency. */
  private render(): void {
    const queryPoint = this.store.queryPoint();
    const results = this.store.results();
    const highlighted = this.store.highlightedIndex();
    const mode = this.store.mode();
    const radiusKm = this.store.radiusKm();
    const rulerPoints = this.store.rulerPoints();
    const distanceKm = this.store.distanceKm();
    if (!this.map) return;

    this.layers.clearLayers();
    if (queryPoint && mode !== 'distance') {
      this.layers.addLayer(buildQueryPin(queryPoint.lat, queryPoint.lon));
      if (mode === 'within') this.layers.addLayer(buildRadiusCircle(queryPoint.lat, queryPoint.lon, radiusKm));
    }
    results.forEach((city, i) => this.layers.addLayer(buildResultPin(city, i, highlighted === i)));
    if (mode === 'distance' && rulerPoints.length > 0) {
      rulerPoints.forEach(p => this.layers.addLayer(buildQueryPin(p.lat, p.lon)));
      if (rulerPoints.length === 2) {
        this.layers.addLayer(buildRulerLine(rulerPoints));
        if (distanceKm !== null) {
          const mid: [number, number] = [
            (rulerPoints[0].lat + rulerPoints[1].lat) / 2,
            (rulerPoints[0].lon + rulerPoints[1].lon) / 2,
          ];
          this.layers.addLayer(L.marker(mid, {
            icon: L.divIcon({ html: distanceLabelHtml(distanceKm), className: '', iconSize: undefined }),
            interactive: false,
          }));
        }
      }
    }
  }
}
```

Implementation notes: Vitest runs in jsdom — the `layers.spec.ts` builders work headless (Leaflet vector objects don't need a map). `map-shell` itself gets no unit spec (per spec: thin wiring, manually smoke-tested in Task 10). If `effect()` in a zoneless app doesn't flush on store changes in the running app, wrap `render()`'s scheduling with `effect(() => { ...reads...; queueMicrotask? })` — no: effects flush automatically; just verify visually in Task 10 and report if anything needed changing.

- [ ] **Step 4: Run tests, verify pass** — `npm test -- --watch=false` → layer builder tests green, everything else unchanged.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(web): Leaflet map shell — OSM tiles, gestures to store, layer rendering"
```

---

### Task 10: Composition + live end-to-end smoke

**Files:**
- Modify: `web/src/app/app.component.ts` (mount the three real components), `web/src/app/app.spec.ts`

**Interfaces:**
- Consumes: `HudBarComponent`, `QueryPanelComponent`, `MapShellComponent` (Tasks 7-9 selectors).

- [ ] **Step 1: Mount the real components** — in `app.component.ts` replace the placeholder slot contents with `<app-map-shell/>`, `<app-hud-bar/>`, `<app-query-panel/>`; add the three components to `imports`. Update the root spec: mocking `GeoApiService` as `{ nearest: vi.fn(), within: vi.fn(), distance: vi.fn() }` in providers, assert the fixture renders `app-hud-bar` and `app-query-panel` elements (map-shell needs real DOM sizing; assert the element exists without asserting Leaflet internals).

- [ ] **Step 2: Unit suite green** — `npm test -- --watch=false && npm run build`.

- [ ] **Step 3: Live smoke (both servers)** —

```powershell
# terminal A (background): API
dotnet run -c Release --project src/HighPerf.Api
# terminal B (background): web
cd web; npm start
```

Then verify in a real browser via Playwright MCP or manual instructions to the user — REQUIRED checks:
1. Map renders dark OSM tiles with visible attribution.
2. Click near Munich → cyan pin + amber pins + panel list; HUD shows engine µs, http ms, `MISS · #n`.
3. Same click again (same quantized bucket) → HUD flips to `cached` + `HIT`.
4. Alt-drag → live circle, release → within results; radius slider re-queries on next click.
5. `D` + two clicks → dashed line + km label; panel shows the distance.
6. Invalid input path: set count field to 0 → clamped to 1 client-side (no 400 possible via UI) — confirm no error line appears.
7. Stop the API process → next click flips HUD to `LINK DOWN`; restart API → next click recovers.
Kill both processes cleanly afterwards (exact PIDs; no orphans).

- [ ] **Step 4: Fix anything the smoke surfaced** (visual polish included: tile filter tuning, z-index, panel scroll). Keep fixes in this task's commit; note each in the commit body.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(web): compose Flight Deck console; live smoke against the API"
```

---

### Task 11: Documentation

**Files:**
- Create: `docs/frontend.md`
- Modify: `docs/index.md`, `docs/architecture.md`, `README.md`

- [ ] **Step 1: Write `docs/frontend.md`** — sections: What it is (Flight Deck console, screenshot placeholder-free description); Running it (`dotnet run -c Release --project src/HighPerf.Api` + `cd web && npm start`, ports 5235/4200); Architecture (GeoQueryStore as single source of truth, thin map/panel/HUD views, mermaid component diagram); HUD semantics (engine µs vs http ms vs cached — copy the honesty rules from the spec verbatim); Gestures table; API additions (Server-Timing + dev CORS, where they live, what tests cover them); Testing (Vitest scope, what's deliberately untested and why).
- [ ] **Step 2: Cross-reference** — `docs/index.md` gains the frontend entry; `docs/architecture.md` gains a one-paragraph "Web console" section linking to frontend.md; `README.md` quickstart gains the web app run line. Verify every file path/name/port cited exists (grep, don't guess).
- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "docs: Flight Deck web console — architecture, HUD semantics, gestures"
```

---

## Plan Self-Review Notes (already applied)

- Spec coverage: CORS+Server-Timing+tests (T1), workspace (T2), service+header parsing (T3), store incl. HIT detection/ring/errors (T4), pipes (T5), theme+shell+tile filter+attribution (T6), HUD honesty rules (T7), panel+inline errors+hover highlight (T8), gestures+layers+two-way highlight (T9), composition+live E2E checks incl. link-down recovery (T10), docs (T11). Spec's "hovering a pin highlights the row" direction: pin→row hover is NOT wired (only row→pin) — deliberate v1 trim; the spec says "two-way" so T9's builders accept the highlight flag and T10's smoke may add `pin.on('mouseover')` one-liners into `store.highlight(i)` if trivially doable; otherwise note it in docs/frontend.md as a known gap. Executor: treat that as in-scope polish for T10, not a new task.
- Placeholder scan: clean — every code step carries complete code; T10/T11 are verification/prose tasks by nature with concrete checklists.
- Type consistency: `CityHit`/`ApiResult`/`RequestTiming`/`QueryMode` (T3) used identically in T4/T7/T8/T9; store method names `queryNearest/queryWithin/addRulerPoint/setMode/setCount/setRadiusKm/setMinPopulation/highlight` consistent across T4/T8/T9; palette hexes in T6 tokens match T9 layer constants (`#54d7ff`, `#ffd166`).
- Known risks flagged for the executor: Angular 21 CLI flag names (`--zoneless`, `--defaults`) may differ per minor version — T2 documents fallbacks; Vitest + TestBed in zoneless mode needs no zone imports (CLI default config); Leaflet + jsdom is only exercised for pure vector builders; `mousedown`/`mouseup` on Leaflet maps fire for vector layers too — the `dragAnchor` guard keeps click handling correct; OSM tile usage policy (attribution required, dev-scale traffic fine).
