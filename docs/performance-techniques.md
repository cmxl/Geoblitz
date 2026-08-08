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
runs in 9.09 us vs 47.0 us for a full unstructured SIMD scan of the array — 0 B allocated
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

**Measured — 18.0x, the single largest factor in the suite**: `Scalar_ChordFullScan`
(367.7 us) vs `Scalar_HaversineFullScanCollect` (6.62 ms). Both are plain scalar loops over
all 170,584 cities producing the same result set; the only difference is squared-chord
distance on precomputed unit vectors instead of `Sin`/`Cos`/`Atan2` per point. The benchmark
pair exists specifically so this factor is *not* entangled with vectorization (technique 3)
— together they are 140.9x (`benchmarks/RESULTS.md`, "Speedup attribution").

## 3. `Vector<float>` SIMD scans with scalar tails

**What**: `ChordKernel.ScanWithin` / `ScanNearest` process `Vector<float>.Count` cities at
a time using `System.Numerics.Vector<float>` — subtracting the query vector, squaring,
summing, and comparing against the max chord-squared threshold as one SIMD operation —
then fall back to an ordinary scalar loop for the remaining `< Vector<float>.Count` cities
at the end of a range.

**Why**: exploits whatever SIMD width the CPU provides at runtime (`Vector.IsHardwareAccelerated`)
without requiring cell sizes to be padded to a vector-width multiple; the scalar tail keeps
results correct for arbitrarily-sized grid-cell ranges.

**Where**: `src/HighPerf.Geo/ChordKernel.cs` — `ScanNearest`, `ScanWithinTopK` (used by the
query path) and `ScanWithin` (the unbounded collect-everything variant, now only exercised by
tests and the full-scan benchmark).

**Measured — 7.8x**: `Simd_ChordFullScan` (47.0 us) vs `Scalar_ChordFullScan` (367.7 us):
identical arithmetic and output, `Vector<float>` versus a scalar loop. That is near-linear in
the 8 `float` lanes this AVX2 host provides, i.e. the kernel is essentially perfectly
vectorized — but it is the *smaller* of the two factors behind the old "137x" headline; trig
elimination (technique 2) contributes 18.0x of it. Combined with grid pruning (technique 4)
the kernel delivers the 1.08 us `Grid_FindNearest10` result (`benchmarks/RESULTS.md`).

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

**Measured**: `Grid_FindWithin100km` (9.09 us) beats a full `Simd_ChordFullScan` of all
cities (47.0 us) by ~5.2x — grid pruning cuts how much data the SIMD kernel has to touch.
End to end that is ~786x the naive scalar-haversine baseline for a radius query, and ~6,630x
for `FindNearest` k=10 (`benchmarks/RESULTS.md`).

## 5. Bounded `TopK` selection on `stackalloc`/`ArrayPool` storage

**What**: both queries select through `TopK`, a `ref struct` bounded max-heap over
`Span<float>`/`Span<int>`. `FindNearest` backs it with `stackalloc` (capped at 128 results).
`FindWithin` sizes it to the caller's result span: up to 256 hits it is `stackalloc`'d, above
that it comes from `ArrayPool<float>`/`ArrayPool<int>.Shared` and is returned in a `finally`.

**Why**: two things at once. Zero managed-heap allocations per query, independent of how many
cities match or how large the dataset is — *and* bounded work: because `TopK` exposes its
current k-th distance as `Threshold`, `ChordKernel.ScanWithinTopK` prunes candidates against
`min(radius, threshold)` and only the retained k hits are ever sorted. A 500 km query matching
tens of thousands of cities therefore costs O(candidates + matches·log k) with most candidates
rejected by one comparison, instead of collecting every match into a growable buffer and
sorting all of it. The `minPopulation` filter is applied inside the scan, *before* selection,
so the retained set is "the k closest cities that pass the filter" — identical semantics to
filtering a fully sorted match list and truncating it.

