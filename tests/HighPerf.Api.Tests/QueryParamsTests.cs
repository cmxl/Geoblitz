using Xunit;

namespace HighPerf.Api.Tests;

/// <summary>Direct unit coverage for <see cref="QueryParams"/>, the allocation-free query-string
/// scanner every endpoint parses through. Existing tests exercise it only indirectly, through full
/// HTTP requests and cache-key composition — these pin the raw-scanning edge cases (duplicate keys,
/// empty values, degenerate query strings, pathological lengths) that those higher-level tests don't
/// specifically target.</summary>
[Collection("api")]
public class QueryParamsTests
{
    [Fact]
    public void DuplicateKeys_ReturnsTheFirstOccurrence()
    {
        // Kestrel does not reject a repeated query key; the hand-rolled scanner (unlike
        // ASP.NET Core's Query dictionary, which would combine both into StringValues) must resolve
        // it deterministically — first match wins.
        Assert.True(QueryParams.TryGetRaw("?lat=1&lat=2".AsSpan(), "lat".AsSpan(), out var raw));
        Assert.Equal("1", raw.ToString());

        Assert.True(QueryParams.TryGetDouble("?lat=1&lat=2".AsSpan(), "lat".AsSpan(), out var value));
        Assert.Equal(1.0, value);
    }

    [Fact]
    public void EmptyValue_ParsesAsPresentButNotANumber()
    {
        Assert.True(QueryParams.TryGetRaw("?lat=&lon=2".AsSpan(), "lat".AsSpan(), out var raw));
        Assert.Equal(0, raw.Length);
        Assert.False(QueryParams.TryGetDouble("?lat=&lon=2".AsSpan(), "lat".AsSpan(), out _));
        Assert.False(QueryParams.TryGetInt("?count=&lat=2".AsSpan(), "count".AsSpan(), out _));
    }

    [Fact]
    public void QuestionMarkOnly_FindsNothing()
    {
        Assert.False(QueryParams.TryGetRaw("?".AsSpan(), "lat".AsSpan(), out _));
        Assert.False(QueryParams.TryGetDouble("?".AsSpan(), "lat".AsSpan(), out _));
    }

    [Fact]
    public void EmptyQueryString_FindsNothing()
    {
        Assert.False(QueryParams.TryGetRaw("".AsSpan(), "lat".AsSpan(), out _));
        Assert.False(QueryParams.TryGetRaw(default, "lat".AsSpan(), out _));
    }

    [Fact]
    public void MissingLeadingQuestionMark_StillParses()
    {
        // Program.cs always passes ctx.Request.QueryString.Value, which includes the leading '?',
        // but the scanner does not require it — pin that it degrades gracefully either way.
        Assert.True(QueryParams.TryGetDouble("lat=52.5&lon=13.4".AsSpan(), "lat".AsSpan(), out var lat));
        Assert.Equal(52.5, lat);
    }

    [Fact]
    public void PairWithoutEqualsSign_IsSkipped()
    {
        // "lat" with no '=' has no separator, so it must be skipped rather than matched or throwing;
        // "lon" after it must still be found.
        Assert.False(QueryParams.TryGetRaw("?lat&lon=2".AsSpan(), "lat".AsSpan(), out _));
        Assert.True(QueryParams.TryGetRaw("?lat&lon=2".AsSpan(), "lon".AsSpan(), out var raw));
        Assert.Equal("2", raw.ToString());
    }

    [Fact]
    public void TrailingAmpersand_DoesNotThrow()
    {
        Assert.True(QueryParams.TryGetDouble("?lat=1&lon=2&".AsSpan(), "lon".AsSpan(), out var lon));
        Assert.Equal(2.0, lon);
    }

    [Fact]
    public void ConsecutiveAmpersands_SkipTheEmptyPairBetweenThem()
    {
        Assert.True(QueryParams.TryGetDouble("?lat=1&&lon=2".AsSpan(), "lon".AsSpan(), out var lon));
        Assert.Equal(2.0, lon);
    }

    [Fact]
    public void ExtremelyLongQueryString_FindsTargetWithoutThrowingOrHanging()
    {
        // pathological input: ~200k chars of unrelated noise parameters ahead of the real one.
        var sb = new System.Text.StringBuilder("?");
        for (var i = 0; i < 20_000; i++) sb.Append("noise").Append(i).Append("=x&");
        sb.Append("lat=48.1374&lon=11.5755");
        var qs = sb.ToString();

        Assert.True(QueryParams.TryGetDouble(qs.AsSpan(), "lat".AsSpan(), out var lat));
        Assert.Equal(48.1374, lat);
        Assert.True(QueryParams.TryGetDouble(qs.AsSpan(), "lon".AsSpan(), out var lon));
        Assert.Equal(11.5755, lon);
        Assert.False(QueryParams.TryGetRaw(qs.AsSpan(), "doesnotexist".AsSpan(), out _));
    }

    [Fact]
    public void OverlongSingleValue_ParsesWithoutThrowing()
    {
        var qs = "?lat=" + new string('9', 100_000) + "&lon=2";
        Assert.True(QueryParams.TryGetRaw(qs.AsSpan(), "lat".AsSpan(), out var raw));
        Assert.Equal(100_000, raw.Length);
        // not a valid double (out of range for double parsing precision aside, this is still a
        // syntactically parseable — if enormous — numeral); the important part is no exception.
        QueryParams.TryGetDouble(qs.AsSpan(), "lat".AsSpan(), out _);
    }
}
