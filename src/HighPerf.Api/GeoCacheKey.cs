using System.Globalization;
using HighPerf.Geo;

namespace HighPerf.Api;

/// <summary>Composes the output-cache key for the geo endpoints from the raw query string.
/// <para><b>Validity is part of the key.</b> The cache lookup runs in middleware, <em>before</em> a
/// handler can reject bad input, so a key that cannot tell "parameter absent" from "parameter
/// present but invalid" lets a cached 200 be replayed for a request the API must answer with 400
/// (M2 review, finding C1). The key therefore starts with a fixed-width validity mask — one char per
/// participating parameter, <c>'-'</c> absent / <c>'v'</c> valid / <c>'x'</c> invalid — followed by
/// one field per parameter.</para>
/// <para><b>Fields are length-prefixed</b> (<c>|&lt;length&gt;:&lt;token&gt;</c>), which is what makes
/// the mask trustworthy. A query value may legally contain the field separator — nothing
/// percent-decodes it and Kestrel does not reject it — so a plain <c>a|b</c> style key would let one
/// parameter's raw text shift every following field boundary and reproduce another request's key with
/// the same mask. With an explicit length per field the key is uniquely decodable, so two equal keys
/// for the same path necessarily agree on every parameter's presence, validity and value, and a
/// valid/invalid pair cannot collide. 400s are never stored anyway (the default output-cache policy
/// caches only 200s), so distinct keys are all the fix needs.</para>
/// <para>The validity predicate here is a <em>conservative superset</em> of the handlers' own
/// validation: everything a handler rejects is marked <c>'x'</c> here, non-finite values included.
/// Marking something invalid that a handler would accept is harmless — it only gives that request its
/// own cache entry — whereas the reverse would reopen C1.</para>
/// <para>Valid values are written <b>canonically</b> (the parsed value re-formatted) so that
/// equivalent spellings — <c>count=3</c> / <c>count=03</c>, <c>lat=47.401</c> / <c>lat=47.4010</c> —
/// still share one entry; coordinates are additionally quantized to 3 decimals (~110 m) for cache
/// density. Invalid values are written raw, so every distinct invalid spelling gets its own
/// (never-stored) key. Quantization is applied only <em>after</em> the range check, otherwise
/// <c>lat=90.0004</c> would round into <c>lat=90</c>'s valid bucket.</para>
/// <para>Cost: one string per request, no boxing, no intermediate allocation — the key is built in a
/// stack buffer.</para></summary>
internal static class GeoCacheKey
{
    /// <summary>Query parameters taking part in the key, in a fixed order. The first
    /// <see cref="ParamCount"/> chars of the key are the validity mask, one char per parameter.</summary>
    private const int ParamCount = 11;

    /// <summary>Raw (unparseable / out-of-range) values are embedded verbatim, truncated to this many
    /// chars — the length prefix always records the <em>original</em> length, so two values can only
    /// share a field if they agree on both their length and their first <see cref="MaxRawChars"/>
    /// chars. Such a pair is always marked <c>'x'</c> for that parameter, and for an endpoint that
    /// reads the parameter both requests are rejected with 400 (never cached); for an endpoint that
    /// ignores it, the parameter cannot influence the response either. Valid values are never
    /// truncated: they are re-formatted canonically first, and a valid geohash is at most 12 chars.
    /// </summary>
    private const int MaxRawChars = 40;

    /// <summary>A declared field length is clamped to this, so it always formats into at most 5
    /// digits. Kestrel's default request-line limit (8 KB) means a real query value cannot come close;
    /// the clamp only guarantees the buffer arithmetic below can never be exceeded.</summary>
    private const int MaxDeclaredLength = 99_999;

    /// <summary>Mask + one <c>|&lt;length&gt;:&lt;token&gt;</c> field per parameter. Per field:
    /// 1 separator + at most 5 length digits + 1 colon (7, rounded to 8) + at most
    /// <see cref="MaxRawChars"/> content — a canonical token is at most 24 chars, the "R" format of a
    /// <see cref="double"/>.</summary>
    private const int MaxKeyChars = ParamCount + ParamCount * (8 + MaxRawChars);

    /// <summary>Enough for the "R" format of any <see cref="double"/> (24 chars) or
    /// <see cref="int"/>.</summary>
    private const int MaxCanonicalChars = 32;

    private const char Absent = '-';
    private const char Valid = 'v';
    private const char Invalid = 'x';

