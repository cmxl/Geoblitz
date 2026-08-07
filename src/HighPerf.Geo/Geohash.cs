namespace HighPerf.Geo;

public static class Geohash
{
    public const int MaxPrecision = 12;
    private const string Base32 = "0123456789bcdefghjkmnpqrstuvwxyz";

    public static int Encode(double lat, double lon, int precision, Span<char> dest)
    {
        double latLo = -90, latHi = 90, lonLo = -180, lonHi = 180;
        var evenBit = true;
        int bit = 0, ch = 0, written = 0;
        while (written < precision)
        {
            if (evenBit)
            {
                var mid = (lonLo + lonHi) / 2;
                if (lon >= mid) { ch = (ch << 1) | 1; lonLo = mid; } else { ch <<= 1; lonHi = mid; }
            }
            else
            {
                var mid = (latLo + latHi) / 2;
                if (lat >= mid) { ch = (ch << 1) | 1; latLo = mid; } else { ch <<= 1; latHi = mid; }
            }
            evenBit = !evenBit;
            if (++bit == 5)
            {
                dest[written++] = Base32[ch];
                bit = 0;
                ch = 0;
            }
        }
        return written;
    }

    public static bool TryDecode(ReadOnlySpan<char> hash, out double lat, out double lon, out double latErr, out double lonErr)
    {
        lat = lon = latErr = lonErr = 0;
        if (hash.IsEmpty || hash.Length > MaxPrecision) return false;

        double latLo = -90, latHi = 90, lonLo = -180, lonHi = 180;
        var evenBit = true;
        foreach (var c in hash)
        {
            var v = Base32.IndexOf(char.ToLowerInvariant(c));
            if (v < 0) return false;
            for (var b = 4; b >= 0; b--)
            {
                var bit = (v >> b) & 1;
                if (evenBit)
                {
                    var mid = (lonLo + lonHi) / 2;
                    if (bit == 1) lonLo = mid; else lonHi = mid;
                }
                else
                {
                    var mid = (latLo + latHi) / 2;
                    if (bit == 1) latLo = mid; else latHi = mid;
                }
                evenBit = !evenBit;
            }
        }
        lat = (latLo + latHi) / 2;
        lon = (lonLo + lonHi) / 2;
        latErr = (latHi - latLo) / 2;
        lonErr = (lonHi - lonLo) / 2;
        return true;
    }
}
