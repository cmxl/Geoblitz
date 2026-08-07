namespace HighPerf.Geo;

/// <summary>Spherical geo math. Internal comparisons use squared 3D chord distance
/// between unit vectors — monotonic with great-circle distance, no per-point trig.</summary>
public static class GeoMath
{
    public const double EarthRadiusKm = 6371.0088;
    private const double DegToRad = Math.PI / 180.0;

    public static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        double p1 = lat1 * DegToRad, p2 = lat2 * DegToRad;
        double dp = p2 - p1, dl = (lon2 - lon1) * DegToRad;
        var a = Math.Sin(dp / 2) * Math.Sin(dp / 2)
              + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }

    public static void ToUnitVector(double latDeg, double lonDeg, out float x, out float y, out float z)
    {
        double lat = latDeg * DegToRad, lon = lonDeg * DegToRad;
        var cosLat = Math.Cos(lat);
        x = (float)(cosLat * Math.Cos(lon));
        y = (float)(cosLat * Math.Sin(lon));
        z = (float)Math.Sin(lat);
    }

    public static float KmToChordSq(double km)
    {
        var half = Math.Min(km, EarthRadiusKm * Math.PI) / (2 * EarthRadiusKm);
        var chord = 2 * Math.Sin(half);
        return (float)(chord * chord);
    }

    public static double ChordSqToKm(float chordSq)
    {
        var chord = Math.Sqrt(Math.Clamp(chordSq, 0f, 4f));
        return 2 * EarthRadiusKm * Math.Asin(chord / 2);
    }
}
