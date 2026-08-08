using System.Net;
using Xunit;

namespace HighPerf.Api.Tests;

/// <summary>Regression tests for M2/C1: the output cache must never be able to replay a cached
/// 200 for a request that handler validation would reject.
/// <para>Every case issues the <b>valid</b> request first — populating the cache — and only then the
/// invalid variant on the same coordinates. That order is what reproduced the bypass in the M2
/// review: the cache lookup happens in middleware, before the handler's validation runs, so a
/// shared cache key means the invalid request never reaches the code that would 400 it.</para></summary>
[Collection("api")]
public class CacheValidationTests(ApiFixture fixture)
{
    [Theory]
    // /cities/nearest — "count" absent (handler default 5) vs present-but-invalid
    [InlineData("/cities/nearest?lat=44.111&lon=7.111",
                "/cities/nearest?lat=44.111&lon=7.111&count=abc")]
    [InlineData("/cities/nearest?lat=44.112&lon=7.112",
                "/cities/nearest?lat=44.112&lon=7.112&count=0")]
    [InlineData("/cities/nearest?lat=44.113&lon=7.113",
                "/cities/nearest?lat=44.113&lon=7.113&count=")]
    [InlineData("/cities/nearest?lat=44.114&lon=7.114&count=5",
                "/cities/nearest?lat=44.114&lon=7.114&count=101")]
    // /geohash/encode — "precision" absent (handler default 9) vs present-but-invalid
    [InlineData("/geohash/encode?lat=12.345&lon=23.456",
                "/geohash/encode?lat=12.345&lon=23.456&precision=0")]
    [InlineData("/geohash/encode?lat=12.346&lon=23.457",
                "/geohash/encode?lat=12.346&lon=23.457&precision=abc")]
    [InlineData("/geohash/encode?lat=12.347&lon=23.458&precision=9",
                "/geohash/encode?lat=12.347&lon=23.458&precision=13")]
    // /cities/within — "minPopulation" absent (handler default 0) vs present-but-invalid
    [InlineData("/cities/within?lat=45.211&lon=8.211&radiusKm=50",
                "/cities/within?lat=45.211&lon=8.211&radiusKm=50&minPopulation=abc")]
    [InlineData("/cities/within?lat=45.212&lon=8.212&radiusKm=50",
                "/cities/within?lat=45.212&lon=8.212&radiusKm=50&minPopulation=-1")]
    // /cities/within — radiusKm out of range after a valid request on the same coordinates
    [InlineData("/cities/within?lat=45.213&lon=8.213&radiusKm=500",
                "/cities/within?lat=45.213&lon=8.213&radiusKm=501")]
    [InlineData("/cities/within?lat=45.214&lon=8.214&radiusKm=50",
                "/cities/within?lat=45.214&lon=8.214&radiusKm=abc")]
    // coordinates that quantize into an already-cached bucket but are themselves out of range
    [InlineData("/cities/nearest?lat=90&lon=10.5",
                "/cities/nearest?lat=90.0004&lon=10.5")]
    [InlineData("/geohash/encode?lat=-90&lon=11.5",
                "/geohash/encode?lat=-90.0002&lon=11.5")]
    [InlineData("/distance?fromLat=10.5&fromLon=20.5&toLat=30.5&toLon=180",
                "/distance?fromLat=10.5&fromLon=20.5&toLat=30.5&toLon=180.0004")]
    // /geohash/decode — a cached valid hash must not answer for an invalid one
    [InlineData("/geohash/decode?hash=u33dc0", "/geohash/decode?hash=u33dc0!")]
    public async Task CachedValidResponse_IsNotReplayedForInvalidVariant(string valid, string invalid)
    {
        using var client = fixture.CreateClient();
        var ok = await client.GetAsync(valid, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode); // populates the cache
        var bad = await client.GetAsync(invalid, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Theory]
    // the reverse order must keep working too: a 400 is never cached, so the following valid
    // request must still be computed and answered with 200
    [InlineData("/cities/nearest?lat=46.311&lon=9.311&count=abc", "/cities/nearest?lat=46.311&lon=9.311")]
    [InlineData("/geohash/encode?lat=13.345&lon=24.456&precision=0", "/geohash/encode?lat=13.345&lon=24.456")]
    [InlineData("/cities/within?lat=46.312&lon=9.312&radiusKm=50&minPopulation=abc",
                "/cities/within?lat=46.312&lon=9.312&radiusKm=50")]
    public async Task InvalidRequest_DoesNotPoisonTheValidVariant(string invalid, string valid)
    {
        using var client = fixture.CreateClient();
        var bad = await client.GetAsync(invalid, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var ok = await client.GetAsync(valid, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task EquivalentValidRequests_StillShareOneCacheEntry()
    {
        // the validity-aware key must not fragment requests that are genuinely equivalent:
        // the same canonical values written differently still hit the same entry
        using var client = fixture.CreateClient();
        var a = await client.GetAsync("/cities/nearest?lat=47.401&lon=9.401&count=3",
            TestContext.Current.CancellationToken);
        var b = await client.GetAsync("/cities/nearest?lat=47.4010&lon=9.4010&count=03",
            TestContext.Current.CancellationToken);
        a.EnsureSuccessStatusCode();
        b.EnsureSuccessStatusCode();
        Assert.Equal(Assert.Single(a.Headers.GetValues("X-Compute-Count")),
                     Assert.Single(b.Headers.GetValues("X-Compute-Count")));
    }
}