**Where**: `src/HighPerf.Geo/TopK.cs`; `ChordKernel.ScanWithinTopK`/`ScanNearest`; used from
`GeoDatabase.FindWithin` and `GeoDatabase.FindNearest`. `src/HighPerf.Geo/HitBuffer.cs` (the
older growable collect-everything buffer) survives only for `ChordKernel.ScanWithin`, which is
now exercised by tests and the full-scan benchmarks rather than by the query path.

**Measured**: `benchmarks/RESULTS.md` — `Grid_FindWithin100km` and `Grid_FindNearest10`
both report `-` (0 B) in the `Allocated` column; in fact all eight benchmarks in the run,
including the full-scan baselines, show zero managed allocations. The bounded selection is
what pays at large radii (measured out-of-band at 500 km around Berlin: ~800 us with
collect-and-sort, ~615 us with bounded `TopK`, still 0 B); at 100 km, where matches stay far
below the 1000-result cap, it is indistinguishable from the old path.

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

**Measured**: `tests/HighPerf.Api.Tests/AllocationTests.cs` replays the `/cities/nearest`
handler's exact synchronous sequence — `GeoCacheKey.Compute` + these span parses + validation
+ `FindNearest` + `CityJson.WriteCities` + `BodyWriter.FlushAsync` — against a real
`HttpResponse` on one thread and measures **200 B per request** with
`GC.GetAllocatedBytesForCurrentThread()`, asserted against a 512 B ceiling. That 200 B is the
two documented strings (the `X-Compute-Count` value, the cache-key string) plus header
bookkeeping: nothing scales with the number of parameters, cities or response bytes. Reaching
for `HttpRequest.Query` instead would materialize a `Dictionary<string, StringValues>` per
request and trip that ceiling. Also exercised end-to-end by the k6 scripts in `loadtest/`
(see [benchmarks.md](benchmarks.md)) and by the endpoint tests in `tests/HighPerf.Api.Tests`.

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

**Declaring `Content-Length` instead of chunking**: the exact body size is known before the
response starts, so `WriteCities` sets `response.ContentLength = writer.BytesCommitted +
writer.BytesPending` immediately before its final `writer.Flush()`. Both terms are needed:
`Utf8JsonWriter.Grow()` `Advance()`s the `PipeWriter` whenever the current buffer fills, which
`/cities/within` routinely does, moving those bytes out of `BytesPending` into
`BytesCommitted`. `Advance()` does not start the response, so the headers are still mutable at
that point. Without this, Kestrel saw an advanced body with no declared length and fell back to
`Transfer-Encoding: chunked` on every compute request; the output cache stores headers verbatim,
so the replay inherited the chunked framing too. Measured against a Release Kestrel build: a
`/cities/nearest` miss now carries `Content-Length: 581` and a 500 km `/cities/within` miss
`Content-Length: 105661`, with no `Transfer-Encoding` header on either.

Note this covers the two `/cities/*` endpoints only. `/distance` and `/geohash/*` serialize via
`Results.Json`, which does not set `Content-Length`, so those still go out chunked — a small,
deliberate gap: they are not the endpoints whose framing overhead matters relative to their
body size, and changing them would mean hand-rolling their serialization too.

**A useful side property**: `Utf8JsonWriter` only advances the pipe on flush, so as long as the
body still fits the current buffer, an exception thrown mid-serialization has advanced nothing
and `UseExceptionHandler` can still emit a clean 500 `application/problem+json`. (For a body
large enough to have already grown its buffer, part of it is committed to the pipe — but not
flushed to the socket — so this guarantee is a property of small responses, not of all of them.)

**Where**: `src/HighPerf.Api/ApiTypes.cs` (`AppJsonContext`); `src/HighPerf.Api/CityJson.cs`
(`PooledJson`, `CityJson.WriteCities`).

