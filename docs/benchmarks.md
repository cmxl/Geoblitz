# Benchmarks

[← Back to docs index](index.md)

Two kinds of measurement exist in this repo: a BenchmarkDotNet micro-benchmark suite
(`HighPerf.Geo` in isolation, no HTTP) and k6 load-test scripts (the full API over HTTP).

## Running the BenchmarkDotNet suite

```bash
dotnet run -c Release --project benchmarks/HighPerf.Benchmarks -- --filter "*GeoBenchmarks*" --job short
```

The benchmarks live in `benchmarks/HighPerf.Benchmarks/GeoBenchmarks.cs`
(`[MemoryDiagnoser]` class `GeoBenchmarks`), and load the real embedded dataset via
`GeoDatabase.LoadDefault()` in `[GlobalSetup]`. `--job short` runs BenchmarkDotNet's
`ShortRun` preset (`IterationCount=3, LaunchCount=1, WarmupCount=3`) — fast enough for a
smoke run, but the resulting confidence intervals are wide; treat the numbers as
directional (correct relative ordering, valid allocation counts) rather than
publication-grade absolute numbers. Drop `--job short` for a full run with tighter
confidence intervals.

### Reading `MemoryDiagnoser` output

Each benchmark method reports:

- **Mean / Error / StdDev**: wall-clock time per invocation and its statistical spread.
- **Ratio / RatioSD**: mean time relative to the `[Benchmark(Baseline = true)]` method
  (`Scalar_HaversineFullScan` in this suite).
- **Allocated / Alloc Ratio**: managed bytes allocated per invocation, measured by the
  `[MemoryDiagnoser]` attribute. A `-` means BenchmarkDotNet observed zero allocations.

### What each benchmark measures

| Method | What it exercises |
|---|---|
| `Scalar_HaversineFullScan` | Baseline: linear scan of all 170,584 cities using per-point `GeoMath.HaversineKm`. |
| `Simd_ChordFullScan` | `ChordKernel.ScanWithin` over the full unit-vector arrays (no grid pruning) — isolates the SIMD chord-distance win. |
| `Grid_FindWithin100km` | `GeoDatabase.FindWithin`, 100 km radius around Berlin — grid pruning + SIMD combined. |
| `Grid_FindNearest10` | `GeoDatabase.FindNearest`, k=10, around Berlin (dense region — few radius-expansion rounds). |
| `Grid_FindNearest10_SparseOcean` | Same, but centered in the South Pacific (sparse region — forces multiple radius-doubling rounds). |
| `Scalar_HaversineSinglePair` | A single `HaversineKm` call — the per-call trig cost floor. |

## Running the k6 load tests

```bash
dotnet run -c Release --project src/HighPerf.Api   # terminal 1, starts on http://localhost:5235
k6 run loadtest/mixed.js                            # terminal 2
```

Individual endpoint scripts: `loadtest/distance.js`, `loadtest/nearest.js`,
`loadtest/within.js`. `mixed.js` drives all four query endpoints (nearest/within/distance/
geohash) with a realistic 40/30/20/10 traffic split and a mix of cache-busting random
coordinates (~70%) and hot repeated coordinates (~30%) to exercise the output cache under
load. Default profile: 32 concurrent VUs for 30 seconds. Initial thresholds (uncalibrated —
adjust after a real run against your target environment): p95 < 20 ms, p99 < 50 ms, error
rate < 0.1%. Override the target with `BASE_URL=http://host:port k6 run loadtest/mixed.js`.
Full details: `loadtest/README.md`.

No k6 run output is checked into this repo — the thresholds above are the scripts'
starting configuration, not a measured result. The BenchmarkDotNet numbers below are the
project's measured performance evidence.

## Latest snapshot

Machine: AMD Ryzen 5 3600X 6-Core (3.80 GHz, 12 logical / 6 physical cores), 127.92 GB RAM,
Windows 11 Pro 10.0.26200, .NET SDK 10.0.302, .NET Runtime 10.0.10, BenchmarkDotNet v0.15.8.
Job: `ShortRun` (`IterationCount=3, LaunchCount=1, WarmupCount=3`).

| Method                         | Mean             | Error            | StdDev        | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |-----------------:|-----------------:|--------------:|------:|--------:|----------:|------------:|
| Scalar_HaversineFullScan       | 6,664,077.604 ns | 1,618,644.477 ns | 88,723.366 ns | 1.000 |    0.02 |         - |          NA |
| Simd_ChordFullScan             |    48,699.134 ns |    25,540.029 ns |  1,399.935 ns | 0.007 |    0.00 |         - |          NA |
| Grid_FindWithin100km           |     8,550.883 ns |    10,404.613 ns |    570.312 ns | 0.001 |    0.00 |         - |          NA |
| Grid_FindNearest10             |       920.351 ns |     1,344.879 ns |     73.717 ns | 0.000 |    0.00 |         - |          NA |
| Grid_FindNearest10_SparseOcean |     2,866.194 ns |     3,761.993 ns |    206.208 ns | 0.000 |    0.00 |         - |          NA |
| Scalar_HaversineSinglePair     |         9.237 ns |        19.456 ns |      1.066 ns | 0.000 |    0.00 |         - |          NA |

Full raw results, machine info, and observations: [`benchmarks/RESULTS.md`](../benchmarks/RESULTS.md).

### Key takeaways

- **Zero-allocation proof**: all six benchmarks show `-` (0 B) allocated, including the two
  full-scan baselines — the query path (`GeoDatabase.FindWithin`/`FindNearest`, backed by
  `HitBuffer`/`TopK` and `ArrayPool`) allocates nothing on the managed heap.
- **SIMD vs scalar**: `Simd_ChordFullScan` (48.7 us) is ~137x faster than
  `Scalar_HaversineFullScan` (6.66 ms) — chord-distance SIMD scanning beats naive per-point
  Haversine by more than two orders of magnitude.
- **Grid vs SIMD full scan**: `Grid_FindWithin100km` (8.55 us) beats `Simd_ChordFullScan`
  (48.7 us) by ~5.7x — grid-cell candidate-range pruning cuts how much data the SIMD kernel
  has to touch.
- **Nearest-neighbor density sensitivity**: `Grid_FindNearest10` (920 ns, dense region
  around Berlin) is ~3x faster than `Grid_FindNearest10_SparseOcean` (2.87 us), because the
  sparse South Pacific query point needs multiple radius-doubling rounds before 10
  neighbors are found.

## See also

- [Performance techniques](performance-techniques.md) — the techniques these numbers
  validate, with file:member references for each one.
- [Architecture](architecture.md) — where each measured stage sits in the request flow.
- [API reference](api.md) — the HTTP-level caching behavior these micro-benchmarks feed into.
