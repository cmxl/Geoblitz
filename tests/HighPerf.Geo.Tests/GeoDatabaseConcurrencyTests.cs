using HighPerf.Geo;
using Xunit;

namespace HighPerf.Geo.Tests;

/// <summary>Coverage gap: <see cref="GeoDatabase"/> is a singleton shared across every request in
/// the API host (see <c>Program.cs</c>: <c>builder.Services.AddSingleton(GeoDatabase.LoadDefault())</c>),
/// so <see cref="GeoDatabase.FindWithin"/> and <see cref="GeoDatabase.FindNearest"/> are called
/// concurrently by design under real load. All prior tests exercise them single-threaded. These
/// tests pin the assumption that the read-only shared state (X/Y/Z, CellStart, population, etc.)
/// plus the purely-local scratch buffers (stackalloc / <see cref="System.Buffers.ArrayPool{T}"/>)
/// make both methods safe to call from many threads at once, by comparing a large batch of
/// concurrently-computed results against sequentially-computed references for exact equality.</summary>
public class GeoDatabaseConcurrencyTests
{
    private static (double Lat, double Lon)[] BuildQueryGrid()
    {
        // deliberately includes duplicate/near-duplicate points (to force many threads through the
        // same grid cells at once) as well as high-latitude and antimeridian-adjacent points.
        var queries = new List<(double, double)>();
        for (var lat = -85.0; lat <= 85.0; lat += 7.5)
            for (var lon = -180.0; lon < 180.0; lon += 15.0)
                queries.Add((lat, lon));
        for (var i = 0; i < 20; i++) queries.Add((52.52437, 13.41053)); // Berlin, repeated
        queries.Add((89.9, 0));
        queries.Add((-89.9, 0));
        queries.Add((0, 179.99));
        queries.Add((0, -179.99));
        return [.. queries];
    }

    [Fact]
    public void FindNearest_ConcurrentCalls_MatchSequentialReferenceExactly()
    {
        var db = GeoDatabase.LoadDefault();
        var queries = BuildQueryGrid();
        const int k = 8;

        var expected = new GeoHit[queries.Length][];
        for (var i = 0; i < queries.Length; i++)
        {
            var buf = new GeoHit[k];
            var n = db.FindNearest(queries[i].Lat, queries[i].Lon, k, buf);
            expected[i] = buf.AsSpan(0, n).ToArray();
        }

        var actual = new GeoHit[queries.Length][];
        Parallel.For(0, queries.Length,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            i =>
            {
                var buf = new GeoHit[k];
                var n = db.FindNearest(queries[i].Lat, queries[i].Lon, k, buf);
                actual[i] = buf.AsSpan(0, n).ToArray();
            });

        for (var i = 0; i < queries.Length; i++)
            Assert.Equal(expected[i], actual[i]);
    }

    [Fact]
    public void FindWithin_ConcurrentCalls_MatchSequentialReferenceExactly()
    {
        var db = GeoDatabase.LoadDefault();
        var queries = BuildQueryGrid();
        const double radiusKm = 200;

        var expected = new GeoHit[queries.Length][];
        for (var i = 0; i < queries.Length; i++)
        {
            var buf = new GeoHit[500];
            var n = db.FindWithin(queries[i].Lat, queries[i].Lon, radiusKm, 0, buf);
            expected[i] = buf.AsSpan(0, n).ToArray();
        }

        var actual = new GeoHit[queries.Length][];
        Parallel.For(0, queries.Length,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            i =>
            {
                var buf = new GeoHit[500];
                var n = db.FindWithin(queries[i].Lat, queries[i].Lon, radiusKm, 0, buf);
                actual[i] = buf.AsSpan(0, n).ToArray();
            });

        for (var i = 0; i < queries.Length; i++)
            Assert.Equal(expected[i], actual[i]);
    }

    [Fact]
    public void FindWithinAndFindNearest_InterleavedOnSharedDatabase_DoNotCorruptEachOther()
    {
        // both entry points hit the same read-only arrays at once, from many threads, using
        // different code paths (bounded-heap-with-filter vs progressive-radius-expansion).
        var db = GeoDatabase.LoadDefault();
        const int iterations = 400;
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, iterations,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            i =>
            {
                var lat = -80.0 + (i * 37 % 160);
                var lon = -170.0 + (i * 53 % 340);

                if (i % 2 == 0)
                {
                    Span<GeoHit> hits = stackalloc GeoHit[10];
                    var n = db.FindNearest(lat, lon, 10, hits);
                    for (var j = 1; j < n; j++)
                        if (hits[j - 1].DistanceKm > hits[j].DistanceKm)
                            errors.Add($"FindNearest({lat},{lon}) not ascending at rank {j}");
                }
                else
                {
                    Span<GeoHit> hits = stackalloc GeoHit[10];
                    var n = db.FindWithin(lat, lon, 100, 0, hits);
                    for (var j = 1; j < n; j++)
                        if (hits[j - 1].DistanceKm > hits[j].DistanceKm)
                            errors.Add($"FindWithin({lat},{lon}) not ascending at rank {j}");
                }
            });

        Assert.Empty(errors);
    }
}
