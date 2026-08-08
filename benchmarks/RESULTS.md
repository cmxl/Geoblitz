# Benchmark Results

## Machine

- **CPU**: AMD Ryzen 5 3600X 6-Core Processor (3.80 GHz, 1 CPU, 12 logical / 6 physical cores)
- **RAM**: 127.92 GB
- **OS**: Windows 11 Pro 10.0.26200 (10.0.26200.8875/25H2/2025Update/HudsonValley2)
- **.NET SDK**: 10.0.302
- **.NET Runtime**: .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
- **BenchmarkDotNet**: v0.15.8

## Command

```
dotnet run -c Release --project benchmarks/Geoblitz.Benchmarks -- --filter "*GeoBenchmarks*"
```

Run with BenchmarkDotNet's **default job** (no `--job short`): 1 launch, auto-tuned invocation
count, heuristic warmup and auto-terminated measurement — the pasted summary has no `Job=` line
precisely because nothing was overridden. Every row's 99.9 % confidence margin is below 3 % of its
mean (worst row 2.49 %), so these numbers are quotable as measured facts. Total wall time ~4.5 min.
The dataset is the embedded GeoNames `cities1000` extract loaded by `GeoDatabase.LoadDefault()`
(170,584 cities);
all query benchmarks use the same Berlin query point (52.52, 13.405) unless the name says otherwise.

## Summary

```
BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8875/25H2/2025Update/HudsonValley2)
AMD Ryzen 5 3600X 3.80GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.302
  [Host]     : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
```

| Method                          | Mean             | Error           | StdDev          | Median           | Ratio | RatioSD | Allocated | Alloc Ratio |
|-------------------------------- |-----------------:|----------------:|----------------:|-----------------:|------:|--------:|----------:|------------:|
| Scalar_HaversineFullScan        | 7,145,274.115 ns |  91,452.1508 ns |  85,544.3986 ns | 7,146,799.219 ns | 1.000 |    0.02 |         - |          NA |
| Scalar_HaversineFullScanCollect | 6,621,386.120 ns | 130,050.9789 ns | 194,654.1977 ns | 6,571,652.344 ns | 0.927 |    0.03 |         - |          NA |
| Scalar_ChordFullScan            |   367,699.328 ns |   5,592.4511 ns |  10,226.1140 ns |   366,068.677 ns | 0.051 |    0.00 |         - |          NA |
| Simd_ChordFullScan              |    47,004.962 ns |     846.5560 ns |   1,070.6223 ns |    46,993.396 ns | 0.007 |    0.00 |         - |          NA |
| Grid_FindWithin100km            |     9,086.349 ns |     180.8439 ns |     488.9220 ns |     9,236.084 ns | 0.001 |    0.00 |         - |          NA |
| Grid_FindNearest10              |     1,077.659 ns |      21.1907 ns |      21.7613 ns |     1,077.815 ns | 0.000 |    0.00 |         - |          NA |
| Grid_FindNearest10_SparseOcean  |     4,189.976 ns |      81.5212 ns |      97.0452 ns |     4,173.481 ns | 0.001 |    0.00 |         - |          NA |
| Scalar_HaversineSinglePair      |         7.113 ns |       0.1769 ns |       0.2038 ns |         7.034 ns | 0.000 |    0.00 |         - |          NA |

## What each benchmark measures

| Benchmark | Metric | Work | Output |
|---|---|---|---|
| `Scalar_HaversineFullScan` | haversine (trig) | scalar loop over all cities | minimum distance |
| `Scalar_HaversineFullScanCollect` | haversine (trig) | scalar loop over all cities | every city within 100 km |
| `Scalar_ChordFullScan` | squared chord (trig-free) | scalar loop over all cities | every city within 100 km |
| `Simd_ChordFullScan` | squared chord (trig-free) | `Vector<float>` loop over all cities | every city within 100 km |
| `Grid_FindWithin100km` | squared chord (trig-free) | SIMD over grid candidate ranges only | every city within 100 km (top-1000) |
| `Grid_FindNearest10` | squared chord (trig-free) | SIMD + expanding-radius grid search | 10 nearest |
| `Grid_FindNearest10_SparseOcean` | as above, sparse query point | forces radius-expansion rounds | 10 nearest |
| `Scalar_HaversineSinglePair` | haversine (trig) | one pair | one distance |

## Speedup attribution

The three middle rows are deliberately output-identical (same metric semantics, same
`HitBuffer` result), so each factor isolates exactly one variable:

