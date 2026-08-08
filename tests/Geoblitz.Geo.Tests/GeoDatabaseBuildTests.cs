using System.Text;
using Geoblitz.Geo;
using Xunit;

namespace Geoblitz.Geo.Tests;

public class GeoDatabaseBuildTests
{
    internal static ParsedCities Cities(params (string Name, double Lat, double Lon, int Pop)[] pts)
    {
        var sb = new StringBuilder();
        foreach (var p in pts)
            sb.Append(p.Name).Append("\tXX\t")
              .Append(p.Lat.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\t')
              .Append(p.Lon.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\t')
              .Append(p.Pop).Append('\n');
        return CityTableParser.Parse(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    [Fact]
    public void CellStart_IsMonotonic_AndCoversAllPoints()
    {
        var db = GeoDatabase.Build(Cities(("A", 0.5, 0.5, 1), ("B", 0.6, 0.4, 2), ("C", 50.2, 8.1, 3), ("D", -33.9, 151.2, 4)));
        Assert.Equal(180 * 360 + 1, db.CellStart.Length);
        for (var c = 0; c < db.CellStart.Length - 1; c++)
            Assert.True(db.CellStart[c] <= db.CellStart[c + 1]);
        Assert.Equal(4, db.CellStart[^1]);
    }

    [Fact]
    public void EveryPoint_LiesInItsOwnCellRange()
    {
        var db = GeoDatabase.Build(Cities(("A", 0.5, 0.5, 1), ("B", 0.6, 0.4, 2), ("C", 50.2, 8.1, 3),
                                           ("D", -33.9, 151.2, 4), ("E", 89.9, 179.9, 5), ("F", -89.9, -179.9, 6)));
        for (var i = 0; i < db.Count; i++)
        {
            var cell = db.CellOfLat(db.GetLat(i)) * db.LonCells + db.CellOfLon(db.GetLon(i));
            Assert.InRange(i, db.CellStart[cell], db.CellStart[cell + 1] - 1);
        }
    }

    [Fact]
    public void SameCellPoints_AreAdjacent_AndDataSurvivesPermutation()
    {
        var db = GeoDatabase.Build(Cities(("C", 50.2, 8.1, 3), ("A", 0.5, 0.5, 1), ("B", 0.6, 0.4, 2)));
        var names = new string[db.Count];
        for (var i = 0; i < db.Count; i++) names[i] = Encoding.UTF8.GetString(db.GetNameUtf8(i));
        var ai = Array.IndexOf(names, "A");
        var bi = Array.IndexOf(names, "B");
        Assert.Equal(1, Math.Abs(ai - bi)); // A and B share a 1-degree cell -> adjacent after permutation
        Assert.Equal(1, db.GetPopulation(ai));
        Assert.Equal(0.5f, db.GetLat(ai), 3);
    }

    [Fact]
    public void UnitVectors_MatchLatLon()
    {
        var db = GeoDatabase.Build(Cities(("A", 48.1374, 11.5755, 1)));
        GeoMath.ToUnitVector(db.GetLat(0), db.GetLon(0), out var x, out var y, out var z);
        Assert.Equal(x, db.X[0], 5);
        Assert.Equal(y, db.Y[0], 5);
        Assert.Equal(z, db.Z[0], 5);
    }

    [Fact]
    public void CellOf_ClampsEdges()
    {
        var db = GeoDatabase.Build(Cities(("A", 0, 0, 1)));
        Assert.Equal(179, db.CellOfLat(90));
        Assert.Equal(0, db.CellOfLat(-90));
        Assert.Equal(359, db.CellOfLon(180));
        Assert.Equal(0, db.CellOfLon(-180));
    }

    [Fact]
    public void Build_ZeroCities_ProducesEmptyQueryableDatabase()
    {
        var db = GeoDatabase.Build(Cities());
        Assert.Equal(0, db.Count);
        Assert.Equal(180 * 360 + 1, db.CellStart.Length);
        Assert.All(db.CellStart, c => Assert.Equal(0, c));

        Span<GeoHit> hits = stackalloc GeoHit[10];
        Assert.Equal(0, db.FindWithin(0, 0, 100, 0, hits));
        Assert.Equal(0, db.FindNearest(0, 0, 5, hits));
    }

    [Fact]
    public void Build_OneCity_FindsItselfAsNearestAndWithin()
    {
        var db = GeoDatabase.Build(Cities(("Solo", 10, 20, 5)));
        Assert.Equal(1, db.Count);

        Span<GeoHit> nearest = stackalloc GeoHit[5];
        var nn = db.FindNearest(10, 20, 5, nearest);
        Assert.Equal(1, nn);
        Assert.Equal(0, nearest[0].Index);
        Assert.InRange(nearest[0].DistanceKm, 0.0, 0.001);

        Span<GeoHit> within = stackalloc GeoHit[5];
        var nw = db.FindWithin(10, 20, 10, 0, within);
        Assert.Equal(1, nw);
        Assert.Equal(0, within[0].Index);
    }

    [Fact]
    public void Build_AllCitiesInOneCell_StillDistinguishesAndFindsAll()
    {
        // Five points packed into the same 1-degree grid cell: exercises the case where the
        // permutation/cell-range logic degenerates to a single non-empty cell.
        var db = GeoDatabase.Build(Cities(
            ("A", 10.10, 20.10, 1), ("B", 10.20, 20.20, 2), ("C", 10.30, 20.30, 3),
            ("D", 10.40, 20.40, 4), ("E", 10.50, 20.50, 5)));
        Assert.Equal(5, db.Count);

        var nonEmptyCells = 0;
        for (var c = 0; c < db.CellStart.Length - 1; c++)
            if (db.CellStart[c + 1] > db.CellStart[c]) nonEmptyCells++;
        Assert.Equal(1, nonEmptyCells);

        Span<GeoHit> hits = stackalloc GeoHit[10];
        var n = db.FindWithin(10.30, 20.30, 100, 0, hits);
        Assert.Equal(5, n);

        var pops = new HashSet<int>();
        for (var i = 0; i < n; i++) pops.Add(db.GetPopulation(hits[i].Index));
        Assert.Equal(new HashSet<int> { 1, 2, 3, 4, 5 }, pops); // every distinct city survived the permutation
    }
}
