using Geoblitz.Geo;
using Xunit;

namespace Geoblitz.Geo.Tests;

public class TopKTests
{
    [Fact]
    public void Keeps_K_Smallest_SortedAscending()
    {
        var rng = new Random(1);
        var keys = new float[200];
        for (var i = 0; i < keys.Length; i++) keys[i] = (float)rng.NextDouble();

        var topk = new TopK(stackalloc float[5], stackalloc int[5]);
        for (var i = 0; i < keys.Length; i++) topk.Add(keys[i], i);

        Span<float> outK = stackalloc float[5];
        Span<int> outI = stackalloc int[5];
        var n = topk.CopySortedTo(outK, outI);

        var expected = keys.Select((k, i) => (k, i)).OrderBy(t => t.k).Take(5).ToArray();
        Assert.Equal(5, n);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(expected[i].k, outK[i], 6);
            Assert.Equal(expected[i].i, outI[i]);
        }
    }

    [Fact]
    public void FewerAddsThanCapacity_ReturnsAll()
    {
        var topk = new TopK(stackalloc float[10], stackalloc int[10]);
        topk.Add(3f, 30); topk.Add(1f, 10); topk.Add(2f, 20);
        Span<float> outK = stackalloc float[10];
        Span<int> outI = stackalloc int[10];
        var n = topk.CopySortedTo(outK, outI);
        Assert.Equal(3, n);
        Assert.Equal(new[] { 10, 20, 30 }, outI[..n].ToArray());
    }

    [Fact]
    public void Threshold_IsInfinity_UntilFull_ThenMaxKept()
    {
        var topk = new TopK(stackalloc float[2], stackalloc int[2]);
        Assert.Equal(float.PositiveInfinity, topk.Threshold);
        topk.Add(5f, 1);
        Assert.Equal(float.PositiveInfinity, topk.Threshold);
        topk.Add(3f, 2);
        Assert.Equal(5f, topk.Threshold);
        topk.Add(1f, 3); // evicts 5
        Assert.Equal(3f, topk.Threshold);
    }

    [Fact]
    public void ScanNearest_MatchesBruteForce()
    {
        var rng = new Random(9);
        const int n = 500;
        var xs = new float[n]; var ys = new float[n]; var zs = new float[n];
        for (var i = 0; i < n; i++)
            GeoMath.ToUnitVector(rng.NextDouble() * 180 - 90, rng.NextDouble() * 360 - 180,
                out xs[i], out ys[i], out zs[i]);
        GeoMath.ToUnitVector(10, 20, out var qx, out var qy, out var qz);

        var topk = new TopK(stackalloc float[8], stackalloc int[8]);
        ChordKernel.ScanNearest(xs, ys, zs, qx, qy, qz, float.PositiveInfinity, 0, ref topk);
        Span<float> outK = stackalloc float[8];
        Span<int> outI = stackalloc int[8];
        var count = topk.CopySortedTo(outK, outI);

        var brute = Enumerable.Range(0, n)
            .Select(i => (Idx: i, D: (xs[i] - qx) * (xs[i] - qx) + (ys[i] - qy) * (ys[i] - qy) + (zs[i] - qz) * (zs[i] - qz)))
            .OrderBy(t => t.D).Take(8).ToArray();

        Assert.Equal(8, count);
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(brute[i].Idx, outI[i]);
            Assert.Equal(brute[i].D, outK[i], 6);
        }
    }
}
