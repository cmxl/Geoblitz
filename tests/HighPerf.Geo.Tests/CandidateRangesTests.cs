using HighPerf.Geo;
using Xunit;

namespace HighPerf.Geo.Tests;

/// <summary>Direct tests for the strongest stated invariant of the grid: the ranges returned by
/// <see cref="GeoDatabase.GetCandidateRanges"/> are a SUPERSET of every point within the radius,
/// for any query latitude in [-90, 90] and any radius up to half the circumference.</summary>
public class CandidateRangesTests
{
    /// <summary>Boundary band excluded from the strict comparison: float chord vs double haversine
    /// disagree by well under a metre, so only points strictly inside the radius are asserted.</summary>
    private const double BoundaryEpsKm = 0.01;

    private static readonly Lazy<GeoDatabase> PolarDense = new(BuildPolarDense);

    /// <summary>Dense grid over both polar caps (where the longitude-window math is hardest) plus an
    /// area-uniform random sprinkle over the whole sphere.</summary>
    private static GeoDatabase BuildPolarDense()
    {
        var pts = new List<(string, double, double, int)>(20_000);
        var n = 0;
        for (var lat = 78.0; lat <= 89.95; lat += 0.25)
            for (var lon = -180.0; lon < 180.0; lon += 2.5)
            {
                pts.Add(($"N{n++}", lat, lon, 1));
                pts.Add(($"S{n++}", -lat, lon, 1));
            }

        var rng = new Random(9001);
        for (var i = 0; i < 3000; i++)
        {
            var u = rng.NextDouble() * 2 - 1; // area-uniform latitude
            pts.Add(($"R{n++}", Math.Asin(u) * 180.0 / Math.PI, rng.NextDouble() * 360 - 180, 1));
        }

        return GeoDatabase.Build(GeoDatabaseBuildTests.Cities(pts.ToArray()));
    }

    private static void AssertSuperset(GeoDatabase db, bool[] covered, double lat, double lon, double radiusKm)
    {
        Array.Clear(covered);
        var ranges = new DataRange[2 * db.LatCells];
        var count = db.GetCandidateRanges(lat, lon, radiusKm, ranges);
        Assert.InRange(count, 0, ranges.Length);

        for (var r = 0; r < count; r++)
        {
            var range = ranges[r];
            Assert.True(range.Start <= range.End, "range is inverted");
            Assert.InRange(range.End, 0, db.Count);
            for (var i = range.Start; i < range.End; i++)
            {
                Assert.False(covered[i], $"index {i} covered by two ranges (double counting)");
                covered[i] = true;
            }
        }

        for (var i = 0; i < db.Count; i++)
        {
            var d = GeoMath.HaversineKm(lat, lon, db.GetLat(i), db.GetLon(i));
            if (d <= radiusKm - BoundaryEpsKm)
                Assert.True(covered[i],
                    $"superset violated: query ({lat}, {lon}) r={radiusKm} km missed index {i} " +
                    $"at ({db.GetLat(i)}, {db.GetLon(i)}), d={d:F3} km");
        }
    }

    [Fact]
    public void IsSuperset_AcrossLatitudeLongitudeRadiusGrid()
    {
        var db = PolarDense.Value;
        var covered = new bool[db.Count];
        double[] lats = [0, 45, -45, 60, 82, 85, 88, 89.5, 89.99, -82, -85, -88, -89.99];
        double[] lons = [0, 45, 179.9, -179.9];
        double[] radii = [1, 50, 300, 500, 1500, 3200];

        foreach (var lat in lats)
            foreach (var lon in lons)
                foreach (var radius in radii)
                    AssertSuperset(db, covered, lat, lon, radius);
    }

