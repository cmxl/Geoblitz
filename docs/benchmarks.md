# Benchmarks

[← Back to docs index](index.md)

Two kinds of measurement exist in this repo: a BenchmarkDotNet micro-benchmark suite
(`Geoblitz.Geo` in isolation, no HTTP) and k6 load-test scripts (the full API over HTTP).

## Running the BenchmarkDotNet suite

```bash
dotnet run -c Release --project benchmarks/Geoblitz.Benchmarks -- --filter "*GeoBenchmarks*"
```

The benchmarks live in `benchmarks/Geoblitz.Benchmarks/GeoBenchmarks.cs`
(`[MemoryDiagnoser]` class `GeoBenchmarks`), and load the real embedded dataset via
`GeoDatabase.LoadDefault()` in `[GlobalSetup]`. The command above uses BenchmarkDotNet's
**default job** (auto-tuned invocation count, heuristic warmup, auto-terminated measurement,
1 launch; ~4.5 min for the whole suite); every number quoted in this repo comes from such a run
and has a 99.9 % confidence margin under 3 % of its mean. Adding `--job short` (`ShortRun`:
`IterationCount=3, LaunchCount=1, WarmupCount=3`) is a smoke run only — it produced error
bars up to 2x the mean on the fast rows, which is why its numbers are no longer quoted
anywhere.

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
| `Scalar_HaversineFullScan` | Baseline: linear scan of all 170,584 cities using per-point `GeoMath.HaversineKm`, keeping the minimum. |
| `Scalar_HaversineFullScanCollect` | Same trig scan, but *collecting every city within 100 km* — output-identical to the two chord benchmarks, so it is the apples-to-apples trig baseline. |
| `Scalar_ChordFullScan` | The same collect-within-100 km work with trig-free squared-chord distance in a plain scalar loop — isolates trig elimination from vectorization. |
| `Simd_ChordFullScan` | `ChordKernel.ScanWithin` over the full unit-vector arrays (no grid pruning) — the same chord loop with `Vector<float>`, so vs. `Scalar_ChordFullScan` it isolates the SIMD win. |
| `Grid_FindWithin100km` | `GeoDatabase.FindWithin`, 100 km radius around Berlin — grid pruning + SIMD + bounded `TopK` selection combined. |
| `Grid_FindNearest10` | `GeoDatabase.FindNearest`, k=10, around Berlin (dense region — few radius-expansion rounds). |
| `Grid_FindNearest10_SparseOcean` | Same, but centered in the South Pacific (sparse region — forces several radius-quadrupling rounds). |
| `Scalar_HaversineSinglePair` | A single `HaversineKm` call — the per-call trig cost floor. |

The three-way ladder (`Scalar_HaversineFullScanCollect` → `Scalar_ChordFullScan` →
`Simd_ChordFullScan`) exists so that no single quoted factor mixes variables: the three produce
the same result set, and consecutive rows differ in exactly one implementation choice.

## Running the k6 load tests

```bash
dotnet run -c Release --project src/Geoblitz.Api   # terminal 1, starts on http://localhost:5235
k6 run loadtest/mixed.js                            # terminal 2
```

Individual endpoint scripts: `loadtest/distance.js`, `loadtest/nearest.js`,
`loadtest/within.js`. `mixed.js` drives all four query endpoints (nearest/within/distance/
geohash) with a realistic 40/30/20/10 traffic split and a mix of cache-busting random
coordinates (~70%) and hot repeated coordinates (~30%) to exercise the output cache under
load. Default profile: 32 concurrent VUs for 30 seconds. Thresholds: p95 < 20 ms,
p99 < 50 ms, error rate < 0.1% — comfortably met on the reference machine (see measured
results below); recalibrate for your own target environment. Override the target with
`BASE_URL=http://host:port k6 run loadtest/mixed.js`.
Full details: `loadtest/README.md`.

### Measured k6 results (2026-08-08)

Measured on the reference machine below (server and k6 on the same host — numbers include
loopback HTTP but no real network), k6 v2.1.0, Release build, 32 VUs × 30 s per scenario,
all thresholds passed with zero failed requests:

| Scenario | Requests | Throughput | avg | p95 | p99 |
|---|---:|---:|---:|---:|---:|
| `mixed.js` (all endpoints, 30% hot/cached) | 627,659 | 20,914 req/s | 1.35 ms | 3.38 ms | 5.81 ms |
| `nearest.js` | 594,676 | 19,822 req/s | 1.44 ms | 3.50 ms | 6.79 ms |
| `within.js` (100 km radius) | 600,772 | 20,021 req/s | 1.42 ms | 3.52 ms | 6.61 ms |
| `distance.js` | 720,805 | 24,025 req/s | 1.16 ms | 2.92 ms | 5.05 ms |

Server process after the ~2.5M-request session: 503 MB working set, no request failures —
Server GC deliberately trades memory for throughput, and the output cache retains entries
for its 10-minute TTL, so a large steady-state working set is expected, not a leak.

## Latest snapshot

Machine: AMD Ryzen 5 3600X 6-Core (3.80 GHz, 12 logical / 6 physical cores), 127.92 GB RAM,
Windows 11 Pro 10.0.26200, .NET SDK 10.0.302, .NET Runtime 10.0.10, BenchmarkDotNet v0.15.8.
Job: **DefaultJob** (BenchmarkDotNet defaults: auto-tuned invocation count, heuristic warmup,
auto-terminated measurement, 1 launch).

