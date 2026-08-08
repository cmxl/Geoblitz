using System.IO.Pipelines;
using System.Text.Json;
using Geoblitz.Geo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Geoblitz.Api.Tests;

/// <summary>Coverage gap: <see cref="PooledJson"/> caches its <see cref="Utf8JsonWriter"/> per
/// thread (<c>[ThreadStatic]</c>) and <see cref="CityJson.WriteCities"/> always returns it to the
/// cache in a <c>finally</c> block — including when a write fails partway through, leaving the
/// writer's internal buffered/depth state mid-object. Every existing test only exercises the happy
/// path, so nothing pins whether the *next* rental (on the same thread) recovers cleanly via
/// <c>Utf8JsonWriter.Reset(PipeWriter)</c>, or whether it inherits corrupted state.</summary>
[Collection("api")]
public class PooledJsonRecoveryTests(ApiFixture fixture)
{
    /// <summary>A <see cref="PipeWriter"/> that throws once its buffer has been requested
    /// <paramref name="ThrowOnCall"/> times, simulating an I/O failure partway through serialization
    /// (after some objects in the "cities" array have already been written, so the writer's internal
    /// depth is non-zero when the exception is thrown).</summary>
    private sealed class ThrowingPipeWriter(int throwOnCall) : PipeWriter
    {
        private int _calls;

        public override void Advance(int bytes) { }

        public override Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (++_calls >= throwOnCall) throw new IOException("simulated write failure");
            return new byte[Math.Max(sizeHint, 128)];
        }

        public override Span<byte> GetSpan(int sizeHint = 0)
        {
            if (++_calls >= throwOnCall) throw new IOException("simulated write failure");
            return new byte[Math.Max(sizeHint, 128)];
        }

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
            => new(new FlushResult(isCanceled: false, isCompleted: false));

        public override void CancelPendingFlush() { }

        public override void Complete(Exception? exception = null) { }
    }

    private sealed class CustomBodyFeature(PipeWriter writer) : IHttpResponseBodyFeature
    {
        public Stream Stream { get; } = Stream.Null;
        public PipeWriter Writer { get; } = writer;
        public Task CompleteAsync() => Task.CompletedTask;
        public void DisableBuffering() { }
        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task WriteCities_ExceptionMidWrite_LeavesThePooledWriterReusableAfterwards()
    {
        var db = fixture.Services.GetRequiredService<GeoDatabase>();
        // A large, multi-buffer body (same query as ResponseFramingTests' multi-buffer regression
        // test — Berlin/500 km saturates the 1000-item cap): with a small 128-byte fake PipeWriter
        // buffer this guarantees dozens of GetSpan/GetMemory calls before the write completes, so the
        // fault below fires with the writer mid-array (non-zero depth), not after it has finished.
        var hits = new GeoHit[1000];
        var n = db.FindWithin(52.52, 13.405, 500, 0, hits);
        Assert.Equal(1000, n);

        // Run everything below on one dedicated thread: PooledJson's cache is [ThreadStatic], so the
        // recovery being tested only happens if the second call lands on the same thread as the first.
        await Task.Factory.StartNew(async () =>
        {
            var faultyCtx = new DefaultHttpContext();
            var faulty = new ThrowingPipeWriter(throwOnCall: 4);
            faultyCtx.Features.Set<IHttpResponseBodyFeature>(new CustomBodyFeature(faulty));

            var ex = Record.Exception(() => CityJson.WriteCities(faultyCtx.Response, db, hits.AsSpan(0, n)));
            Assert.NotNull(ex);
            Assert.IsType<IOException>(ex);

            // Second call, same thread, healthy PipeWriter: must not throw and must produce
            // well-formed, complete JSON — proving Rent()+Reset() discarded the corrupted state.
            using var body = new MemoryStream();
            var goodCtx = new DefaultHttpContext();
            goodCtx.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(body));

            CityJson.WriteCities(goodCtx.Response, db, hits.AsSpan(0, n));
            await goodCtx.Response.BodyWriter.FlushAsync(TestContext.Current.CancellationToken);

            using var doc = JsonDocument.Parse(body.ToArray());
            Assert.Equal(n, doc.RootElement.GetProperty("count").GetInt32());
            Assert.Equal(n, doc.RootElement.GetProperty("cities").GetArrayLength());
            Assert.Equal(body.Length, goodCtx.Response.ContentLength);
        }, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }
}
