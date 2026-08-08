using Microsoft.AspNetCore.Http;
using Xunit;

namespace Geoblitz.Api.Tests;

/// <summary>Unit tests for the output-cache key. These complement
/// <see cref="CacheValidationTests"/>: the collision cases below cannot be reproduced through
/// <c>HttpClient</c>, because <c>Uri</c> percent-encodes the <c>|</c> they depend on — but Kestrel
/// accepts a literal <c>|</c> in a query string, so they are reachable from a real client.
/// <para>Part of the <c>api</c> collection so it stays serialized with
/// <see cref="AllocationTests"/>, whose process-wide allocation measurement must not race other
/// tests.</para></summary>
[Collection("api")]
public class GeoCacheKeyTests
{
    private static string Key(string queryString)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString(queryString);
        return GeoCacheKey.Compute(ctx);
    }

    [Theory]
    // A value containing the field separator must not be able to shift the following field
    // boundaries and so reproduce another request's key. Left request is answered 200 (an
    // out-of-range/NaN-free read that the handler accepts), right request must be 400 — with
    // un-delimited fields both produced the identical key "xxx--------|NaN|NaN|A|B||||||||".
    [InlineData("?lat=NaN&lon=NaN&fromLat=A|B", "?lat=NaN|NaN&lon=A&fromLat=B")]
    // Two *valid-input* requests whose fields would otherwise alias — the second asks for a
    // different radius and minPopulation, so sharing an entry would serve plainly wrong data.
    [InlineData("?lat=41.4&lon=2.2&count=A&radiusKm=50&minPopulation=70&precision=7|X",
                "?lat=41.4&lon=2.2&count=A|50&radiusKm=70&minPopulation=7&precision=X")]
    // separator inside the geohash, which is embedded raw
    [InlineData("?hash=u33dc0&lat=1|2", "?hash=u33dc0&lat=1|2&lon=")]
    public void SeparatorInsideAValue_CannotForgeAnotherRequestsKey(string a, string b)
        => Assert.NotEqual(Key(a), Key(b));

    [Theory]
    // absent vs present-but-invalid vs valid must all be distinct
    [InlineData("?lat=1&lon=2", "?lat=1&lon=2&count=abc")]
    [InlineData("?lat=1&lon=2", "?lat=1&lon=2&count=0")]
    [InlineData("?lat=1&lon=2", "?lat=1&lon=2&count=")]
    [InlineData("?lat=1&lon=2&count=5", "?lat=1&lon=2&count=abc")]
    [InlineData("?lat=90&lon=2", "?lat=90.0004&lon=2")]      // quantizes into the valid bucket
    [InlineData("?lat=1&lon=2", "?lat=NaN&lon=2")]           // non-finite is invalid, not "0"
    [InlineData("?lat=1&lon=2&radiusKm=500", "?lat=1&lon=2&radiusKm=501")]
    [InlineData("?hash=u33dc0", "?hash=u33dc0z!")]
    public void InvalidAndValidVariants_GetDifferentKeys(string valid, string invalid)
        => Assert.NotEqual(Key(valid), Key(invalid));

    [Theory]
    // equivalent spellings must still share one cache entry
    [InlineData("?lat=47.401&lon=9.401&count=3", "?lat=47.4010&lon=9.4010&count=03")]
    [InlineData("?lat=47.401&lon=9.401&count=3", "?lat=47.40105&lon=9.40104&count=3")] // same bucket
    [InlineData("?lat=47.401&lon=9.401", "?LAT=47.401&LON=9.401")]                     // names are case-insensitive
    public void EquivalentRequests_ShareOneKey(string a, string b)
        => Assert.Equal(Key(a), Key(b));

    [Fact]
    public void OverlongValues_DoNotOverflowTheKeyBuffer()
    {
        // raw values are truncated into the key, but the length prefix keeps different lengths
        // distinct; neither may throw, and all of these are invalid input anyway
        var a = Key("?lat=" + new string('9', 4000) + "&lon=2");
        var b = Key("?lat=" + new string('9', 4001) + "&lon=2");
        Assert.NotEqual(a, b);
        Assert.NotEqual(Key("?lat=1&lon=2"), a);
    }
}