| Step | From → To | Factor | What it isolates |
|---|---|---|---|
| Trig elimination | `Scalar_HaversineFullScanCollect` 6.62 ms → `Scalar_ChordFullScan` 367.7 us | **18.0x** | replacing per-point `Sin`/`Cos`/`Atan2` with squared-chord distance on precomputed unit vectors |
| Vectorization | `Scalar_ChordFullScan` 367.7 us → `Simd_ChordFullScan` 47.0 us | **7.8x** | `Vector<float>` (8 lanes on this AVX2 host) over the same arithmetic — near-linear in lane count |
| Both combined | `Scalar_HaversineFullScanCollect` 6.62 ms → `Simd_ChordFullScan` 47.0 us | **140.9x** | the full "trig-free + SIMD" kernel vs the naive trig scan |
| Candidate pruning | `Simd_ChordFullScan` 47.0 us → `Grid_FindWithin100km` 9.09 us | **5.2x** | grid cell ranges cutting how much data the SIMD kernel touches |
| End to end | `Scalar_HaversineFullScan` 7.15 ms → `Grid_FindWithin100km` 9.09 us | **786x** | naive baseline vs the shipped query path |
| Nearest-k | `Scalar_HaversineFullScan` 7.15 ms → `Grid_FindNearest10` 1.08 us | **6,630x** | as above, plus bounded `TopK` selection instead of a full pass |

Earlier revisions of this file quoted a single "137x SIMD speedup" against the min-only haversine
baseline. That number conflated three independent variables (metric, vectorization, output); the
table above replaces it. Note the honest split: **most of the win is trig elimination (18x), not
vectorization (7.8x)** — vectorization is the smaller factor, though 7.8x on 8 lanes shows the
kernel is essentially perfectly vectorized.

## Observations

- **Zero allocation, all eight benchmarks.** Every row reports `-` in `Allocated`, i.e. under
  BenchmarkDotNet's `MemoryDiagnoser` no managed allocation is attributable to a single operation:
  the grid query path (`GeoDatabase.FindWithin` / `FindNearest`), the `TopK` heap, the candidate
  range buffers and `HitBuffer` all live on the stack or come from `ArrayPool`.
- **Trig is the dominant cost of the naive approach.** `Scalar_HaversineSinglePair` at 7.1 ns times
  170,584 cities predicts ~1.2 ms; the measured full scan is 6.6-7.1 ms, i.e. the isolated single-pair
  call benefits from constant folding and a hot cache that the full scan does not get.
- **Collecting hits is not what costs.** `Scalar_HaversineFullScanCollect` (6.62 ms) is marginally
  *faster* than the min-only `Scalar_HaversineFullScan` (7.15 ms), a 7 % difference outside both
  error bars but well below the factors being attributed. Whatever its cause (the two differ only in
  their branch and in the `HitBuffer` writes), it confirms that collecting matches into `HitBuffer`
  is not what the trig-vs-chord comparison above is measuring.
- **Grid pruning pays off exactly where expected.** `Grid_FindWithin100km` (9.09 us) beats the SIMD
  full scan (47.0 us) by 5.2x: the candidate ranges cut ~140 k points down to the few thousand in
  the latitude/longitude window, and the SIMD kernel then only has to touch those.
- **`FindWithin` after the M1 fix.** These are the first default-job numbers measured against the
  corrected candidate-range geometry *and* the bounded `TopK` selection (`FindWithin` no longer
  collects and sorts every match). 100 km / Berlin lands at 9.09 us versus 8.55 us +- 10.4 us in the
  old `ShortRun` smoke run — i.e. indistinguishable at that radius, where the match count is far
  below the 1000-result cap; the bounded selection is what pays at large radii, where the old
  collect-and-sort had to grow a buffer for every match (measured out-of-band at 500 km: ~800 us
  before, ~615 us after).
- **Nearest-neighbour cost depends on density.** `Grid_FindNearest10` (1.08 us) finds its 10
  neighbours inside the first 50 km radius round in dense Berlin. `Grid_FindNearest10_SparseOcean`
  (4.19 us) is ~3.9x slower: the sparse South Pacific point forces several radius-quadrupling rounds,
  each rescanning a larger candidate set.

## API hot path

BenchmarkDotNet covers the `Geoblitz.Geo` query path only. The allocation profile of the API
endpoints themselves is measured by `Geoblitz.Api.Tests/AllocationTests.cs`:

| Measurement | Value | What it covers |
|---|---|---|
| `/cities/nearest` hot path, in-process | **256 B / request** | cache-key composition + span query parsing + validation + `FindNearest` + `ServerTiming.Set` + `CityJson.WriteCities` + `BodyWriter.FlushAsync`, on one thread, exact per-thread counting |
| `/cities/nearest` end to end (TestServer) | ~101 KB / request | the above plus routing, output-cache store, TestServer and `HttpClient` — harness-dominated |
| `/cities/within` end to end (TestServer) | ~110 KB / request | as above, with a several-hundred-city body |
| cached replay, end to end (TestServer) | ~16-31 KB / request | output-cache hit, no handler execution |

The 256 B figure is the meaningful one: it is the three documented per-request strings (the
`X-Compute-Count` header value, the `GeoCacheKey` string, and the `Server-Timing` header value)
plus header bookkeeping, with **nothing proportional to the number of cities or bytes written**.
The TestServer figures are a coarse gross-regression tripwire, not a statement about Kestrel's
per-request cost.
