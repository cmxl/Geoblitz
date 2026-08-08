using System.Diagnostics;
using System.IO.Pipelines;
using HighPerf.Geo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HighPerf.Api.Tests;

/// <summary>M2/I3 — measured allocation proof for the API hot path.
/// <para><b>What this proves.</b> <see cref="HotPath_InProcess_AllocatesOnlyTheDocumentedStrings"/>
/// replays the exact synchronous sequence of the <c>/cities/*</c> endpoint lambdas (cache-key
/// composition → span query parsing → validation → grid query → <see cref="CityJson.WriteCities"/> →
/// <c>BodyWriter.FlushAsync</c>) against a real <see cref="HttpResponse"/> whose body writer is a
/// <see cref="PipeWriter"/> over <see cref="Stream.Null"/>, and measures
/// <c>GC.GetAllocatedBytesForCurrentThread()</c> across it. Because the whole sequence
/// runs on one thread with no awaits that suspend, this is an exact, noise-free number: it pins the
/// per-request managed allocation of <em>our</em> code to the documented handful of small strings
/// (the <c>X-Compute-Count</c> value, the <c>GeoCacheKey</c> string, the <c>Content-Length</c>
/// header value, the <c>Server-Timing</c> header value) — no per-city, per-hit or per-byte
/// allocation.</para>
/// <para><b>What it does not prove.</b> It does not measure Kestrel, routing, output caching or the
/// HTTP/1.1 framing around the handler. <see cref="Endpoints_ViaTestServer_StayUnderCeiling"/> covers
/// the end-to-end in-process request instead, but there the TestServer + HttpClient harness
/// (request/response objects, pipes, header dictionaries, the test client's own buffers) dominates
/// the number by an order of magnitude, so it is a <em>regression tripwire</em> — it would catch
/// someone reaching for <c>ctx.Request.Query[...]</c> or a LINQ projection in a handler — and an
/// honestly-labelled figure, not a claim about Kestrel's real per-request cost.</para></summary>
[Collection("api")]
public class AllocationTests(ApiFixture fixture, ITestOutputHelper output)
{
    /// <summary>Ceiling for the in-process hot path. The measured value is 256 B/request, all of it
    /// the documented small strings (including the Server-Timing header value added in the web
    /// flight-deck task); the ceiling leaves headroom for runtime/JIT variation while
    /// still tripping on any per-city or per-byte allocation (a single <c>ctx.Request.Query</c>
    /// materialization or one boxed value per city is far above it).</summary>
    private const long HotPathCeilingBytes = 512;

    /// <summary>Ceiling for the full TestServer round trip, harness overhead included. See the class
    /// remarks: the measured value is ~100 KB/request and is dominated by TestServer + HttpClient
    /// (a fresh HttpContext, DI scope, two pipes, header dictionaries and the client's own
    /// request/response objects per call), not by the endpoint. It is therefore a coarse
    /// gross-regression guard; <see cref="HotPathCeilingBytes"/> is the tight one.</summary>
    private const long TestServerCeilingBytes = 160 * 1024;

