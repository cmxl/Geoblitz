using System.Globalization;

namespace HighPerf.Api;

/// <summary>Allocation-free query-string lookup over the raw QueryString span.
/// Values must not be percent-encoded (all our params are numbers / base32).</summary>
internal static class QueryParams
{
    public static bool TryGetDouble(ReadOnlySpan<char> queryString, ReadOnlySpan<char> name, out double value)
    {
        value = 0;
        return TryGetRaw(queryString, name, out var raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public static bool TryGetInt(ReadOnlySpan<char> queryString, ReadOnlySpan<char> name, out int value)
    {
        value = 0;
        return TryGetRaw(queryString, name, out var raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    public static bool TryGetRaw(ReadOnlySpan<char> queryString, ReadOnlySpan<char> name, out ReadOnlySpan<char> raw)
    {
        raw = default;
        var qs = queryString;
        if (!qs.IsEmpty && qs[0] == '?') qs = qs[1..];
        while (!qs.IsEmpty)
        {
            var amp = qs.IndexOf('&');
            var pair = amp < 0 ? qs : qs[..amp];
            qs = amp < 0 ? default : qs[(amp + 1)..];
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;
            if (pair[..eq].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                raw = pair[(eq + 1)..];
                return true;
            }
        }
        return false;
    }
}
