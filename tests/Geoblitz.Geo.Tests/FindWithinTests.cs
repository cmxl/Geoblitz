using System.Text;
using Geoblitz.Geo;
using Xunit;

namespace Geoblitz.Geo.Tests;

public class FindWithinTests
{
    private static GeoDatabase Db(params (string Name, double Lat, double Lon, int Pop)[] pts)
        => GeoDatabase.Build(GeoDatabaseBuildTests.Cities(pts));

    private static string Name(GeoDatabase db, int index) => Encoding.UTF8.GetString(db.GetNameUtf8(index));

    [Fact]
    public void Finds_PointsInRadius_SortedByDistance()
    {
        var db = Db(("Munich", 48.1374, 11.5755, 1_471_508),
                    ("Freising", 48.4028, 11.7489, 45_227),
                    ("Berlin", 52.5244, 13.4105, 3_644_826));
        Span<GeoHit> hits = stackalloc GeoHit[10];
        var n = db.FindWithin(48.2, 11.6, 60, 0, hits);
        Assert.Equal(2, n);
        Assert.Equal("Munich", Name(db, hits[0].Index));
        Assert.Equal("Freising", Name(db, hits[1].Index));
        Assert.True(hits[0].DistanceKm < hits[1].DistanceKm);
        Assert.InRange(hits[0].DistanceKm, 6.0, 9.0); // ~7 km
    }

    [Fact]
    public void AntimeridianWrap_FindsBothSides()
    {
        var db = Db(("East", 0, 179.9, 1), ("West", 0, -179.9, 1), ("Far", 0, 0, 1));
        Span<GeoHit> hits = stackalloc GeoHit[10];
        var n = db.FindWithin(0, 179.99, 50, 0, hits);
        Assert.Equal(2, n);
        var names = new[] { Name(db, hits[0].Index), Name(db, hits[1].Index) };
        Assert.Contains("East", names);
        Assert.Contains("West", names);
    }

    [Fact]
    public void NearPole_WideLongitudeSpread_AllFound()
    {
        var db = Db(("P1", 89.0, 0, 1), ("P2", 89.0, 90, 1), ("P3", 89.0, 170, 1), ("Equator", 0, 0, 1));
        Span<GeoHit> hits = stackalloc GeoHit[10];
        var n = db.FindWithin(89.5, 45, 500, 0, hits);
        Assert.Equal(3, n);
    }

    [Fact]
    public void MinPopulation_Filters()
    {
        var db = Db(("Big", 48.0, 11.0, 1_000_000), ("Small", 48.01, 11.01, 500));
        Span<GeoHit> hits = stackalloc GeoHit[10];
        var n = db.FindWithin(48.0, 11.0, 50, 10_000, hits);
        Assert.Equal(1, n);
        Assert.Equal("Big", Name(db, hits[0].Index));
    }

    [Fact]
    public void ResultSpanSmallerThanMatches_ReturnsClosestOnes()
    {
        var db = Db(("A", 48.0, 11.0, 1), ("B", 48.1, 11.0, 1), ("C", 48.2, 11.0, 1), ("D", 48.3, 11.0, 1));
        Span<GeoHit> hits = stackalloc GeoHit[2];
        var n = db.FindWithin(48.0, 11.0, 500, 0, hits);
        Assert.Equal(2, n);
        Assert.Equal("A", Name(db, hits[0].Index));
        Assert.Equal("B", Name(db, hits[1].Index));
    }

    [Fact]
    public void NoMatches_ReturnsZero()
    {
        var db = Db(("A", 48.0, 11.0, 1));
        Span<GeoHit> hits = stackalloc GeoHit[4];
        Assert.Equal(0, db.FindWithin(-48.0, -11.0, 100, 0, hits));
    }

    /// <summary>Boundary band excluded from strict set comparison: float chord vs double haversine
    /// disagree by well under a metre, so points within this band of the radius are neither required
    /// nor forbidden. Everything strictly inside must be returned, nothing outside may be.</summary>
    private const double BoundaryEpsKm = 0.01;

    /// <summary>Asserts INDEX-SET equality against a haversine brute force (modulo the boundary band).</summary>
    private static void AssertIndexSetMatchesBruteForce(
        GeoDatabase db, double qLat, double qLon, double radiusKm, GeoHit[] hits)
    {
        var n = db.FindWithin(qLat, qLon, radiusKm, 0, hits);
        Assert.True(n <= hits.Length);

        var returned = new HashSet<int>();
        for (var i = 0; i < n; i++)
        {
            Assert.True(returned.Add(hits[i].Index), $"index {hits[i].Index} returned twice");
            if (i > 0) Assert.True(hits[i - 1].DistanceKm <= hits[i].DistanceKm, "results not ascending");
        }

        for (var i = 0; i < db.Count; i++)
        {
            var d = GeoMath.HaversineKm(qLat, qLon, db.GetLat(i), db.GetLon(i));
            if (d <= radiusKm - BoundaryEpsKm)
                Assert.True(returned.Contains(i),
                    $"query ({qLat}, {qLon}) r={radiusKm} km missed index {i} at " +
                    $"({db.GetLat(i)}, {db.GetLon(i)}), d={d:F3} km");
            else if (d > radiusKm + BoundaryEpsKm)
                Assert.False(returned.Contains(i),
                    $"query ({qLat}, {qLon}) r={radiusKm} km returned out-of-radius index {i}, d={d:F3} km");
        }
    }