    public static string Compute(HttpContext ctx)
    {
        var qs = (ctx.Request.QueryString.Value ?? "").AsSpan();
        Span<char> key = stackalloc char[MaxKeyChars];
        // two disjoint slices of one buffer: the fixed-width mask, then the length-prefixed fields
        var mask = key[..ParamCount];
        var fields = key[ParamCount..];
        var pos = 0;

        mask[0] = Coordinate(qs, "lat", 90, fields, ref pos);
        mask[1] = Coordinate(qs, "lon", 180, fields, ref pos);
        mask[2] = Coordinate(qs, "fromLat", 90, fields, ref pos);
        mask[3] = Coordinate(qs, "fromLon", 180, fields, ref pos);
        mask[4] = Coordinate(qs, "toLat", 90, fields, ref pos);
        mask[5] = Coordinate(qs, "toLon", 180, fields, ref pos);
        mask[6] = Integer(qs, "count", 1, 100, fields, ref pos);
        mask[7] = Double(qs, "radiusKm", 0, 500, minInclusive: false, quantize: false, fields, ref pos);
        mask[8] = Integer(qs, "minPopulation", 0, int.MaxValue, fields, ref pos);
        mask[9] = Integer(qs, "precision", 1, Geohash.MaxPrecision, fields, ref pos);
        mask[10] = Hash(qs, fields, ref pos);

        return new string(key[..(ParamCount + pos)]);
    }

    private static char Coordinate(ReadOnlySpan<char> qs, string name, double limit, Span<char> fields, ref int pos)
        => Double(qs, name, -limit, limit, minInclusive: true, quantize: true, fields, ref pos);

    private static char Double(ReadOnlySpan<char> qs, string name, double min, double max,
        bool minInclusive, bool quantize, Span<char> fields, ref int pos)
    {
        if (!QueryParams.TryGetRaw(qs, name, out var raw)) return WriteAbsent(fields, ref pos);
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value) || value > max || (minInclusive ? value < min : value <= min))
            return WriteRaw(raw, fields, ref pos);

        if (quantize) value = Math.Round(value, 3);
        Span<char> canonical = stackalloc char[MaxCanonicalChars];
        return value.TryFormat(canonical, out var written, "R", CultureInfo.InvariantCulture)
            ? WriteField(canonical[..written], fields, ref pos, Valid)
            : WriteRaw(raw, fields, ref pos); // unreachable with the buffer size above; fails closed
    }

    private static char Integer(ReadOnlySpan<char> qs, string name, int min, int max, Span<char> fields, ref int pos)
    {
        if (!QueryParams.TryGetRaw(qs, name, out var raw)) return WriteAbsent(fields, ref pos);
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < min || value > max)
            return WriteRaw(raw, fields, ref pos);

        Span<char> canonical = stackalloc char[MaxCanonicalChars];
        return value.TryFormat(canonical, out var written, provider: CultureInfo.InvariantCulture)
            ? WriteField(canonical[..written], fields, ref pos, Valid)
            : WriteRaw(raw, fields, ref pos);
    }

    private static char Hash(ReadOnlySpan<char> qs, Span<char> fields, ref int pos)
    {
        if (!QueryParams.TryGetRaw(qs, "hash", out var raw)) return WriteAbsent(fields, ref pos);
        // a geohash has no canonical rewrite, so it goes in raw either way; only the mask changes
        var valid = Geohash.TryDecode(raw, out _, out _, out _, out _);
        WriteRaw(raw, fields, ref pos);
        return valid ? Valid : Invalid;
    }

    private static char WriteAbsent(Span<char> fields, ref int pos)
        => WriteField(default, fields, ref pos, Absent);

    private static char WriteRaw(ReadOnlySpan<char> raw, Span<char> fields, ref int pos)
    {
        var length = Math.Min(raw.Length, MaxDeclaredLength); // the ORIGINAL length goes in the prefix
        if (raw.Length > MaxRawChars) raw = raw[..MaxRawChars];
        return WriteField(raw, fields, ref pos, Invalid, length);
    }

    /// <summary>Appends one <c>|&lt;length&gt;:&lt;token&gt;</c> field. The explicit length is what
    /// makes the key uniquely decodable even when a token contains the separator.</summary>
    private static char WriteField(ReadOnlySpan<char> token, Span<char> fields, ref int pos, char state,
        int declaredLength = -1)
    {
        fields[pos++] = '|';
        var length = declaredLength < 0 ? token.Length : declaredLength;
        if (!length.TryFormat(fields[pos..], out var lengthChars, provider: CultureInfo.InvariantCulture))
            throw new InvalidOperationException("cache-key buffer too small for a field length");
        pos += lengthChars;
        fields[pos++] = ':';
        token.CopyTo(fields[pos..]);
        pos += token.Length;
        return state;
    }
}
