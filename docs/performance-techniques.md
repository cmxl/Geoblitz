# Performance Techniques Catalog

[← Back to docs index](index.md)

This is the core deliverable of the project: ten concrete techniques used to make the
geo queries fast and (mostly) allocation-free, each with what it is, why it exists, where
it lives in the code, and what was actually measured. Numbers are from
[`benchmarks/RESULTS.md`](../benchmarks/RESULTS.md) unless noted otherwise; see
[benchmarks.md](benchmarks.md) for how to reproduce them.

## 1. Struct-of-arrays + cell-order permutation

**What**: `GeoDatabase` stores cities as parallel primitive arrays (`X`, `Y`, `Z`, `_lat`,
`_lon`, `_population`, `_country`, a UTF-8 name blob + offsets) rather than an array of
city objects. At build time, a counting sort permutes every row into grid-cell order, so
all cities belonging to the same spatial cell end up in one contiguous index range across
every array.

**Why**: cache locality and no pointer chasing. A query only ever needs to touch a handful
of contiguous slices (one per candidate grid cell) instead of dereferencing scattered
objects, and each slice is directly SIMD-scannable.

**Where**: `src/HighPerf.Geo/GeoDatabase.cs`, private constructor — the counting-sort
(`cellOf`, `counts`, `CellStart`) and permutation (`order[]`, the `for (var d = 0; d < Count; d++)` copy loop).

**Measured**: this layout is what makes the grid-narrowed scan possible; `Grid_FindWithin100km`
runs in 8.55 us vs 48.7 us for a full unstructured SIMD scan of the array — 0 B allocated
in both cases (`benchmarks/RESULTS.md`).

## 2. Unit-vector chord distance

**What**: `GeoMath.ToUnitVector` converts each city's (lat, lon) into a 3D unit vector
`(X, Y, Z)` once, at build time. Distance comparisons then use squared Euclidean ("chord")
distance between unit vectors, converted to/from kilometers via `KmToChordSq` /
`ChordSqToKm`, instead of Haversine trig per candidate.

**Why**: zero per-point trigonometry on the hot (query) path — only the single query point
needs `ToUnitVector` per request; every stored city's vector was precomputed once at
startup. Chord-squared distance is monotonic with great-circle distance, so radius checks,
top-k comparisons, and sorting are all correct without ever calling `asin`/`sqrt` inside
the scan loop. This is a refinement of the original spec, which proposed precomputing only
`SinLat`/`CosLat` per city — that captures latitude but still needs a per-candidate
longitude-difference cosine at scan time. Full unit vectors fold both lat and lon into a
single 3-way subtract-square-add, which vectorizes cleanly (technique 3) and needs no
per-candidate trig at all.

**Where**: `src/HighPerf.Geo/GeoMath.cs` (`ToUnitVector`, `KmToChordSq`, `ChordSqToKm`);
vectors built once in `GeoDatabase`'s constructor and consumed by `ChordKernel`.

**Measured**: `Simd_ChordFullScan` (48.7 us) vs `Scalar_HaversineFullScan` (6.66 ms) — a
full linear scan using chord distance is ~137x faster than the naive per-point Haversine
scan (`benchmarks/RESULTS.md`).

## 3. `Vector<float>` SIMD scans with scalar tails

**What**: `ChordKernel.ScanWithin` / `ScanNearest` process `Vector<float>.Count` cities at
a time using `System.Numerics.Vector<float>` — subtracting the query vector, squaring,
summing, and comparing against the max chord-squared threshold as one SIMD operation —
then fall back to an ordinary scalar loop for the remaining `< Vector<float>.Count` cities
at the end of a range.

**Why**: exploits whatever SIMD width the CPU provides at runtime (`Vector.IsHardwareAccelerated`)
without requiring cell sizes to be padded to a vector-width multiple; the scalar tail keeps
results correct for arbitrarily-sized grid-cell ranges.

**Where**: `src/HighPerf.Geo/ChordKernel.cs`, `ScanWithin` and `ScanNearest`.

**Measured**: this kernel is what drives the ~137x `Simd_ChordFullScan` vs
`Scalar_HaversineFullScan` gap, and — combined with grid pruning (technique 4) — the 920 ns
`Grid_FindNearest10` result (`benchmarks/RESULTS.md`).

## 4. CSR grid index → contiguous candidate ranges

**What**: the cell-order permutation (technique 1) plus a `CellStart[]` offset array forms
a CSR-style (compressed sparse row) index over a lat/lon grid. `GetCandidateRanges`
computes the minimal set of contiguous `[start, end)` index ranges that cover a query's
bounding box for a given radius — including antimeridian wraparound as two segments, and a
whole-row shortcut when the longitude window would otherwise exceed 360°.