**Measured**: contributes to the zero-allocation results in `benchmarks/RESULTS.md` for the
grid-query benchmarks and to the 200 B/request hot path of technique 6.
`tests/HighPerf.Api.Tests/ResponseFramingTests.cs` asserts the declared `Content-Length`
equals the exact body size for both a small body and one spanning several writer buffers. The
absence of chunked framing itself is not automatable in-process (TestServer has no HTTP/1.1
framing layer and reports a buffered length to the client regardless), so it was verified by hand
against a Release Kestrel build — the numbers quoted above.

## 8. Output caching with quantized keys

**What**: the five geo endpoints use ASP.NET Core's `OutputCache` middleware with a custom
`"Geo"` policy: a 10-minute TTL, `SetVaryByQuery([])` (disable the default raw-query-string
vary), and a `VaryByValue` callback (`GeoCacheKey.Compute`) that builds the key from the raw
query string — coordinates quantized to 3 decimal degrees (~110 m buckets at the equator),
`count`/`radiusKm`/`minPopulation`/`precision` canonicalized, `hash` verbatim. `/healthz` is
deliberately *not* cached. A per-process `ComputeCounter` is incremented on every actual
computation and written to an `X-Compute-Count` response header before the handler returns its
body.

**Why the key encodes validity, not just values**: the cache lookup happens in middleware,
*before* any handler can reject bad input. A key that maps "parameter absent" and "parameter
present but garbage" to the same slot therefore lets a cached 200 be replayed for a request the
API is required to answer with 400 — `count=abc` or `count=0` inheriting the answer computed
for the default `count=5` (M2 review, C1). `GeoCacheKey` fixes this structurally: the key opens
with a fixed-width validity mask, one character per participating parameter (`-` absent, `v`
valid, `x` invalid), followed by one **length-prefixed** field per parameter
(`|<length>:<token>`). The length prefix is what makes the mask trustworthy: a query value may
legally contain the field separator (nothing percent-decodes it and Kestrel does not reject it),
so with plain `a|b|c` fields one parameter's raw text could shift every following field boundary
and reproduce another request's key *with the same mask* — including two valid requests aliasing
onto each other's results. With explicit lengths the key is uniquely decodable, so two equal keys
for one path necessarily agree on every parameter's presence, validity and value, and a
valid/invalid pair cannot collide however the invalid value is spelled. Invalid
values additionally go into the key raw; valid ones go in canonically, so `count=3`/`count=03`
and `lat=47.401`/`lat=47.4010` still share one entry. Quantization is applied only *after* the
range check — otherwise `lat=90.0004` would round straight into `lat=90`'s valid bucket. The
predicate used here is a deliberately conservative superset of the handlers' own validation:
marking something invalid that a handler would accept only costs that request its own cache
entry, whereas the reverse reopens the bypass. Since the default output-cache policy stores
only 200s, distinct keys are all the fix needs — a 400 is never written to the cache.

**Related validation fix**: the handlers' range guards were written as `x is < -90 or > 90`,
which every comparison against `NaN` answers "false" — and `double.TryParse` accepts the literal
`"NaN"`. So `lat=NaN` used to pass validation and be answered (and cached) as an empty 200, while
`/distance?fromLat=NaN` reached `Utf8JsonWriter.WriteNumber`, which rejects non-finite doubles and
turned it into a 500. The guards now read `x is not (>= -90 and <= 90)`, which rejects NaN and
both infinities, and the cache key marks non-finite values invalid for the same reason.

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
`X-Compute-Count` replay behavior. `tests/HighPerf.Api.Tests/CacheValidationTests.cs` covers
the validity dimension: for every invalid parameter shape it issues the *valid* request first
(populating the cache) and then the invalid variant, asserting a 400; plus the reverse order
(a 400 must not poison the valid variant) and that equivalent spellings still share one entry.

**Cost**: one string per request, no boxing — the key is composed in a stack buffer with
`TryFormat`, and the only allocation is the final `string`. Included in the 200 B/request
figure of technique 6.

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
  microseconds (`Grid_FindNearest10`: 1.08 us; `Grid_FindWithin100km`: 9.09 us,
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