    [Fact]
    public void MatchesBruteForce_OnRandomData()
    {
        var rng = new Random(123);
        var pts = new (string, double, double, int)[3000];
        for (var i = 0; i < pts.Length; i++)
            pts[i] = ($"P{i}", rng.NextDouble() * 180 - 90, rng.NextDouble() * 360 - 180, 0);
        var db = GeoDatabase.Build(GeoDatabaseBuildTests.Cities(pts));

        var hits = new GeoHit[3000];
        for (var q = 0; q < 40; q++)
        {
            double qLat = rng.NextDouble() * 180 - 90, qLon = rng.NextDouble() * 360 - 180;
            AssertIndexSetMatchesBruteForce(db, qLat, qLon, 400, hits);
        }
    }

    [Fact]
    public void MatchesBruteForce_AtHighLatitudes_AndInThePoleRow()
    {
        // dense polar cap: exactly the geometry the longitude-window math gets wrong
        var pts = new List<(string, double, double, int)>();
        var n = 0;
        for (var lat = 80.0; lat <= 89.9; lat += 0.5)
            for (var lon = -180.0; lon < 180.0; lon += 5.0)
            {
                pts.Add(($"N{n++}", lat, lon, 0));
                pts.Add(($"S{n++}", -lat, lon, 0));
            }
        var db = GeoDatabase.Build(GeoDatabaseBuildTests.Cities(pts.ToArray()));
        var hits = new GeoHit[db.Count];

        foreach (var qLat in new[] { 82.0, 85.0, 88.0, 89.99, -85.0, -88.0, -89.99 })
            foreach (var radius in new[] { 1.0, 50.0, 300.0, 500.0 })
                foreach (var qLon in new[] { 0.0, 179.5 })
                    AssertIndexSetMatchesBruteForce(db, qLat, qLon, radius, hits);
    }

    [Fact]
    public void PoleRow_TinyRadius_FindsTheNearbyPoint()
    {
        // Regression: four points in the top grid row; the one 386 m away (20 deg of longitude at
        // lat 89.99) was missed because the row's longitude window was computed for lat 89.9.
        var db = Db(("P0", 89.99, 0, 1), ("P20", 89.99, 20, 1), ("P90", 89.99, 90, 1), ("P179", 89.99, 179, 1));
        Span<GeoHit> hits = stackalloc GeoHit[10];
        var n = db.FindWithin(89.99, 0, 1, 0, hits);

        Assert.Equal(2, n);
        Assert.Equal("P0", Name(db, hits[0].Index));
        Assert.Equal("P20", Name(db, hits[1].Index));
        Assert.InRange(hits[1].DistanceKm, 0.38, 0.39); // ~386 m
    }

    [Fact]
    public void MinPopulation_IsAppliedBeforeTruncation()
    {
        // The closest point fails the filter and the result span holds only one hit: the returned
        // hit must be the closest point that PASSES the filter, not "nothing".
        var db = Db(("Small", 48.0, 11.0, 1), ("Big", 48.1, 11.0, 1_000_000));
        Span<GeoHit> hits = stackalloc GeoHit[1];
        var n = db.FindWithin(48.0, 11.0, 100, 10_000, hits);
        Assert.Equal(1, n);
        Assert.Equal("Big", Name(db, hits[0].Index));
    }

    [Fact]
    public void RealDataset_Berlin15km_FindsBerlinFirst()
    {
        var db = GeoDatabase.LoadDefault();
        Assert.True(db.Count > 100_000);
        var hits = new GeoHit[1000];
        // NOTE: query point is the GeoNames coordinate for the "Berlin" city record itself
        // (52.52437, 13.41053), not the commonly-cited 52.5200/13.4050 landmark coordinate.
        // The embedded GeoNames cities1000 dataset also lists Berlin's boroughs (e.g. "Mitte",
        // pop 102,338) as separate populated places, and Mitte's point happens to sit only ~8m
        // from 52.5200/13.4050 — genuinely closer than the "Berlin" record's own point (~612m
        // away) at that coordinate. Using Berlin's own coordinate keeps this a real smoke test
        // without depending on that district-vs-city ambiguity.
        var n = db.FindWithin(52.52437, 13.41053, 15, 0, hits);
        Assert.True(n > 0);
        Assert.Equal("Berlin", Encoding.UTF8.GetString(db.GetNameUtf8(hits[0].Index)));
        Assert.True(hits[0].DistanceKm < 3);
    }

