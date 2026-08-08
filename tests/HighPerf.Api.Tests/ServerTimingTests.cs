using System.Text.RegularExpressions;
using Xunit;

namespace HighPerf.Api.Tests;

[Collection("api")]
public partial class ServerTimingTests(ApiFixture fixture)
{
    [GeneratedRegex(@"^engine;dur=\d+\.\d{3}$")]
    private static partial Regex TimingPattern();

    [Theory]
    [InlineData("/distance?fromLat=52.52&fromLon=13.405&toLat=48.1374&toLon=11.5755")]
    [InlineData("/cities/nearest?lat=47.001&lon=15.001&count=3")]
    [InlineData("/cities/within?lat=47.002&lon=15.002&radiusKm=40")]
    [InlineData("/geohash/encode?lat=47.003&lon=15.003")]
    [InlineData("/geohash/decode?hash=u4pruydqqvj")]
    public async Task GeoEndpoints_EmitServerTimingHeader(string url)
    {
        using var client = fixture.CreateClient();
        var res = await client.GetAsync(url, TestContext.Current.CancellationToken);
        res.EnsureSuccessStatusCode();
        var value = Assert.Single(res.Headers.GetValues("Server-Timing"));
        Assert.Matches(TimingPattern(), value);
    }

    [Fact]
    public async Task CacheHit_ReplaysOriginalTimingValue()
    {
        using var client = fixture.CreateClient();
        const string url = "/cities/nearest?lat=46.501&lon=14.501&count=4";
        var first = await client.GetAsync(url, TestContext.Current.CancellationToken);
        var second = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(first.Headers.GetValues("Server-Timing").Single(),
                     second.Headers.GetValues("Server-Timing").Single());
        Assert.Equal(first.Headers.GetValues("X-Compute-Count").Single(),
                     second.Headers.GetValues("X-Compute-Count").Single()); // proves it was a replay
    }

    [Fact]
    public async Task Healthz_HasNoServerTiming()
    {
        using var client = fixture.CreateClient();
        var res = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);
        Assert.False(res.Headers.Contains("Server-Timing"));
    }

    [Fact]
    public async Task ValidationError_HasNoServerTiming()
    {
        using var client = fixture.CreateClient();
        // lat out of range -> 400 before the compute call, so no engine timing should be emitted.
        var res = await client.GetAsync("/cities/nearest?lat=999&lon=1", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, res.StatusCode);
        Assert.False(res.Headers.Contains("Server-Timing"));
    }
}
