using System.Text;
using HighPerf.Geo;
using Xunit;

namespace HighPerf.Geo.Tests;

public class FindNearestTests
{
    /// <summary>Asserts the k returned neighbours are the true k nearest: distances match the
    /// haversine reference tightly, and the index SET matches whenever rank k is unambiguous
    /// (i.e. the k-th and (k+1)-th brute-force distances are clearly separated).</summary>
    private static void AssertNearestMatchesBruteForce(GeoDatabase db, double qLat, double qLon, int k)
    {
        Span<GeoHit> hits = stackalloc GeoHit[k];
        var n = db.FindNearest(qLat, qLon, k, hits);
        Assert.Equal(Math.Min(k, db.Count), n);

        var brute = Enumerable.Range(0, db.Count)
            .Select(i => (Idx: i, D: GeoMath.HaversineKm(qLat, qLon, db.GetLat(i), db.GetLon(i))))
            .OrderBy(t => t.D).ToArray();

        for (var i = 0; i < n; i++)
        {
            if (i > 0) Assert.True(hits[i - 1].DistanceKm <= hits[i].DistanceKm, "results not ascending");
            Assert.True(Math.Abs(hits[i].DistanceKm - brute[i].D) < 0.01,
                $"({qLat}, {qLon}) rank {i}: got {hits[i].DistanceKm} km, brute force {brute[i].D} km " +
                $"(index {hits[i].Index} vs {brute[i].Idx})");
        }

        // identity check when the k-th place is not a near-tie with the (k+1)-th
        if (n < brute.Length && brute[n].D - brute[n - 1].D > 0.01)
        {
            var expected = new HashSet<int>();
            for (var i = 0; i < n; i++) expected.Add(brute[i].Idx);
            for (var i = 0; i < n; i++)
                Assert.True(expected.Contains(hits[i].Index),
                    $"({qLat}, {qLon}) rank {i}: index {hits[i].Index} is not in the true k-nearest set");
        }
    }

    [Fact]
    public void MatchesBruteForce_OnRandomData()
    {
        var rng = new Random(77);
        var pts = new (string, double, double, int)[5000];
        for (var i = 0; i < pts.Length; i++)
        {
            var u = rng.NextDouble() * 2 - 1; // area-uniform latitude, not uniform-in-degrees
            pts[i] = ($"P{i}", Math.Asin(u) * 180.0 / Math.PI, rng.NextDouble() * 360 - 180, 0);
        }
        var db = GeoDatabase.Build(GeoDatabaseBuildTests.Cities(pts));

        for (var q = 0; q < 60; q++)
        {
            // a third of the queries deliberately sit at high |lat|, a third near the antimeridian
            var qLat = (q % 3) switch
            {
                0 => 84.0 + rng.NextDouble() * 6.0,
                1 => -90.0 + rng.NextDouble() * 6.0,
                _ => rng.NextDouble() * 180 - 90,
            };
            var qLon = q % 3 == 1 ? 180.0 - rng.NextDouble() : rng.NextDouble() * 360 - 180;
            AssertNearestMatchesBruteForce(db, qLat, qLon, 10);
        }
    }

    [Fact]
    public void NearPole_HeapFillsInsideTruncatedWindow_StillReturnsTrueNearest()
    {
        // Regression for the longitude-window defect. From (88, 0):
        //   Shadow (84, 100)  -> 738.9 km  (outside the old +-82.5 deg window computed for row 84..85)
        //   Decoy  (82, 59)   -> 797.8 km  (inside the old +-59.0 deg window computed for row 82..83)
        // Both are inside the third progressive radius (800 km), so the k=1 heap filled with Decoy and
        // the "kth <= radius" termination fired before Shadow was ever a candidate.
        var db = GeoDatabase.Build(GeoDatabaseBuildTests.Cities(
            ("Shadow", 84.0, 100.0, 0), ("Decoy", 82.0, 59.0, 0)));

        Span<GeoHit> hits = stackalloc GeoHit[1];
        var n = db.FindNearest(88, 0, 1, hits);
        Assert.Equal(1, n);
        Assert.Equal("Shadow", Encoding.UTF8.GetString(db.GetNameUtf8(hits[0].Index)));
        Assert.InRange(hits[0].DistanceKm, 735, 742);
    }

    [Fact]
    public void NearPole_DenseCap_MatchesBruteForce()
    {
        var pts = new List<(string, double, double, int)>();
        var n = 0;
        for (var lat = 80.0; lat <= 89.9; lat += 0.5)
            for (var lon = -180.0; lon < 180.0; lon += 5.0)
            {
                pts.Add(($"N{n++}", lat, lon, 0));
                pts.Add(($"S{n++}", -lat, lon, 0));
            }
        var db = GeoDatabase.Build(GeoDatabaseBuildTests.Cities(pts.ToArray()));

        foreach (var qLat in new[] { 82.0, 85.0, 88.0, 89.99, -85.0, -88.0 })
            foreach (var qLon in new[] { 0.0, 179.5, -100.0 })
                foreach (var k in new[] { 1, 5, 20 })
                    AssertNearestMatchesBruteForce(db, qLat, qLon, k);
    }

    [Fact]
    public void RealDataset_HighLatitude_MatchesBruteForce()
    {
        // Reported repro: FindNearest(89.9, 0, 5) returned Belush'ya Guba @ 2046 km as rank 5 where
        // brute force says Khatanga @ 2006 km.
        var db = GeoDatabase.LoadDefault();
        AssertNearestMatchesBruteForce(db, 89.9, 0, 5);
        AssertNearestMatchesBruteForce(db, 88.0, 0, 5);
        AssertNearestMatchesBruteForce(db, -89.9, 0, 3);
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
