using System.Globalization;

namespace HighPerf.Api;

internal static class GeoCacheKey
{
    public static string Compute(HttpContext ctx)
    {
        var qs = (ctx.Request.QueryString.Value ?? "").AsSpan();
        var lat = Quantized(qs, "lat");
        var lon = Quantized(qs, "lon");
        var fromLat = Quantized(qs, "fromLat");
        var fromLon = Quantized(qs, "fromLon");
        var toLat = Quantized(qs, "toLat");
        var toLon = Quantized(qs, "toLon");
        QueryParams.TryGetInt(qs, "count", out var count);
        QueryParams.TryGetDouble(qs, "radiusKm", out var radius);
        QueryParams.TryGetInt(qs, "minPopulation", out var minPop);
        QueryParams.TryGetInt(qs, "precision", out var precision);
        QueryParams.TryGetRaw(qs, "hash", out var hash);
        return string.Create(CultureInfo.InvariantCulture,
            $"{lat}|{lon}|{fromLat}|{fromLon}|{toLat}|{toLon}|{count}|{radius}|{minPop}|{precision}|{hash}");
    }

    private static double Quantized(ReadOnlySpan<char> qs, string name)
        => QueryParams.TryGetDouble(qs, name, out var v) ? Math.Round(v, 3) : double.NaN;
}
