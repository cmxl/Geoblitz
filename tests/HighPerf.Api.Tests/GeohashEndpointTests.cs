using System.Net;
using System.Text.Json;
using Xunit;

namespace HighPerf.Api.Tests;

[Collection("api")]
public class GeohashEndpointTests(ApiFixture fixture)
{
    [Fact]
    public async Task Encode_KnownVector()
    {
        using var client = fixture.CreateClient();
        var res = await client.GetAsync("/geohash/encode?lat=57.64911&lon=10.40744&precision=11",
            TestContext.Current.CancellationToken);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("u4pruydqqvj", doc.RootElement.GetProperty("geohash").GetString());
    }

    [Fact]
    public async Task Encode_DefaultPrecision_Is9()
    {
        using var client = fixture.CreateClient();
        var res = await client.GetAsync("/geohash/encode?lat=48.1374&lon=11.5755",
            TestContext.Current.CancellationToken);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(9, doc.RootElement.GetProperty("geohash").GetString()!.Length);
    }

    [Fact]
    public async Task Decode_KnownVector()
    {
        using var client = fixture.CreateClient();
        var res = await client.GetAsync("/geohash/decode?hash=u4pruydqqvj", TestContext.Current.CancellationToken);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(57.64911, doc.RootElement.GetProperty("lat").GetDouble(), 4);
        Assert.Equal(10.40744, doc.RootElement.GetProperty("lon").GetDouble(), 4);
        Assert.True(doc.RootElement.GetProperty("latError").GetDouble() > 0);
    }

    [Theory]
    [InlineData("/geohash/encode?lat=91&lon=0")]
    [InlineData("/geohash/encode?lat=0&lon=0&precision=0")]
    [InlineData("/geohash/encode?lat=0&lon=0&precision=13")]
    [InlineData("/geohash/decode")]
    [InlineData("/geohash/decode?hash=aaa")]
    public async Task InvalidInput_Returns400(string url)
    {
        using var client = fixture.CreateClient();
        var res = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