    [Fact]
    public void IsSuperset_ForRandomQueries_IncludingPolesAndAntimeridian()
    {
        var db = PolarDense.Value;
        var covered = new bool[db.Count];
        var rng = new Random(20260807);

        for (var q = 0; q < 120; q++)
        {
            var lat = (q % 3) switch
            {
                0 => 85.0 + rng.NextDouble() * 5.0,   // > 85
                1 => -90.0 + rng.NextDouble() * 5.0,  // < -85
                _ => rng.NextDouble() * 180.0 - 90.0,
            };
            var lon = (q % 4) switch
            {
                0 => 180.0 - rng.NextDouble() * 0.5,   // just west of the antimeridian
                1 => -180.0 + rng.NextDouble() * 0.5,  // just east of it
                _ => rng.NextDouble() * 360.0 - 180.0,
            };
            var radius = 500.0 + rng.NextDouble() * 4500.0; // 500 .. 5000 km
            AssertSuperset(db, covered, lat, lon, radius);
        }
    }

    [Fact]
    public void AntimeridianQuery_EmitsAtMostTwoDisjointSegmentsPerRow()
    {
        var db = PolarDense.Value;
        var ranges = new DataRange[2 * db.LatCells];

        foreach (var lon in new[] { 179.9, -179.9, 180.0, -180.0 })
            foreach (var radius in new[] { 50.0, 300.0, 500.0 })
                foreach (var lat in new[] { 0.0, 60.0, 85.0, -85.0, 89.99 })
                {
                    var count = db.GetCandidateRanges(lat, lon, radius, ranges);
                    Assert.InRange(count, 0, ranges.Length);

                    var perRow = new Dictionary<int, int>();
                    for (var r = 0; r < count; r++)
                    {
                        var range = ranges[r];
                        if (range.Start == range.End) continue;
                        var row = db.CellOfLat(db.GetLat(range.Start));
                        perRow[row] = perRow.GetValueOrDefault(row) + 1;
                        // every point of a segment must belong to that same latitude row
                        Assert.Equal(row, db.CellOfLat(db.GetLat(range.End - 1)));
                    }

                    foreach (var (row, segments) in perRow)
                        Assert.True(segments <= 2,
                            $"query ({lat}, {lon}) r={radius}: row {row} produced {segments} segments");
                }
    }

    [Fact]
    public void PoleRow_TinyRadius_FallsBackToWholeRow()
    {
        // The top row spans latitude 89..90; its worst-case longitude window is unbounded, so the
        // only superset-safe answer for that row is a whole-row scan (regression for the old
        // Math.Min(89.9, ...) clamp, which produced a +-5 degree window for a 1 km radius).
        var db = GeoDatabase.Build(GeoDatabaseBuildTests.Cities(
            ("A", 89.99, 0, 1), ("B", 89.99, 20, 1), ("C", 89.99, 179, 1), ("Equator", 0, 0, 1)));

        var ranges = new DataRange[2 * db.LatCells];
        var count = db.GetCandidateRanges(89.99, 0, 1, ranges);

        var covered = 0;
        for (var r = 0; r < count; r++) covered += ranges[r].End - ranges[r].Start;
        Assert.Equal(3, covered); // all three pole-row points are candidates, the equator point is not
    }

    [Fact]
    public void EquatorWindow_StaysTight_OnePointPerLongitudeCell()
    {
        // One point per longitude cell along the equator. At the equator the exact longitude
        // half-window equals the angular radius (500 km / R = 4.4966 deg), so the window must cover
        // cells 175..184 -> exactly 10 candidates. Guards against the corrected formula over-widening
        // the common low-latitude case.
        var pts = new (string, double, double, int)[360];
        for (var c = 0; c < 360; c++) pts[c] = ($"C{c}", 0.5, -180.0 + c + 0.5, 1);
        var db = GeoDatabase.Build(GeoDatabaseBuildTests.Cities(pts));

        var ranges = new DataRange[2 * db.LatCells];
        var count = db.GetCandidateRanges(0, 0, 500, ranges);
        var covered = 0;
        for (var r = 0; r < count; r++) covered += ranges[r].End - ranges[r].Start;
        Assert.Equal(10, covered);
    }
}
