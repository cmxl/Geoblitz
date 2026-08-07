using System.Text;
using HighPerf.Geo;
using Xunit;

namespace HighPerf.Geo.Tests;

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
}
