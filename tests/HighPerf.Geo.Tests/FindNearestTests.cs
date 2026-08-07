using System.Text;
using HighPerf.Geo;
using Xunit;

namespace HighPerf.Geo.Tests;

public class FindNearestTests
{
    [Fact]
    public void MatchesBruteForce_OnRandomData()
    {
        var rng = new Random(77);
        var pts = new (string, double, double, int)[5000];
        for (var i = 0; i < pts.Length; i++)
            pts[i] = ($"P{i}", rng.NextDouble() * 180 - 90, rng.NextDouble() * 360 - 180, 0);
        var db = GeoDatabase.Build(GeoDatabaseBuildTests.Cities(pts));

        Span<GeoHit> hits = stackalloc GeoHit[10];
        for (var q = 0; q < 20; q++)
        {
            double qLat = rng.NextDouble() * 180 - 90, qLon = rng.NextDouble() * 360 - 180;
            var n = db.FindNearest(qLat, qLon, 10, hits);
            Assert.Equal(10, n);

            var brute = Enumerable.Range(0, db.Count)
                .Select(i => (Idx: i, D: GeoMath.HaversineKm(qLat, qLon, db.GetLat(i), db.GetLon(i))))
                .OrderBy(t => t.D).Take(10).ToArray();

            for (var i = 0; i < 10; i++)
                Assert.True(Math.Abs(hits[i].DistanceKm - brute[i].D) < 1.5,
                    $"q{q} rank {i}: got {hits[i].DistanceKm}, brute {brute[i].D}");
        }
    }

    [Fact]
    public void KLargerThanDataset_ReturnsAll()
    {
        var db = GeoDatabase.Build(GeoDatabaseBuildTests.Cities(("A", 1, 1, 0), ("B", 2, 2, 0)));
        Span<GeoHit> hits = stackalloc GeoHit[10];
        Assert.Equal(2, db.FindNearest(0, 0, 10, hits));
    }

    [Fact]
    public void KZero_ReturnsZero()
    {
        var db = GeoDatabase.Build(GeoDatabaseBuildTests.Cities(("A", 1, 1, 0)));
        Span<GeoHit> hits = stackalloc GeoHit[4];
        Assert.Equal(0, db.FindNearest(0, 0, 0, hits));
    }

    [Fact]
    public void SparseRegion_StillFindsK_AcrossExpansions()
    {
        // nearest neighbors far beyond the initial 50 km radius
        var db = GeoDatabase.Build(GeoDatabaseBuildTests.Cities(
            ("Far1", 30, 30, 0), ("Far2", 35, 35, 0), ("Far3", -40, -40, 0)));
        Span<GeoHit> hits = stackalloc GeoHit[3];
        var n = db.FindNearest(0, 0, 3, hits);
        Assert.Equal(3, n);
        Assert.True(hits[0].DistanceKm <= hits[1].DistanceKm && hits[1].DistanceKm <= hits[2].DistanceKm);
    }

    [Fact]
    public void RealDataset_NearestToBerlin_IsBerlin()
    {
        var db = GeoDatabase.LoadDefault();
        Span<GeoHit> hits = stackalloc GeoHit[1];

        // NOTE: query point is the GeoNames coordinate for the "Berlin" city record itself
        // (52.52437, 13.41053), not the commonly-cited 52.5200/13.4050 landmark coordinate.
        // The embedded GeoNames cities1000 dataset also lists Berlin's boroughs (e.g. "Mitte",
        // ~8m from 52.5200/13.4050) which are genuinely closer than the "Berlin" record's own
        // point (~612m away) at that coordinate — see FindWithinTests.RealDataset_Berlin15km_FindsBerlinFirst
        // for the same rationale. Using Berlin's own coordinate keeps this a real smoke test.
        var n = db.FindNearest(52.52437, 13.41053, 1, hits);
        Assert.Equal(1, n);
        Assert.Equal("Berlin", Encoding.UTF8.GetString(db.GetNameUtf8(hits[0].Index)));
    }
}
