using System.Net;
using System.Text.Json;
using Xunit;

namespace Geoblitz.Api.Tests;

[Collection("api")]
public class CitiesEndpointTests(ApiFixture fixture)
{
    [Fact]
    public async Task Nearest_Berlin_ReturnsBerlinFirst_SortedAscending()
    {
        using var client = fixture.CreateClient();
        // NOTE: query point is the GeoNames coordinate for the "Berlin" city record itself
        // (52.52437, 13.41053), not the commonly-cited 52.5200/13.4050 landmark coordinate.
        // The embedded GeoNames dataset also lists Berlin's boroughs (e.g. "Mitte", ~8m from
        // 52.5200/13.4050) which are genuinely closer than the "Berlin" record's own point
        // (~612m away) at that coordinate — see FindNearestTests.RealDataset_NearestToBerlin_IsBerlin
        // for the same rationale.
        var res = await client.GetAsync("/cities/nearest?lat=52.52437&lon=13.41053&count=5",
            TestContext.Current.CancellationToken);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = doc.RootElement;
        Assert.Equal(5, root.GetProperty("count").GetInt32());
        var cities = root.GetProperty("cities");
        Assert.Equal(5, cities.GetArrayLength());
        Assert.Equal("Berlin", cities[0].GetProperty("name").GetString());
        Assert.Equal("DE", cities[0].GetProperty("country").GetString());
        Assert.True(cities[0].GetProperty("population").GetInt32() > 1_000_000);
        var prev = -1.0;
        foreach (var c in cities.EnumerateArray())
        {
            var d = c.GetProperty("distanceKm").GetDouble();
            Assert.True(d >= prev);
            prev = d;
        }
    }

    [Fact]
    public async Task Nearest_DefaultCount_Is5()
    {
        using var client = fixture.CreateClient();
        var res = await client.GetAsync("/cities/nearest?lat=48.1374&lon=11.5755",
            TestContext.Current.CancellationToken);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(5, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Within_Munich30km_MinPopulationFilters()
    {
        using var client = fixture.CreateClient();
        var res = await client.GetAsync("/cities/within?lat=48.1374&lon=11.5755&radiusKm=30&minPopulation=1000000",
            TestContext.Current.CancellationToken);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var cities = doc.RootElement.GetProperty("cities");
        Assert.True(cities.GetArrayLength() >= 1);
        foreach (var c in cities.EnumerateArray())
            Assert.True(c.GetProperty("population").GetInt32() >= 1_000_000);
    }

    [Theory]
    [InlineData("/cities/nearest?lat=91&lon=0")]
    [InlineData("/cities/nearest?lat=0&lon=0&count=0")]
    [InlineData("/cities/nearest?lat=0&lon=0&count=101")]
    [InlineData("/cities/within?lat=0&lon=0")]                       // radius missing
    [InlineData("/cities/within?lat=0&lon=0&radiusKm=0")]
    [InlineData("/cities/within?lat=0&lon=0&radiusKm=501")]
    [InlineData("/cities/within?lat=0&lon=0&radiusKm=10&minPopulation=-1")]
    // non-finite values parse as doubles but compare false against every range bound, so they must
    // be rejected explicitly rather than silently answered with an empty result set
    [InlineData("/cities/nearest?lat=NaN&lon=0")]
    [InlineData("/cities/nearest?lat=0&lon=NaN")]
    [InlineData("/cities/nearest?lat=Infinity&lon=0")]
    [InlineData("/cities/within?lat=NaN&lon=0&radiusKm=10")]
    [InlineData("/cities/within?lat=0&lon=0&radiusKm=NaN")]
    public async Task InvalidInput_Returns400(string url)
    {
        using var client = fixture.CreateClient();
        var res = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Names_WithNonAscii_AreValidJson()
    {
        using var client = fixture.CreateClient();
        // São Paulo region — exercises UTF-8 name blob escaping
        var res = await client.GetAsync("/cities/nearest?lat=-23.5475&lon=-46.63611&count=3",
            TestContext.Current.CancellationToken);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("São Paulo", doc.RootElement.GetProperty("cities")[0].GetProperty("name").GetString());
    }
}
