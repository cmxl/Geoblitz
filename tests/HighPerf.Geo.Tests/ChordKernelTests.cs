using HighPerf.Geo;
using Xunit;

namespace HighPerf.Geo.Tests;

public class ChordKernelTests
{
    private static (float[] xs, float[] ys, float[] zs) RandomPoints(int n, int seed)
    {
        var rng = new Random(seed);
        var xs = new float[n]; var ys = new float[n]; var zs = new float[n];
        for (var i = 0; i < n; i++)
            GeoMath.ToUnitVector(rng.NextDouble() * 180 - 90, rng.NextDouble() * 360 - 180,
                out xs[i], out ys[i], out zs[i]);
        return (xs, ys, zs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(1000)]
    public void ScanWithin_MatchesScalarReference_AllSizes(int n)
    {
        var (xs, ys, zs) = RandomPoints(n, seed: n + 1);
        GeoMath.ToUnitVector(48.0, 11.0, out var qx, out var qy, out var qz);
        var maxChordSq = GeoMath.KmToChordSq(3000);

        var expected = new List<(int Idx, float D)>();
        for (var i = 0; i < n; i++)
        {
            float dx = xs[i] - qx, dy = ys[i] - qy, dz = zs[i] - qz;
            var d = dx * dx + dy * dy + dz * dz;
            if (d <= maxChordSq) expected.Add((100 + i, d));
        }

        var hits = new HitBuffer(4);
        try
        {
            ChordKernel.ScanWithin(xs, ys, zs, qx, qy, qz, maxChordSq, baseIndex: 100, ref hits);
            Assert.Equal(expected.Count, hits.Count);
            for (var i = 0; i < hits.Count; i++)
            {
                Assert.Equal(expected[i].Idx, hits.Indices[i]);
                Assert.Equal(expected[i].D, hits.DistSq[i], 6);
            }
        }
        finally { hits.Dispose(); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(1000)]
    public void ScanWithinTopK_MatchesFilteredScalarReference_AllSizes(int n)
    {
        var (xs, ys, zs) = RandomPoints(n, seed: n + 101);
        // every third point fails the filter, so rejected lanes fall inside the vector body
        var pops = new int[n];
        for (var i = 0; i < n; i++) pops[i] = i % 3 == 0 ? 10 : 1_000;
        const int minPopulation = 500;
        const int k = 5;

        GeoMath.ToUnitVector(48.0, 11.0, out var qx, out var qy, out var qz);
        var maxChordSq = GeoMath.KmToChordSq(3000);

        var expected = new List<(int Idx, float D)>();
        for (var i = 0; i < n; i++)
        {
            float dx = xs[i] - qx, dy = ys[i] - qy, dz = zs[i] - qz;
            var d = dx * dx + dy * dy + dz * dz;
            if (d <= maxChordSq && pops[i] >= minPopulation) expected.Add((100 + i, d));
        }
        expected.Sort((a, b) => a.D.CompareTo(b.D));
        var expectedCount = Math.Min(k, expected.Count);

        Span<float> keys = stackalloc float[k];
        Span<int> idx = stackalloc int[k];
        var topk = new TopK(keys, idx);
        ChordKernel.ScanWithinTopK(xs, ys, zs, pops, minPopulation,
            qx, qy, qz, maxChordSq, baseIndex: 100, ref topk);

        Span<float> outKeys = stackalloc float[k];
        Span<int> outIdx = stackalloc int[k];
        var got = topk.CopySortedTo(outKeys, outIdx);

        Assert.Equal(expectedCount, got);
        for (var i = 0; i < got; i++)
        {
            Assert.Equal(expected[i].Idx, outIdx[i]);
            Assert.Equal(expected[i].D, outKeys[i], 6);
        }
    }

    [Fact]
    public void HitBuffer_GrowsPastInitialCapacity()
    {
        var hits = new HitBuffer(2);
        try
        {
            for (var i = 0; i < 100; i++) hits.Add(i, 100 - i);
            Assert.Equal(100, hits.Count);
            Assert.Equal(99, hits.Indices[99]);
            Assert.Equal(1f, hits.DistSq[99], 5);
        }
        finally { hits.Dispose(); }
    }

    [Fact]
    public void HitBuffer_SortByDistance_CoSortsIndices()
    {
        var hits = new HitBuffer(4);
        try
        {
            hits.Add(10, 3f); hits.Add(11, 1f); hits.Add(12, 2f);
            hits.SortByDistance();
            Assert.Equal(new[] { 11, 12, 10 }, hits.Indices.ToArray());
            Assert.Equal(new[] { 1f, 2f, 3f }, hits.DistSq.ToArray());
        }
        finally { hits.Dispose(); }
    }

    [Fact]
    public void ScanWithin_NoMatches_LeavesBufferEmpty()
    {
        var (xs, ys, zs) = RandomPoints(50, 7);
        GeoMath.ToUnitVector(48.0, 11.0, out var qx, out var qy, out var qz);
        var hits = new HitBuffer(4);
        try
        {
            ChordKernel.ScanWithin(xs, ys, zs, qx, qy, qz, maxChordSq: 0f, 0, ref hits);
            Assert.Equal(0, hits.Count);
        }
        finally { hits.Dispose(); }
    }
}
