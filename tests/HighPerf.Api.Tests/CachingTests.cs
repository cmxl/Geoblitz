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
