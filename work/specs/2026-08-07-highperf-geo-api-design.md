# HighPerf Geo API — Design Spec

**Date:** 2026-08-07
**Status:** Approved
**Purpose:** Learning/reference showcase for maximum-performance ASP.NET Core — every technique documented and benchmarked so patterns can be lifted into real projects.

## Goal

A web API on .NET 10 / ASP.NET Core tuned for the lowest possible response times and highest computational efficiency from the first commit. No database — the API computes geo results (distances, nearest cities, radius searches, geohashes) over an in-memory dataset. Caching is an explicit **second** layer: the compute path must be fast on its own; caching only accelerates repeated queries.

**Success criteria:**

- Zero heap allocations per request on the hot query endpoints (verified with BenchmarkDotNet `MemoryDiagnoser` and load-test GC counters).
- Every optimization is proven by a benchmark (scalar vs. SIMD, grid vs. brute force, cached vs. uncached) — no cargo-cult tuning.
- Kernel correctness verified against naive reference implementations, including edge cases (poles, antimeridian, zero distance).
- `docs/` teaches each technique: what, why, measured numbers, where it lives in code.

## Decisions (with rationale)

| Decision | Choice | Rationale |
|---|---|---|
| Domain | Geo/routing engine | Heavy real math, SIMD-friendly, meaningful results, natural cache story |
| Dataset | GeoNames `cities1000` (~140k cities), embedded resource | Real-world data, zero external dependencies, loads at startup |
| Runtime | JIT + Dynamic PGO (no Native AOT) | Highest steady-state throughput for compute kernels; startup time irrelevant for a long-running API. Code stays AOT-*compatible* (source-gen JSON, no reflection) but we publish JIT. |
| Spatial index | Fixed lat/lon grid, CSR layout | Flat arrays, cache-friendly, no pointer chasing; showcases indexing + SIMD together. K-d tree rejected (branchy, cache-hostile, hard to keep allocation-free). |
| Caching | In-process only: OutputCache + FusionCache L1 | **Deviation from global FusionCache L1+L2 Redis default, deliberate:** with no database, a Redis hop (network + serialization) costs more than recomputing. Single-instance compute service needs no backplane. |
| JSON | System.Text.Json source-generated; `Utf8JsonWriter` → `PipeWriter` on hot paths | Reflection-free, zero intermediate DTO/buffer allocations |
| GC | Server GC | Throughput-oriented; steady in-memory dataset suits it |

## Solution layout

```
highperformance/
├─ src/
│  ├─ HighPerf.Geo/          # pure compute library — no ASP.NET dependency
│  └─ HighPerf.Api/          # minimal API host, thin HTTP adapter over Geo
├─ tests/
│  ├─ HighPerf.Geo.Tests/    # xUnit v3 — kernels vs. naive reference implementations
│  └─ HighPerf.Api.Tests/    # WebApplicationFactory integration tests
├─ benchmarks/
│  └─ HighPerf.Benchmarks/   # BenchmarkDotNet
├─ loadtest/                 # k6 scripts (req/s, p50/p99 latency)
├─ docs/                     # cross-referenced markdown + Mermaid
└─ work/                     # specs + plans (this file)
```

The compute engine is a standalone library so benchmarks and unit tests exercise it without HTTP overhead; `HighPerf.Api` stays a thin adapter.

## Components

### 1. `HighPerf.Geo` — data layer

**`GeoDataset`** (immutable after startup, lock-free concurrent reads):

- Source: GeoNames `cities1000.txt` (tab-separated), GZip-compressed embedded resource (name, ASCII name, country code, lat, lon, population).
- Startup parsing: streamed decompression + `Utf8Parser` span parsing. No `string.Split`, no intermediate strings. City names stored once as UTF-8 bytes (flat `byte[]` blob + offsets) for zero-conversion JSON writing; a parallel `string[]` only if a non-hot path needs it.
- Struct-of-arrays layout:
  - `float[] Lat, Lon`
  - `float[] SinLat, CosLat` — precomputed so per-point distance math skips trig
  - `int[] Population`
  - name blob + `int[] NameOffsets`, `string[] CountryCodes` (interned, ~250 distinct)

### 2. `HighPerf.Geo` — grid index

**`GridIndex`**, built once at startup:

- Fixed-size lat/lon cells (cell size chosen so average occupancy ≈ tens of points; exact size decided by benchmark during implementation).
- CSR layout: `int[] CellStart` (offsets, length = cells + 1) + `int[] PointIndex` (flat, length = N). Point indices within a cell are contiguous → linear SIMD-scannable memory.
- Query support: given (lat, lon, radius) or (lat, lon, k), compute the ring of intersecting cells with pure arithmetic (handles antimeridian wrap and polar clamping), yield contiguous candidate ranges.

### 3. `HighPerf.Geo` — compute kernels