**Why**: turns "cities within N km" into a handful of array-slice lookups instead of a scan
over all 170,584 cities; the SIMD kernel (technique 3) then only has to touch what's
actually in range.

**Where**: `src/HighPerf.Geo/GeoDatabase.cs`, `CellStart` field and `GetCandidateRanges` method.

**Measured**: `Grid_FindWithin100km` (8.55 us) beats a full `Simd_ChordFullScan` of all
cities (48.7 us) by ~5.7x — grid pruning cuts how much data the SIMD kernel has to touch
(`benchmarks/RESULTS.md`).

## 5. `stackalloc` top-k heap + `ArrayPool` hit buffers

**What**: `FindNearest` uses `TopK`, a `ref struct` bounded max-heap backed by
`stackalloc`'d `Span<float>`/`Span<int>` (capped at 128 results). `FindWithin` uses
`HitBuffer`, a growable buffer backed by `ArrayPool<int>`/`ArrayPool<float>.Shared`,
disposed back to the pool when the query completes.

**Why**: zero managed-heap allocations per query, independent of how many cities match or
how large the dataset is.

**Where**: `src/HighPerf.Geo/TopK.cs`; `src/HighPerf.Geo/HitBuffer.cs`; used from
`GeoDatabase.FindNearest` and `GeoDatabase.FindWithin` respectively.

**Measured**: `benchmarks/RESULTS.md` — `Grid_FindWithin100km` and `Grid_FindNearest10`
both report `-` (0 B) in the `Allocated` column; in fact all six benchmarks in the run,
including the two full-scan baselines, show zero managed allocations.

## 6. Span-based query parsing

**What**: `QueryParams.TryGetDouble` / `TryGetInt` / `TryGetRaw` parse parameters directly
out of the raw `ReadOnlySpan<char>` from `ctx.Request.QueryString.Value`, splitting on `&`
and `=` by hand, instead of going through ASP.NET Core's default query-string
materialization (`HttpRequest.Query`, which builds a `Dictionary<string, StringValues>`
plus per-value string/array allocations).

**Why**: every endpoint handler reads 2-4 query parameters per request; parsing straight
off the span avoids allocating a `StringValues` collection on every single request,
regardless of hit/miss on the output cache.

**Where**: `src/HighPerf.Api/QueryParams.cs`; called from every endpoint in
`src/HighPerf.Api/Program.cs` (e.g. `QueryParams.TryGetDouble(qs, "lat", out var lat)`).

**Measured**: not isolated as its own BenchmarkDotNet entry (it is part of the request
path, not the `HighPerf.Geo` library under benchmark); exercised end-to-end by the k6
scripts in `loadtest/` (see [benchmarks.md](benchmarks.md)) and by the endpoint tests in
`tests/HighPerf.Api.Tests`.

## 7. Source-generated JSON + pooled `Utf8JsonWriter` → `PipeWriter`

**What**: response DTOs (`DistanceResponse`, `GeohashEncodeResponse`,
`GeohashDecodeResponse`, `ApiProblem`) are serialized through a source-generated
`JsonSerializerContext` (`AppJsonContext`, `[JsonSerializable]` per type) rather than
runtime reflection. City list responses (`/cities/nearest`, `/cities/within`) skip DTOs
entirely: `CityJson.WriteCities` rents a `[ThreadStatic]`-cached `Utf8JsonWriter` (via
`PooledJson.Rent`/`Return`) and writes tokens directly into `HttpResponse.BodyWriter`
(a `PipeWriter`), including the UTF-8 city-name bytes straight from the name blob with no
intermediate `string`.

**Why**: avoids per-request `Utf8JsonWriter` allocation, avoids reflection-based
serialization overhead, and avoids materializing a `string` per city name just to
re-encode it as UTF-8.

**Where**: `src/HighPerf.Api/ApiTypes.cs` (`AppJsonContext`); `src/HighPerf.Api/CityJson.cs`
(`PooledJson`, `CityJson.WriteCities`).

**Measured**: contributes to the zero-allocation results in `benchmarks/RESULTS.md` for the
grid-query benchmarks and to the low per-request overhead exercised by `loadtest/nearest.js`
and `loadtest/within.js`.

## 8. Output caching with quantized keys

**What**: all six endpoints use ASP.NET Core's `OutputCache` middleware with a custom
`"Geo"` policy: a 10-minute TTL, `SetVaryByQuery([])` (disable the default raw-query-string
vary), and a `VaryByValue` callback (`GeoCacheKey.Compute`) that builds the cache key from
each numeric parameter rounded to 3 decimal degrees (~110 m buckets at the equator) plus
the exact `count`/`radiusKm`/`minPopulation`/`precision`/`hash` values. A per-process
`ComputeCounter` is incremented on every actual computation and written to an
`X-Compute-Count` response header before the handler returns its body.

