using System.Text;
using HighPerf.Geo;
using Xunit;

namespace HighPerf.Geo.Tests;

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

    [Fact]
    public void MatchesBruteForce_OnRandomData()
    {
        var rng = new Random(123);
        var pts = new (string, double, double, int)[3000];
        for (var i = 0; i < pts.Length; i++)
            pts[i] = ($"P{i}", rng.NextDouble() * 180 - 90, rng.NextDouble() * 360 - 180, 0);
        var db = GeoDatabase.Build(GeoDatabaseBuildTests.Cities(pts));

        var hits = new GeoHit[3000];
        for (var q = 0; q < 20; q++)
        {
            double qLat = rng.NextDouble() * 180 - 90, qLon = rng.NextDouble() * 360 - 180;
            const double radius = 400;
            var n = db.FindWithin(qLat, qLon, radius, 0, hits);

            var brute = 0;
            for (var i = 0; i < db.Count; i++)
                if (GeoMath.HaversineKm(qLat, qLon, db.GetLat(i), db.GetLon(i)) <= radius + 0.5)
                    brute++;
            // chord vs haversine float rounding can differ at the exact boundary; allow off-by-boundary
            Assert.InRange(n, brute - 2, brute + 2);
        }
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
}
