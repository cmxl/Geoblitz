# API Reference

[← Back to docs index](index.md)

Base URL for local development: `http://localhost:5235` (see
`src/HighPerf.Api/Properties/launchSettings.json`; `dotnet run -c Release --project src/HighPerf.Api`).

All responses are `application/json; charset=utf-8` unless noted. All six endpoints below
are `GET`-only and cached — see [Caching semantics](#caching-semantics).

## `GET /healthz`

No parameters. Not cached (no output-cache policy applied).

**Response**: `200 OK`, `text/plain`, body `ok`.

## `GET /distance`

Great-circle (Haversine) distance in kilometers between two points.

| Parameter | Type | Required | Range |
|---|---|---|---|
| `fromLat` | number | yes | `[-90, 90]` |
| `fromLon` | number | yes | `[-180, 180]` |
| `toLat` | number | yes | `[-90, 90]` |
| `toLon` | number | yes | `[-180, 180]` |

**Example**:

```
GET /distance?fromLat=52.52&fromLon=13.405&toLat=48.1374&toLon=11.5755
```

```json
{ "kilometers": 504.2863652346621 }
```

## `GET /cities/nearest`

The `count` nearest cities to a point, sorted ascending by distance.

| Parameter | Type | Required | Default | Range |
|---|---|---|---|---|
| `lat` | number | yes | — | `[-90, 90]` |
| `lon` | number | yes | — | `[-180, 180]` |
| `count` | integer | no | `5` | `[1, 100]` |

**Example**:

```
GET /cities/nearest?lat=52.52437&lon=13.41053&count=3
```

```json
{
  "count": 3,
  "cities": [
    { "name": "Berlin", "country": "DE", "population": 3426354, "lat": 52.52437, "lon": 13.41053, "distanceKm": 0 },
    { "name": "Mitte", "country": "DE", "population": 102338, "lat": 52.52003, "lon": 13.40489, "distanceKm": 0.615 },
    { "name": "Prenzlauer Berg", "country": "DE", "population": 148878, "lat": 52.53878, "lon": 13.42443, "distanceKm": 1.858 }
  ]
}
```

> **Data caveat**: the query point above is Berlin's own GeoNames coordinate
> (52.52437, 13.41053), not the commonly-cited landmark coordinate (52.5200, 13.4050). The
> embedded `cities1000` dataset also lists Berlin's boroughs (e.g. "Mitte", ~8 m from the
> landmark coordinate) as separate entries, which are genuinely closer to the landmark
> coordinate than the "Berlin" city record itself (~612 m away) — so nearest-city tests and
> examples deliberately query Berlin's own record coordinate to keep "Berlin" first. See
> `tests/HighPerf.Geo.Tests/FindNearestTests.cs` (`RealDataset_NearestToBerlin_IsBerlin`).

## `GET /cities/within`

All cities within `radiusKm` of a point with population at least `minPopulation`, sorted
ascending by distance.

| Parameter | Type | Required | Default | Range |
|---|---|---|---|---|
| `lat` | number | yes | — | `[-90, 90]` |
| `lon` | number | yes | — | `[-180, 180]` |
| `radiusKm` | number | yes | — | `(0, 500]` |
| `minPopulation` | integer | no | `0` | `>= 0` |

**Example**:

```
GET /cities/within?lat=48.1374&lon=11.5755&radiusKm=30&minPopulation=1000000
```

```json
{
  "count": 1,
  "cities": [
    { "name": "Munich", "country": "DE", "population": 1505005, "lat": 48.13743, "lon": 11.57549, "distanceKm": 0.003 }
  ]
}
```

Both `/cities/nearest` and `/cities/within` share the same response shape: a top-level
`count` and a `cities` array of `{ name, country, population, lat, lon, distanceKm }`.

## `GET /geohash/encode`

| Parameter | Type | Required | Default | Range |
|---|---|---|---|---|
| `lat` | number | yes | — | `[-90, 90]` |
| `lon` | number | yes | — | `[-180, 180]` |
| `precision` | integer | no | `9` | `[1, 12]` |

**Example**:

```
GET /geohash/encode?lat=57.64911&lon=10.40744&precision=11
```

```json
{ "geohash": "u4pruydqqvj" }
```

## `GET /geohash/decode`

| Parameter | Type | Required | Notes |
|---|---|---|---|
| `hash` | string | yes | 1-12 base32 characters (geohash alphabet) |

**Example**:

```
GET /geohash/decode?hash=u4pruydqqvj
```

```json
{
  "lat": 57.64911063015461,
  "lon": 10.407439693808556,
  "latError": 6.705522537231445e-7,
  "lonError": 6.705522537231445e-7
}
```

`latError`/`lonError` are the half-width of the decoded cell in degrees (precision bound).

## Error shape

All validation failures return `400 Bad Request` with `application/problem+json`:

```json
{ "title": "Invalid request", "status": 400, "detail": "fromLat must be a number in [-90, 90]" }
```

Unhandled exceptions are caught by the global exception handler and return
`500 Internal Server Error` with the same `application/problem+json` shape:

```json
{ "title": "Internal error", "status": 500, "detail": "An unexpected error occurred." }
```

## Caching semantics

Every endpoint above (except `/healthz`) uses ASP.NET Core's `OutputCache` middleware with
a shared `"Geo"` policy:

- **TTL**: 10 minutes (`Expire(TimeSpan.FromMinutes(10))`).
- **Cache key**: the default query-string vary is disabled (`SetVaryByQuery([])`) and
  replaced by a computed key (`GeoCacheKey.Compute`) that rounds every coordinate
  parameter (`lat`, `lon`, `fromLat`, `fromLon`, `toLat`, `toLon`) to 3 decimal degrees
  (~110 m buckets at the equator) and includes the exact `count` / `radiusKm` /
  `minPopulation` / `precision` / `hash` values. Two requests whose coordinates round to
  the same bucket and whose other parameters match exactly share one cache entry.
- **Observability**: every actual computation increments a process-wide counter and
  returns it as `X-Compute-Count`. Because `OutputCache` replays the entire stored
  response — including headers — on a hit, a cache hit returns the *same*
  `X-Compute-Count` value as the original miss (verified above: a request replayed from
  cache came back with `X-Compute-Count: 1` and an `Age: 24` header, matching the
  original response's `Date`). A different `X-Compute-Count` value between two requests
  proves they were computed independently (cache miss). See
  [performance-techniques.md, item 8](performance-techniques.md#8-output-caching-with-quantized-keys)
  for why this replaces the originally-planned `X-Cache` header.

## See also

- [Architecture](architecture.md) — request flow through the cache and into the geo index.
- [Performance techniques](performance-techniques.md) — caching, JSON writing, and query
  parsing techniques used by these endpoints.
- [Benchmarks](benchmarks.md) — measured query latency and allocation numbers.
