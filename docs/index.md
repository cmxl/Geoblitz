# HighPerformance Geo API — Documentation

## Purpose

This project is a learning/reference showcase, not a product. It answers a narrow set of
geospatial queries — nearest cities, cities within a radius, great-circle distance,
geohash encode/decode — over the real GeoNames `cities1000` dataset (170,584 cities), and
uses the exercise to demonstrate a catalog of concrete, measured performance techniques in
a .NET 10 minimal API: struct-of-arrays layout, SIMD scans, zero-allocation query paths,
source-generated JSON, and output caching with a quantized cache key.

Read the docs in this order if you're new to the project:

1. **[Architecture](architecture.md)** — solution layout, startup flow (embedded gzip →
   span parse → cell-order permutation → CSR grid), and the end-to-end request flow for
   `/cities/nearest`, with component and sequence diagrams.
2. **[Performance techniques](performance-techniques.md)** — the core deliverable: ten
   techniques used in the hot path, each with what it is, why it exists, where it lives in
   the code, and the measured effect.
3. **[API reference](api.md)** — all six endpoints: parameters, defaults, limits, example
   requests/responses, error shape, and caching semantics.
4. **[Benchmarks](benchmarks.md)** — how to run the BenchmarkDotNet suite and the k6 load
   tests, how to read `MemoryDiagnoser` output, and the latest measured results.

## Project history

- **Design spec**: [`work/specs/2026-08-07-highperf-geo-api-design.md`](../work/specs/2026-08-07-highperf-geo-api-design.md)
- **Implementation plan**: [`work/plans/2026-08-07-highperf-geo-api.md`](../work/plans/2026-08-07-highperf-geo-api.md)

Two things changed between the original spec and the finished implementation, both
explained where they matter:

- The spec's `X-Cache` header was replaced by `X-Compute-Count` replay semantics —
  see [performance-techniques.md, item 8](performance-techniques.md#8-output-caching-with-quantized-keys).
- The spec's `SinLat`/`CosLat` precomputation was refined into full unit-vector `X`/`Y`/`Z`
  arrays — see [performance-techniques.md, item 2](performance-techniques.md#2-unit-vector-chord-distance).

## Repository root

- [`README.md`](../README.md) — project pitch and quickstart.
- `src/HighPerf.Geo` — the geo index and math library.
- `src/HighPerf.Api` — the minimal-API host.
- `tests/` — 83 xUnit tests across both projects.
- `benchmarks/` — BenchmarkDotNet suite and [`RESULTS.md`](../benchmarks/RESULTS.md).
- `loadtest/` — k6 scripts.
- `tools/prepare-dataset.ps1` — regenerates the embedded dataset.
