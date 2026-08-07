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
dotnet run -c Release --project benchmarks/HighPerf.Benchmarks -- --filter "*GeoBenchmarks*" --job short
```

Note: this was a smoke-run using BenchmarkDotNet's `short` (`ShortRun`) job preset
(`IterationCount=3, LaunchCount=1, WarmupCount=3`) as specified in the task brief, rather than
the default job — numbers are directional (confidence intervals are wide) but sufficient to
validate relative ordering and the zero-allocation claim.

## Summary

```
BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8875/25H2/2025Update/HudsonValley2)
AMD Ryzen 5 3600X 3.80GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1
WarmupCount=3
```

| Method                         | Mean             | Error            | StdDev        | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |-----------------:|-----------------:|--------------:|------:|--------:|----------:|------------:|
| Scalar_HaversineFullScan       | 6,664,077.604 ns | 1,618,644.477 ns | 88,723.366 ns | 1.000 |    0.02 |         - |          NA |
| Simd_ChordFullScan             |    48,699.134 ns |    25,540.029 ns |  1,399.935 ns | 0.007 |    0.00 |         - |          NA |
| Grid_FindWithin100km           |     8,550.883 ns |    10,404.613 ns |    570.312 ns | 0.001 |    0.00 |         - |          NA |
| Grid_FindNearest10             |       920.351 ns |     1,344.879 ns |     73.717 ns | 0.000 |    0.00 |         - |          NA |
| Grid_FindNearest10_SparseOcean |     2,866.194 ns |     3,761.993 ns |    206.208 ns | 0.000 |    0.00 |         - |          NA |
| Scalar_HaversineSinglePair     |         9.237 ns |        19.456 ns |      1.066 ns | 0.000 |    0.00 |         - |          NA |

## Observations

- **Zero-allocation proof**: `Grid_FindWithin100km` and `Grid_FindNearest10` both report `-`
  (0 B) in the `Allocated` column — the grid query path (`GeoDatabase.FindWithin` /
  `FindNearest`, `HitBuffer`/`TopK` backed by `ArrayPool`) allocates nothing on the managed heap
  per call. In fact all six benchmarks in this run show zero allocations.
- **SIMD vs scalar**: `Simd_ChordFullScan` (48.7 us) is ~137x faster than
  `Scalar_HaversineFullScan` (6.66 ms) — a full linear scan over the unit-vector arrays with
  `Vector<float>` chord-distance comparisons beats the naive per-point Haversine scan by more
  than two orders of magnitude, well past the "roughly an order of magnitude" expectation.
- **Grid vs SIMD full scan**: `Grid_FindWithin100km` (8.55 us) beats `Simd_ChordFullScan`
  (48.7 us) by ~5.7x — the grid-cell candidate-range pruning cuts the amount of data the SIMD
  kernel has to touch.
- **Nearest-neighbor**: `Grid_FindNearest10` (920 ns) is fast because Berlin sits in a
  dense region (few radius-expansion rounds needed). `Grid_FindNearest10_SparseOcean` (2.87 us)
  is ~3x slower, as expected, since the sparse South Pacific query point forces multiple radius
  doubling rounds before 10 neighbors are found.
