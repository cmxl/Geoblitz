using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace HighPerf.Api.Tests;

// Single-origin hosting: the API serves the built Angular console from wwwroot when present
// (tools/publish-web.ps1 populates it). These tests build their own WebApplicationFactory
// instances pointed at a throwaway temp directory instead of injecting the shared ApiFixture,
// because each test needs a different WebRootPath - a setting the shared fixture doesn't
// (and shouldn't) vary. They still declare [Collection("api")], like every other class in
// this assembly, purely to stay serialized with AllocationTests: that suite's precise,
// process-wide GC measurement (GC.GetTotalAllocatedBytes) would otherwise be polluted by the
// large one-time GeoDatabase load each fresh WebApplicationFactory here performs, if xUnit
// ran this class's default collection in parallel with "api".
[Collection("api")]
public class StaticHostingTests : IDisposable
{
    private readonly string _webRoot;

    public StaticHostingTests()
    {
        _webRoot = Path.Combine(Path.GetTempPath(), "highperf-wwwroot-" + Guid.NewGuid());
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"),
            "<!doctype html><html><head><title>Flight Deck</title></head>" +
            "<body>Flight Deck console</body></html>");
        File.WriteAllText(Path.Combine(_webRoot, "main-ABC123.js"),
            "console.log('flight deck bundle');");
    }

    public void Dispose() => Directory.Delete(_webRoot, recursive: true);

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseWebRoot(_webRoot));

    [Fact]
    public async Task Root_ServesIndexHtml_WithNoCacheHeader()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var res = await client.GetAsync("/", TestContext.Current.CancellationToken);

        res.EnsureSuccessStatusCode();
        Assert.Equal("text/html", res.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(res.Headers.CacheControl);
        Assert.True(res.Headers.CacheControl!.NoCache);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Flight Deck console", body);
    }

    [Fact]
    public async Task HashedAsset_ServedWithImmutableLongLivedCacheHeader()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var res = await client.GetAsync("/main-ABC123.js", TestContext.Current.CancellationToken);

        res.EnsureSuccessStatusCode();
        var cc = res.Headers.CacheControl;
        Assert.NotNull(cc);
        Assert.True(cc!.Public);
        Assert.Equal(TimeSpan.FromSeconds(31536000), cc.MaxAge);
        Assert.Contains(cc.Extensions, e => e.Name == "immutable");
    }

    [Fact]
    public async Task GeoEndpoints_StillWork_WhenStaticHostingIsEnabled()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var res = await client.GetAsync("/cities/nearest?lat=48.1374&lon=11.5755&count=3",
            TestContext.Current.CancellationToken);

        res.EnsureSuccessStatusCode();
        Assert.Equal("application/json", res.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Root_404s_WhenWebRootDirectoryDoesNotExist()
    {
        // A fresh checkout (and CI, which never runs tools/publish-web.ps1) has no wwwroot
        // directory at all. Point WebRootPath at a guaranteed-nonexistent path rather than
        // relying on src/HighPerf.Api/wwwroot's ambient state on disk, since a developer who
        // already ran the publish script locally would otherwise make this test flaky.
        var missingWebRoot = Path.Combine(Path.GetTempPath(), "highperf-missing-wwwroot-" + Guid.NewGuid());
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseWebRoot(missingWebRoot));
        using var client = factory.CreateClient();

        var res = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
