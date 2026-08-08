using Xunit;

namespace HighPerf.Api.Tests;

[Collection("api")]
public class CachingTests(ApiFixture fixture)
{
    private static string CountHeader(HttpResponseMessage res)
        => Assert.Single(res.Headers.GetValues("X-Compute-Count"));

    [Fact]
    public async Task IdenticalRequests_SecondIsServedFromCache()
    {
        using var client = fixture.CreateClient();
        const string url = "/cities/nearest?lat=50.001&lon=8.001&count=7";
        var first = await client.GetAsync(url, TestContext.Current.CancellationToken);
        var second = await client.GetAsync(url, TestContext.Current.CancellationToken);
        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();
        Assert.Equal(CountHeader(first), CountHeader(second)); // replayed header == cache hit
        Assert.Equal(await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
                     await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NearbyCoordinates_SameQuantizedBucket_ShareCacheEntry()
    {
        using var client = fixture.CreateClient();
        var a = await client.GetAsync("/cities/nearest?lat=51.00010&lon=9.00010&count=3",
            TestContext.Current.CancellationToken);
        var b = await client.GetAsync("/cities/nearest?lat=51.00012&lon=9.00012&count=3",
            TestContext.Current.CancellationToken); // rounds to same 3-decimal bucket
        Assert.Equal(CountHeader(a), CountHeader(b));
    }

    [Fact]
    public async Task DifferentCount_IsACacheMiss()
    {
        using var client = fixture.CreateClient();
        var a = await client.GetAsync("/cities/nearest?lat=52.100&lon=10.100&count=3",
            TestContext.Current.CancellationToken);
        var b = await client.GetAsync("/cities/nearest?lat=52.100&lon=10.100&count=4",
            TestContext.Current.CancellationToken);
        Assert.NotEqual(CountHeader(a), CountHeader(b));
    }

    [Theory]
    [InlineData("/cities/nearest?lat=41.421&lon=2.221&count=4")]
    [InlineData("/cities/within?lat=41.422&lon=2.222&radiusKm=40")]
    public async Task CachedReplay_IsByteIdenticalToTheComputedResponse(string url)
    {
        // The riskiest seam of the output-cache setup: the handler writes straight to
        // Response.BodyWriter, and the middleware has to capture and replay exactly those bytes.
        // NOTE: this test deliberately does not assert the response framing. TestServer buffers the
        // body and HttpContentHeaders.ContentLength falls back to the buffered length, so it reports
        // a Content-Length whether or not the server declared one — see ResponseFramingTests for the
        // assertion that actually pins Content-Length down.
        using var client = fixture.CreateClient();
        var miss = await client.GetAsync(url, TestContext.Current.CancellationToken);
        var hit = await client.GetAsync(url, TestContext.Current.CancellationToken);
        miss.EnsureSuccessStatusCode();
        hit.EnsureSuccessStatusCode();
        Assert.Equal(CountHeader(miss), CountHeader(hit)); // the second one is the cached replay
        var missBody = await miss.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        var hitBody = await hit.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(missBody, hitBody);
        Assert.Equal(miss.Content.Headers.ContentType!.ToString(), hit.Content.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task DifferentBucket_IsACacheMiss()
    {
        using var client = fixture.CreateClient();
        var a = await client.GetAsync("/cities/nearest?lat=53.101&lon=10.100&count=3",
            TestContext.Current.CancellationToken);
        var b = await client.GetAsync("/cities/nearest?lat=53.109&lon=10.100&count=3",
            TestContext.Current.CancellationToken);
        Assert.NotEqual(CountHeader(a), CountHeader(b));
    }
}
