using Geoblitz.Geo;
using Xunit;

namespace Geoblitz.Geo.Tests;

public class GeoMathTests
{
    [Fact]
    public void Haversine_BerlinToMunich_WithinOneKm()
        => Assert.InRange(GeoMath.HaversineKm(52.5200, 13.4050, 48.1374, 11.5755), 503.0, 506.0);

    [Fact]
    public void Haversine_SamePoint_IsZero()
        => Assert.Equal(0.0, GeoMath.HaversineKm(48.0, 11.0, 48.0, 11.0), 3);

    [Fact]
    public void Haversine_PoleToPole_IsHalfCircumference()
        => Assert.InRange(GeoMath.HaversineKm(90, 0, -90, 0), 20015.0, 20016.0);

    [Fact]
    public void Haversine_AcrossAntimeridian_OneDegreeAtEquator()
        => Assert.InRange(GeoMath.HaversineKm(0, 179.5, 0, -179.5), 111.0, 111.4);

    [Fact]
    public void UnitVector_HasLengthOne()
    {
        GeoMath.ToUnitVector(48.1374, 11.5755, out var x, out var y, out var z);
        Assert.Equal(1.0, Math.Sqrt((double)x * x + (double)y * y + (double)z * z), 5);
    }

    [Fact]
    public void ChordSq_RoundTrips_Km()
    {
        foreach (var km in new[] { 0.5, 10, 111, 1000, 5000, 15000 })
            Assert.Equal(km, GeoMath.ChordSqToKm(GeoMath.KmToChordSq(km)), 1);
    }

    [Fact]
    public void ChordDistance_MatchesHaversine_ForRandomPairs()
    {
        var rng = new Random(42);
        for (var i = 0; i < 500; i++)
        {
            double lat1 = rng.NextDouble() * 180 - 90, lon1 = rng.NextDouble() * 360 - 180;
            double lat2 = rng.NextDouble() * 180 - 90, lon2 = rng.NextDouble() * 360 - 180;
            GeoMath.ToUnitVector(lat1, lon1, out var x1, out var y1, out var z1);
            GeoMath.ToUnitVector(lat2, lon2, out var x2, out var y2, out var z2);
            float dx = x1 - x2, dy = y1 - y2, dz = z1 - z2;
            var viaChord = GeoMath.ChordSqToKm(dx * dx + dy * dy + dz * dz);
            var haversine = GeoMath.HaversineKm(lat1, lon1, lat2, lon2);
            Assert.True(Math.Abs(viaChord - haversine) <= Math.Max(1.0, haversine * 0.005),
                $"chord {viaChord} vs haversine {haversine}");
        }
    }
}