    [Fact]
    public void HotPath_InProcess_AllocatesOnlyTheDocumentedStrings()
    {
        var db = fixture.Services.GetRequiredService<GeoDatabase>();
        var counter = new ComputeCounter();
        var ctx = NewContext("?lat=52.52&lon=13.405&count=10");

        Assert.True(Nearest(ctx, db, counter).IsCompleted); // no thread hop, so per-thread counting is exact
        for (var i = 0; i < 500; i++) Nearest(ctx, db, counter); // warm up JIT, ArrayPool, header dictionary

        const int iterations = 2000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++) Nearest(ctx, db, counter);
        var perRequest = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)iterations;

        output.WriteLine($"/cities/nearest hot path: {perRequest:F1} B/request (in-process, single thread)");
        Assert.True(perRequest < HotPathCeilingBytes,
            $"hot path allocated {perRequest:F1} B/request, ceiling is {HotPathCeilingBytes} B");
    }

    [Theory]
    [InlineData("/cities/nearest?lat={0}&lon=9.5&count=10")]
    [InlineData("/cities/within?lat={0}&lon=9.5&radiusKm=50")]
    public async Task Endpoints_ViaTestServer_StayUnderCeiling(string template)
    {
        using var client = fixture.CreateClient();
        // distinct coordinates per request so every request is a cache miss and therefore actually
        // executes the compute path (a cached replay would measure the cache, not the endpoint)
        for (var i = 0; i < 20; i++) await Request(client, template, 30.0 + i * 0.01);

        const int requests = 200;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        for (var i = 0; i < requests; i++) await Request(client, template, 40.0 + i * 0.01);
        var perRequest = (GC.GetTotalAllocatedBytes(precise: true) - before) / (double)requests;

        // for context, the same measurement on the output-cache replay path (one repeated URL)
        await Request(client, template, 39.5);
        var beforeReplay = GC.GetTotalAllocatedBytes(precise: true);
        for (var i = 0; i < requests; i++) await Request(client, template, 39.5);
        var perReplay = (GC.GetTotalAllocatedBytes(precise: true) - beforeReplay) / (double)requests;

        output.WriteLine($"{template}: {perRequest:F0} B/request compute, {perReplay:F0} B/request " +
                         "cached replay — end-to-end, TestServer + HttpClient overhead included");
        Assert.True(perRequest < TestServerCeilingBytes,
            $"end-to-end allocated {perRequest:F0} B/request, ceiling is {TestServerCeilingBytes} B");
        Assert.True(perReplay < TestServerCeilingBytes,
            $"cached replay allocated {perReplay:F0} B/request, ceiling is {TestServerCeilingBytes} B");
    }

    private static async Task Request(HttpClient client, string template, double lat)
    {
        var res = await client.GetAsync(
            string.Format(System.Globalization.CultureInfo.InvariantCulture, template, lat),
            TestContext.Current.CancellationToken);
        res.EnsureSuccessStatusCode();
        await res.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
    }

    private static HttpContext NewContext(string queryString)
    {
        var ctx = new DefaultHttpContext();
        ctx.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(Stream.Null));
        ctx.Request.QueryString = new QueryString(queryString);
        return ctx;
    }

    /// <summary>Byte-for-byte the synchronous body of the <c>/cities/nearest</c> endpoint lambda in
    /// <c>Program.cs</c> — kept in sync deliberately, so the measurement covers the real code path.</summary>
    private static Task Nearest(HttpContext ctx, GeoDatabase db, ComputeCounter counter)
    {
        GC.KeepAlive(GeoCacheKey.Compute(ctx)); // documented allocation: the output-cache key string
        var qs = (ctx.Request.QueryString.Value ?? "").AsSpan();
        if (!QueryParams.TryGetDouble(qs, "lat", out var lat) || lat is not (>= -90 and <= 90))
            throw new InvalidOperationException("lat");
        if (!QueryParams.TryGetDouble(qs, "lon", out var lon) || lon is not (>= -180 and <= 180))
            throw new InvalidOperationException("lon");
        var count = 5;
        if (QueryParams.TryGetRaw(qs, "count", out _) &&
            (!QueryParams.TryGetInt(qs, "count", out count) || count is < 1 or > 100))
            throw new InvalidOperationException("count");

        ctx.Response.Headers["X-Compute-Count"] = counter.Increment().ToString(); // documented allocation
        Span<GeoHit> hits = stackalloc GeoHit[100];
        var start = Stopwatch.GetTimestamp();
        var n = db.FindNearest(lat, lon, count, hits);
        ServerTiming.Set(ctx, start); // documented allocation: the Server-Timing header value string
        CityJson.WriteCities(ctx.Response, db, hits[..n]);
        return ctx.Response.BodyWriter.FlushAsync().AsTask();
    }
}