| Method                          | Mean             | Error           | StdDev          | Ratio | Allocated |
|-------------------------------- |-----------------:|----------------:|----------------:|------:|----------:|
| Scalar_HaversineFullScan        | 7,145,274.115 ns |  91,452.1508 ns |  85,544.3986 ns | 1.000 |         - |
| Scalar_HaversineFullScanCollect | 6,621,386.120 ns | 130,050.9789 ns | 194,654.1977 ns | 0.927 |         - |
| Scalar_ChordFullScan            |   367,699.328 ns |   5,592.4511 ns |  10,226.1140 ns | 0.051 |         - |
| Simd_ChordFullScan              |    47,004.962 ns |     846.5560 ns |   1,070.6223 ns | 0.007 |         - |
| Grid_FindWithin100km            |     9,086.349 ns |     180.8439 ns |     488.9220 ns | 0.001 |         - |
| Grid_FindNearest10              |     1,077.659 ns |      21.1907 ns |      21.7613 ns | 0.000 |         - |
| Grid_FindNearest10_SparseOcean  |     4,189.976 ns |      81.5212 ns |      97.0452 ns | 0.001 |         - |
| Scalar_HaversineSinglePair      |         7.113 ns |       0.1769 ns |       0.2038 ns | 0.000 |         - |

Full raw results, machine info, the attribution table and observations:
[`benchmarks/RESULTS.md`](../benchmarks/RESULTS.md).

### Key takeaways

- **Zero-allocation proof**: all eight benchmarks show `-` (0 B) allocated, including the
  full-scan baselines — the query path (`GeoDatabase.FindWithin`/`FindNearest`, backed by
  bounded `TopK` selection on `stackalloc`/`ArrayPool` storage) allocates nothing on the
  managed heap.
- **Trig elimination is the bigger factor: 18.0x.** `Scalar_ChordFullScan` (367.7 us) vs
  `Scalar_HaversineFullScanCollect` (6.62 ms) — same scalar loop, same result set, the only
  difference being squared-chord distance on precomputed unit vectors instead of
  `Sin`/`Cos`/`Atan2` per point.
- **Vectorization on top: 7.8x.** `Simd_ChordFullScan` (47.0 us) vs `Scalar_ChordFullScan`
  (367.7 us) — near-linear in the 8 `float` lanes of `Vector<float>` on this AVX2 host.
  Combined, the trig-free SIMD kernel is **140.9x** the naive trig scan; against the min-only
  baseline the ratio reads 152x. (Earlier revisions quoted a single "137x SIMD speedup" — that
  conflated metric, output and vectorization, and has been replaced by this split.)
- **Grid vs SIMD full scan**: `Grid_FindWithin100km` (9.09 us) beats `Simd_ChordFullScan`
  (47.0 us) by ~5.2x — grid-cell candidate-range pruning cuts how much data the SIMD kernel
  has to touch. End to end, the shipped path is ~786x the naive baseline for a radius query and
  ~6,630x for `FindNearest` k=10.
- **Nearest-neighbor density sensitivity**: `Grid_FindNearest10` (1.08 us, dense region
  around Berlin) is ~3.9x faster than `Grid_FindNearest10_SparseOcean` (4.19 us), because the
  sparse South Pacific query point needs several radius-quadrupling rounds before 10
  neighbors are found.

## API endpoint allocations

BenchmarkDotNet measures `Geoblitz.Geo` only. The endpoints' own allocation profile is measured
by `tests/Geoblitz.Api.Tests/AllocationTests.cs`, which runs on every `dotnet test`:

| Measurement | Value | Covers |
|---|---|---|
| `/cities/nearest` hot path, in-process, exact per-thread count | **256 B / request** | `GeoCacheKey.Compute` + span query parsing + validation + `FindNearest` + `ServerTiming.Set` + `CityJson.WriteCities` + `BodyWriter.FlushAsync` |
| `/cities/nearest` end to end via `TestServer` | ~101 KB / request | the above plus routing, output-cache store, TestServer + `HttpClient` |
| `/cities/within` end to end via `TestServer` | ~110 KB / request | as above, several-hundred-city body |
| cached replay end to end via `TestServer` | ~16-31 KB / request | output-cache hit, handler not executed |

The 256 B is the load-bearing number and is asserted against a 512 B ceiling: it accounts for the
three documented per-request strings (the `X-Compute-Count` value, the `GeoCacheKey` string, and
the `Server-Timing` header value) plus header bookkeeping, and **nothing scales with the number of
cities or bytes written**. The
TestServer figures are dominated by the test harness (a fresh `HttpContext`, DI scope, two pipes and
the client's own request/response objects per call); they are kept as a coarse gross-regression
tripwire and must not be read as Kestrel's per-request cost.

## See also

- [Performance techniques](performance-techniques.md) — the techniques these numbers
  validate, with file:member references for each one.
- [Architecture](architecture.md) — where each measured stage sits in the request flow.
- [API reference](api.md) — the HTTP-level caching behavior these micro-benchmarks feed into.