**Why — the hit-rate insight**: real client coordinates rarely repeat to the 15th decimal
place GPS reports, but rounding to ~110 m turns "nearby" requests (e.g. a user's phone
sending slightly jittered coordinates) into the same cache key, multiplying the hit rate
for hot areas without materially changing the answer.

**Why `X-Compute-Count` instead of the spec's `X-Cache`**: ASP.NET Core's `OutputCache`
replays the *entire* stored response — including headers — verbatim on a cache hit. An
explicit `X-Cache: HIT/MISS` header set inside the handler would only ever be written on a
miss (the handler doesn't run on a hit), so it can't reflect the real cache state. Instead,
`X-Compute-Count` is set on every actual computation; because a cache hit replays the
stored header untouched, two requests mapping to the same quantized key return the *same*
`X-Compute-Count` value — proving a cache hit — while a genuinely new computation always
returns a strictly higher one. Tests assert exactly this in
`tests/HighPerf.Api.Tests/CachingTests.cs`.

**Where**: `src/HighPerf.Api/Program.cs` (`AddOutputCache` policy registration,
`.CacheOutput("Geo")` on each endpoint); `src/HighPerf.Api/GeoCacheKey.cs`;
`src/HighPerf.Api/ComputeCounter.cs`.

**Measured**: `tests/HighPerf.Api.Tests/CachingTests.cs` — `IdenticalRequests_SecondIsServedFromCache`,
`NearbyCoordinates_SameQuantizedBucket_ShareCacheEntry`, `DifferentCount_IsACacheMiss`,
`DifferentBucket_IsACacheMiss` all pass, verifying both the quantization and the
`X-Compute-Count` replay behavior.

## 9. Host tuning

**What**: `WebApplication.CreateSlimBuilder` (trimmed-down default host, no unused
features); `<ServerGarbageCollection>true</ServerGarbageCollection>` and
`<TieredPGO>true</TieredPGO>` in `src/HighPerf.Api/HighPerf.Api.csproj`;
`<InvariantGlobalization>true</InvariantGlobalization>` to skip ICU loading; and
`builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false)` to drop the `Server`
response header.

**Why**: `CreateSlimBuilder` and `InvariantGlobalization` reduce startup work and memory
footprint for an API that does no culture-sensitive formatting; Server GC favors
throughput for a request-heavy workload; TieredPGO lets the JIT specialize hot methods
(like the SIMD scan kernels) based on observed runtime behavior; dropping the `Server`
header removes a small, useless per-response write and a minor information-disclosure
surface.

**Where**: `src/HighPerf.Api/Program.cs` (`CreateSlimBuilder`, `ConfigureKestrel`);
`src/HighPerf.Api/HighPerf.Api.csproj` (`ServerGarbageCollection`, `InvariantGlobalization`,
`TieredPgo` properties).

**Measured**: not isolated as a standalone benchmark (host-level settings affect the whole
process, not a single BenchmarkDotNet iteration); reflected in the overall throughput
targets exercised by `loadtest/mixed.js` (see `loadtest/README.md` for the k6 performance
thresholds: p95 < 20 ms, p99 < 50 ms, error rate < 0.1%).

## 10. What we deliberately did NOT do

- **Redis L2 cache**: the in-process `OutputCache` (technique 8) already serves quantized
  queries in-memory; adding a Redis hop in front of a computation that itself costs
  microseconds (`Grid_FindNearest10`: 920 ns; `Grid_FindWithin100km`: 8.55 us,
  `benchmarks/RESULTS.md`) would make the *cache-hit* path slower than the *cache-miss*
  path — the network round trip dominates. Redis remains the right call for multi-instance
  deployments needing a shared cache, which this single-process showcase doesn't need.
- **Native AOT**: AOT compilation loses dynamic PGO (profile-guided optimization performed
  by the JIT at runtime based on actual call patterns), which technique 9 relies on for the
  hot SIMD kernels. AOT's startup-time win doesn't offset that loss for a long-running API
  process — it would matter more for a CLI or short-lived function.
- **A FusionCache sub-result layer**: this project has no evidence of expensive
  sub-computations being repeated across different top-level queries (each query's grid
  ranges and SIMD scan are already cheap and mostly independent per request) — adding a
  caching layer without a measured, repeated bottleneck to justify it would be premature.
  Revisit if profiling ever shows the same sub-computation (e.g. `GetCandidateRanges` for a
  popular region) recurring often enough across distinct cache-key buckets to be worth the
  added complexity.

## See also

- [Architecture](architecture.md) — how these techniques compose end-to-end for a request.
- [API reference](api.md) — the caching semantics (item 8) as seen from the client side.
- [Benchmarks](benchmarks.md) — full methodology and results.
