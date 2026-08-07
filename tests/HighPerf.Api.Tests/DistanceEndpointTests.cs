using System.Net;
using System.Text.Json;
using Xunit;

namespace HighPerf.Api.Tests;

[Collection("api")]
public class DistanceEndpointTests(ApiFixture fixture)
{
    [Fact]
    public async Task BerlinToMunich_ReturnsAbout504()
    {
        using var client = fixture.CreateClient();
        var res = await client.GetAsync("/distance?fromLat=52.52&fromLon=13.405&toLat=48.1374&toLon=11.5755",
            TestContext.Current.CancellationToken);
        res.EnsureSuccessStatusCode();
        Assert.Equal("application/json", res.Content.Headers.ContentType!.MediaType);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.InRange(doc.RootElement.GetProperty("kilometers").GetDouble(), 503.0, 506.0);
    }

    [Theory]
    [InlineData("/distance")]                                                    // all missing
    [InlineData("/distance?fromLat=91&fromLon=0&toLat=0&toLon=0")]               // lat out of range
    [InlineData("/distance?fromLat=0&fromLon=181&toLat=0&toLon=0")]              // lon out of range
    [InlineData("/distance?fromLat=abc&fromLon=0&toLat=0&toLon=0")]              // not a number
    public async Task InvalidInput_Returns400Problem(string url)
    {
        using var client = fixture.CreateClient();
        var res = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType!.MediaType);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(400, doc.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("detail").GetString()));
    }
}
