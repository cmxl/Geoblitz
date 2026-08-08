using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace HighPerf.Api.Tests;

[Collection("api")]
public class CorsTests(ApiFixture fixture)
{
    [Fact]
    public async Task Preflight_FromDevOrigin_IsAllowed()
    {
        using var client = fixture.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Options, "/cities/nearest?lat=1&lon=1");
        req.Headers.Add("Origin", "http://localhost:4200");
        req.Headers.Add("Access-Control-Request-Method", "GET");
        var res = await client.SendAsync(req, TestContext.Current.CancellationToken);
        Assert.True(res.StatusCode is System.Net.HttpStatusCode.NoContent or System.Net.HttpStatusCode.OK);
        Assert.Equal("http://localhost:4200", res.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task SimpleRequest_FromDevOrigin_ExposesTimingHeaders()
    {
        using var client = fixture.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/distance?fromLat=1&fromLon=1&toLat=2&toLon=2");
        req.Headers.Add("Origin", "http://localhost:4200");
        var res = await client.SendAsync(req, TestContext.Current.CancellationToken);
        res.EnsureSuccessStatusCode();
        var exposed = string.Join(",", res.Headers.GetValues("Access-Control-Expose-Headers"));
        Assert.Contains("X-Compute-Count", exposed);
        Assert.Contains("Server-Timing", exposed);
    }

    [Fact]
    public async Task ForeignOrigin_GetsNoCorsHeaders()
    {
        using var client = fixture.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        req.Headers.Add("Origin", "https://evil.example");
        var res = await client.SendAsync(req, TestContext.Current.CancellationToken);
        Assert.False(res.Headers.Contains("Access-Control-Allow-Origin"));
    }

    // Uses a locally-created WebApplicationFactory (not the shared "api" collection fixture,
    // which runs in Development) so it can pin the Production-only environment without
    // affecting the other tests in the collection. Spinning up a second factory reloads the
    // geo database and costs roughly 1-2 extra seconds in this test.
    [Fact]
    public async Task ProductionEnvironment_GetsNoCorsHeadersForDevOrigin()
    {
        await using var prodFactory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("environment", "Production"));
        using var client = prodFactory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        req.Headers.Add("Origin", "http://localhost:4200");
        var res = await client.SendAsync(req, TestContext.Current.CancellationToken);
        res.EnsureSuccessStatusCode();
        Assert.False(res.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
