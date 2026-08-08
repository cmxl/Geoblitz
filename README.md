# HighPerformance Geo API

A .NET 10 minimal API that answers geospatial queries — nearest cities, cities within a
radius, great-circle distance, and geohash encode/decode — over the full GeoNames
`cities1000` dataset (170,584 cities), entirely from an in-memory, struct-of-arrays index.
It exists as a learning/reference showcase for allocation-free, SIMD-accelerated request
handling in ASP.NET Core: every query-path allocation, trig call, and JSON write was a
deliberate choice, measured with BenchmarkDotNet and exercised under load with k6.

See [`docs/`](docs/index.md) for the full write-up: architecture, the performance
techniques catalog, the API reference, and benchmark results.

## Measured highlights

Full linear scan of all 170,584 cities, collecting every city within 100 km — the same result
set three ways, so each factor isolates one decision (BenchmarkDotNet default job, AMD Ryzen 5
3600X, .NET 10; see [`benchmarks/RESULTS.md`](benchmarks/RESULTS.md)):

| Implementation | Time | Allocated | Factor |
|---|---:|---:|---|
| Scalar loop, haversine trig per city | 6.62 ms | 0 B | baseline |
| Scalar loop, trig-free squared-chord distance | 367.7 us | 0 B | **18.0x** (trig elimination) |
| `Vector<float>` SIMD, squared-chord distance | 47.0 us | 0 B | **7.8x** more (vectorization) |
| Shipped path: grid candidate ranges + SIMD (`FindWithin`, 100 km) | 9.09 us | 0 B | **5.2x** more (pruning) |
| Shipped path: `FindNearest`, k=10 | 1.08 us | 0 B | 6,630x vs. the naive baseline |

At the HTTP layer, the `/cities/nearest` hot path (cache key, query parsing, validation, query,
JSON write, flush) allocates a measured **256 B per request** — the three documented per-request
strings (cache key, `X-Compute-Count`, `Server-Timing`) and header bookkeeping, with nothing
scaling per city or per byte (`tests/HighPerf.Api.Tests/AllocationTests.cs`).

## Quickstart

Requires the .NET 10 SDK.

```bash
dotnet run -c Release --project src/HighPerf.Api
```

The API listens on `http://localhost:5235` (see
`src/HighPerf.Api/Properties/launchSettings.json`). The city dataset is embedded in the
build (`src/HighPerf.Geo/Resources/cities.tsv.gz`), so no separate data step is needed to
run the API.

Only if you want to regenerate the dataset from a fresh GeoNames export:

```powershell
pwsh tools/prepare-dataset.ps1
```

This downloads `cities1000.zip` from geonames.org, reduces each row to a 5-column TSV
(`name, country, lat, lon, population`), and gzips it into
`src/HighPerf.Geo/Resources/cities.tsv.gz`.

Optional: the "Flight Deck" web console (Angular, requires the API above running first).

```bash
cd web && npm ci && npm start
```

Then open `http://localhost:4200` — see [`docs/frontend.md`](docs/frontend.md) for what it
does and how it's built.

## Try it

```bash
# Health check
curl "http://localhost:5235/healthz"

# Great-circle distance between Berlin and Munich (km)
curl "http://localhost:5235/distance?fromLat=52.52&fromLon=13.405&toLat=48.1374&toLon=11.5755"

# 5 nearest cities to a point
curl "http://localhost:5235/cities/nearest?lat=52.52437&lon=13.41053&count=5"

# Cities within 30 km of Munich with population >= 1,000,000
curl "http://localhost:5235/cities/within?lat=48.1374&lon=11.5755&radiusKm=30&minPopulation=1000000"

# Encode a coordinate to a geohash
curl "http://localhost:5235/geohash/encode?lat=57.64911&lon=10.40744&precision=11"

# Decode a geohash back to a coordinate + error bounds
curl "http://localhost:5235/geohash/decode?hash=u4pruydqqvj"
```

Full parameter reference, defaults, limits, and error shapes: [`docs/api.md`](docs/api.md).

## Development

```bash
dotnet test                 # 160 tests: 79 HighPerf.Geo.Tests + 81 HighPerf.Api.Tests
dotnet run -c Release --project benchmarks/HighPerf.Benchmarks -- --filter "*GeoBenchmarks*"
k6 run loadtest/mixed.js    # requires the API running and k6 installed
```

## Project layout

- `src/HighPerf.Geo` — the geo index and math library (no ASP.NET Core dependency).
- `src/HighPerf.Api` — the minimal-API host: 6 endpoints, output caching, JSON writing.
- `tests/` — xUnit test suites for both projects (160 tests total, including the endpoint
  allocation tripwire and the cache-validation regression suite).
- `benchmarks/HighPerf.Benchmarks` — BenchmarkDotNet suite; results in `benchmarks/RESULTS.md`.
- `loadtest/` — k6 scripts for per-endpoint and mixed-traffic load testing.
- `tools/prepare-dataset.ps1` — regenerates the embedded dataset from GeoNames.
- `web/` — the Angular "Flight Deck" map console for the API (see [`docs/frontend.md`](docs/frontend.md)).
- `docs/` — architecture, performance techniques, API reference, benchmarks, frontend ([index](docs/index.md)).