- **Haversine distance**: scalar reference implementation (`double`, correctness baseline) + vectorized implementation (`Vector256<float>`/`Vector512<float>` with runtime width detection, using precomputed sin/cos arrays).
- **k-nearest**: candidate cells expand ring-by-ring until the k-th best distance beats the nearest unvisited ring; per-candidate distances via SIMD scan; top-k maintained in a `stackalloc` fixed-size max-heap (k capped at 100).
- **Radius search**: SIMD scan over candidate cells, matches written through a caller-provided `Span<Result>` or pooled buffer (`ArrayPool<T>`), count returned. Optional min-population filter applied in the same pass.
- **Geohash encode/decode**: bit-interleaving implementation, `stackalloc` char/byte buffers.
- Kernel API style: `static` methods over `ReadOnlySpan<float>` inputs, results into caller-provided spans. No LINQ, no delegates, no closures on hot paths. `readonly struct` for all value types; `[SkipLocalsInit]` where measured to help.

### 4. `HighPerf.Api` — HTTP layer

Endpoints (minimal APIs):

| Route | Computation |
|---|---|
| `GET /distance?fromLat&fromLon&toLat&toLon` | Haversine between two points (pure-math baseline endpoint) |
| `GET /cities/nearest?lat&lon&count` | k-nearest cities (count default 5, max 100) |
| `GET /cities/within?lat&lon&radiusKm&minPopulation` | radius search (radius max 500 km, results capped at 1000, sorted by distance) |
| `GET /geohash/encode?lat&lon&precision` | geohash string (precision 1–12, default 9) |
| `GET /geohash/decode?hash` | center lat/lon + error bounds |
| `GET /healthz` | liveness (no dataset touch) |

Request path:

- Parameter parsing via `TryParse`/`IUtf8SpanParsable`-style span parsing; validation (lat ∈ [-90, 90], lon ∈ [-180, 180], bounds above) fails fast with `ProblemDetails` 400 — no exceptions for control flow, no allocation before validation passes.
- Responses: `readonly record struct` DTOs serialized with source-generated `JsonSerializerContext`; the two hot endpoints (`nearest`, `within`) write JSON with `Utf8JsonWriter` directly into `HttpResponse.BodyWriter`, streaming city names from the UTF-8 blob without string conversion.
- Kestrel/host trimming: no unused middleware, no response compression (small payloads), `Server` header suppressed, `InvariantGlobalization`, `ServerGarbageCollection`, TieredPGO (default-on) documented.

### 5. Caching — second layer

- **OutputCache** (in-process) on `nearest`, `within`, `distance`, and `geohash` endpoints; vary-by-query with short TTLs (dataset is static, so TTL is about memory pressure, not staleness).
- **Cache-key quantization (core teaching point):** raw float coordinates make every key unique (`48.13701` ≠ `48.13702`), rendering the cache useless. A custom output-cache policy quantizes lat/lon to ~3 decimals (≈ 110 m buckets) in the cache key while computing with full precision on miss. Documented with hit-rate measurements.
- **FusionCache (L1 memory only)** for computed sub-results where partial reuse pays off (e.g., resolved candidate-cell sets for hot map regions) — only where benchmarks show a win; otherwise omitted. No Redis, no backplane (see Decisions).
- Cache hit/miss observable via response header (e.g. `X-Cache`) for tests and load tests.

### 6. Error handling

- Validation errors → `ProblemDetails` 400 with field-level messages.
- Unexpected errors → global exception handler → `ProblemDetails` 500, Serilog structured log; no stack traces in responses.
- Startup failures (dataset missing/corrupt) fail the host immediately with a clear log message.

### 7. Testing

- **TDD** for all kernels: scalar reference implementation first (validated against known city-pair distances), then SIMD implementations must match the reference within tolerance across randomized inputs and edge cases: poles, antimeridian crossing, zero distance, k > candidate count, empty radius results.
- Grid index tests: cell assignment, ring expansion order, antimeridian wrap.
- Integration tests (`WebApplicationFactory`): endpoint contracts, validation responses, output-cache behavior (miss → hit via `X-Cache`), JSON shape.
- Benchmarks (BenchmarkDotNet, `MemoryDiagnoser`): scalar vs. SIMD Haversine; brute-force vs. grid query; allocation counts per operation. Cached vs. uncached end-to-end comparison lives in the k6 load tests, not BenchmarkDotNet.
- Load tests: k6 scripts for each endpoint, mixed-traffic scenario, reporting req/s and p50/p95/p99.

### 8. Documentation (`docs/`)

- `docs/index.md` — entry point and cross-reference index
- `docs/architecture.md` — solution structure, request flow, Mermaid diagrams
- `docs/performance-techniques.md` — catalog: each technique (SoA layout, SIMD kernels, span parsing, source-gen JSON, direct PipeWriter writes, pooling/stackalloc, cache-key quantization, GC/host settings) with what/why/measured numbers/code location
- `docs/api.md` — endpoint reference
- `docs/benchmarks.md` — how to run BDN + k6, latest result snapshots

## Out of scope (YAGNI)

- Authentication/BFF (no browser client, no sensitive data)
- Redis/L2 cache and backplane
- Docker/nginx deployment assets (can be added later via `/noobit:deploy-setup`)
- Routing/pathfinding between cities (name says "routing engine" informally; actual routing graphs are a separate project)
- Rate limiting, OpenAPI UI hardening — dev-time OpenAPI JSON only

## Open items deferred to implementation plan

- Exact grid cell size (benchmark-driven, start ~1°×1° and tune)
- Whether `Vector512` paths pay off on target hardware vs. `Vector256` only
- Whether FusionCache sub-result caching earns its place (benchmark gate)
