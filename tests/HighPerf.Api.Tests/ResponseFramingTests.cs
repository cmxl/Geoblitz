using HighPerf.Geo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HighPerf.Api.Tests;

/// <summary>M2/I4 — the compute path must declare <c>Content-Length</c> instead of falling back to
/// chunked framing. This is asserted directly against <see cref="CityJson.WriteCities"/> because it
/// is the load-bearing part: the size is only knowable before the flush, and once
/// <c>Response.HasStarted</c> is true it can no longer be set.
/// <para>Deliberate limitation: these tests pin the declared length, not the wire framing. No
/// in-process host can prove the framing — TestServer has no HTTP/1.1 framing layer and reports a
/// buffered length to the client either way. That <c>Transfer-Encoding: chunked</c> is actually gone
/// was verified by hand against a Release Kestrel build (`Content-Length: 581` on a
/// <c>/cities/nearest</c> miss and `105661` on a multi-buffer <c>/cities/within</c> miss, no
/// <c>Transfer-Encoding</c> header, matching body sizes).</para></summary>
[Collection("api")]
public class ResponseFramingTests(ApiFixture fixture)
{
    [Fact]
    public async Task WriteCities_DeclaresContentLength_MatchingTheExactBodySize()
    {
        var db = fixture.Services.GetRequiredService<GeoDatabase>();
        using var body = new MemoryStream();
        var ctx = new DefaultHttpContext();
        ctx.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(body));

        WriteFiveNearestBerlin(ctx, db);

        var declared = ctx.Response.ContentLength;
        await ctx.Response.BodyWriter.FlushAsync(TestContext.Current.CancellationToken);

        Assert.True(body.Length > 0);
        Assert.Equal(body.Length, declared);
    }

    [Fact]
    public async Task WriteCities_DeclaresContentLength_ForABodyThatSpansSeveralBuffers()
    {
        // regression guard for the interesting half of I4: a large result set makes
        // Utf8JsonWriter.Grow() Advance() the PipeWriter mid-serialization, so BytesPending holds
        // only the tail and the declared length has to include BytesCommitted as well.
        var db = fixture.Services.GetRequiredService<GeoDatabase>();
        using var body = new MemoryStream();
        var ctx = new DefaultHttpContext();
        ctx.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(body));

        WriteWithin(ctx, db, 1000);

        var declared = ctx.Response.ContentLength;
        await ctx.Response.BodyWriter.FlushAsync(TestContext.Current.CancellationToken);

        Assert.True(body.Length > 64 * 1024, $"expected a multi-buffer body, got {body.Length} bytes");
        Assert.Equal(body.Length, declared);
    }

    private static void WriteWithin(HttpContext ctx, GeoDatabase db, int capacity)
    {
        var hits = new GeoHit[capacity];
        var n = db.FindWithin(52.52, 13.405, 500, 0, hits);
        Assert.Equal(capacity, n); // Berlin/500 km saturates the cap, i.e. a genuinely large body
        CityJson.WriteCities(ctx.Response, db, hits.AsSpan(0, n));
    }

    private static void WriteFiveNearestBerlin(HttpContext ctx, GeoDatabase db)
    {
        Span<GeoHit> hits = stackalloc GeoHit[5];
        var n = db.FindNearest(52.52, 13.405, 5, hits);
        CityJson.WriteCities(ctx.Response, db, hits[..n]);
    }
}