    [Fact]
    public void RealDataset_LargeRadius_ReturnsClosestNAscending()
    {
        // ~19.5k cities match a 500 km radius around Berlin but only 1000 fit in the result span:
        // the returned 1000 must be the 1000 closest, ascending. Guards the bounded-selection path.
        var db = GeoDatabase.LoadDefault();
        var hits = new GeoHit[1000];
        const double qLat = 52.52437, qLon = 13.41053, radius = 500;
        var n = db.FindWithin(qLat, qLon, radius, 0, hits);
        Assert.Equal(1000, n);

        var brute = new List<double>(32_000);
        for (var i = 0; i < db.Count; i++)
        {
            var d = GeoMath.HaversineKm(qLat, qLon, db.GetLat(i), db.GetLon(i));
            if (d <= radius) brute.Add(d);
        }
        brute.Sort();
        Assert.True(brute.Count > 10_000, $"expected a large match set, got {brute.Count}");

        for (var i = 0; i < n; i++)
        {
            if (i > 0) Assert.True(hits[i - 1].DistanceKm <= hits[i].DistanceKm, "results not ascending");
            Assert.True(Math.Abs(hits[i].DistanceKm - brute[i]) < 0.02,
                $"rank {i}: got {hits[i].DistanceKm} km, brute force {brute[i]} km");
        }
    }

    [Fact]
    public void RealDataset_LargeRadius_WithMinPopulation_ReturnsClosestQualifying()
    {
        // The filter must be applied BEFORE selection: closer cities below the threshold must not
        // consume heap slots. ~19.5k cities match a 500 km radius around Berlin, most of them below
        // 50k population, and only 50 results fit — so this exercises heap eviction with an active
        // filter, which the two-point filter tests cannot.
        var db = GeoDatabase.LoadDefault();
        var hits = new GeoHit[50];
        const double qLat = 52.52437, qLon = 13.41053, radius = 500;
        const int minPopulation = 50_000;
        var n = db.FindWithin(qLat, qLon, radius, minPopulation, hits);
        Assert.Equal(50, n);

        var brute = new List<(int Idx, double D)>();
        for (var i = 0; i < db.Count; i++)
        {
            if (db.GetPopulation(i) < minPopulation) continue;
            var d = GeoMath.HaversineKm(qLat, qLon, db.GetLat(i), db.GetLon(i));
            if (d <= radius) brute.Add((i, d));
        }
        brute.Sort((a, b) => a.D.CompareTo(b.D));
        Assert.True(brute.Count > 60, $"expected more qualifying matches than the span, got {brute.Count}");

        var expected = new HashSet<int>();
        for (var i = 0; i < n; i++) expected.Add(brute[i].Idx);
        for (var i = 0; i < n; i++)
        {
            if (i > 0) Assert.True(hits[i - 1].DistanceKm <= hits[i].DistanceKm, "results not ascending");
            Assert.True(db.GetPopulation(hits[i].Index) >= minPopulation, "filter leaked");
            Assert.True(expected.Contains(hits[i].Index),
                $"rank {i}: index {hits[i].Index} is not among the 50 closest qualifying cities");
            Assert.True(Math.Abs(hits[i].DistanceKm - brute[i].D) < 0.02,
                $"rank {i}: got {hits[i].DistanceKm} km, brute force {brute[i].D} km");
        }
    }

    [Fact]
    public void RealDataset_LargeRadius_DoesNotAllocate()
    {
        // I1 regression guard. The old path collected all ~19.5k matches into a growing pooled
        // HitBuffer and sorted them; the win of the bounded selection is CPU (no 19.5k-element sort,
        // no growth copies), and the path must stay allocation-free on the managed heap. Both
        // selection-buffer branches are covered: k <= StackSelectionCapacity (256) is pure
        // stackalloc, larger result spans rent from ArrayPool (allocation-free once warm).
        var db = GeoDatabase.LoadDefault();
        var big = new GeoHit[1000];   // ArrayPool branch
        var small = new GeoHit[100];  // stackalloc branch
        for (var i = 0; i < 20; i++)
        {
            db.FindWithin(52.52437, 13.41053, 500, 0, big);
            db.FindWithin(52.52437, 13.41053, 500, 0, small);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10; i++)
        {
            db.FindWithin(52.52437, 13.41053, 500, 0, big);
            db.FindWithin(52.52437, 13.41053, 500, 0, small);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 1024, $"allocated {allocated} B over 20 queries (expected 0)");
    }
}
