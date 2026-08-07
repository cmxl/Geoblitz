using System.Numerics;

namespace HighPerf.Geo;

public static class ChordKernel
{
    public static void ScanWithin(
        ReadOnlySpan<float> xs, ReadOnlySpan<float> ys, ReadOnlySpan<float> zs,
        float qx, float qy, float qz, float maxChordSq,
        int baseIndex, ref HitBuffer hits)
    {
        int n = xs.Length, i = 0;
        var w = Vector<float>.Count;
        if (Vector.IsHardwareAccelerated && n >= w)
        {
            var vqx = new Vector<float>(qx);
            var vqy = new Vector<float>(qy);
            var vqz = new Vector<float>(qz);
            var vmax = new Vector<float>(maxChordSq);
            for (; i <= n - w; i += w)
            {
                var dx = new Vector<float>(xs.Slice(i, w)) - vqx;
                var dy = new Vector<float>(ys.Slice(i, w)) - vqy;
                var dz = new Vector<float>(zs.Slice(i, w)) - vqz;
                var dsq = dx * dx + dy * dy + dz * dz;
                var mask = Vector.LessThanOrEqual(dsq, vmax);
                if (mask != Vector<int>.Zero)
                    for (var j = 0; j < w; j++)
                        if (mask[j] != 0)
                            hits.Add(baseIndex + i + j, dsq[j]);
            }
        }
        for (; i < n; i++)
        {
            float dx = xs[i] - qx, dy = ys[i] - qy, dz = zs[i] - qz;
            var dsq = dx * dx + dy * dy + dz * dz;
            if (dsq <= maxChordSq)
                hits.Add(baseIndex + i, dsq);
        }
    }

    public static void ScanNearest(
        ReadOnlySpan<float> xs, ReadOnlySpan<float> ys, ReadOnlySpan<float> zs,
        float qx, float qy, float qz, float maxChordSq,
        int baseIndex, ref TopK topk)
    {
        int n = xs.Length, i = 0;
        var w = Vector<float>.Count;
        if (Vector.IsHardwareAccelerated && n >= w)
        {
            var vqx = new Vector<float>(qx);
            var vqy = new Vector<float>(qy);
            var vqz = new Vector<float>(qz);
            for (; i <= n - w; i += w)
            {
                var limit = Math.Min(maxChordSq, topk.Threshold);
                var vmax = new Vector<float>(limit);
                var dx = new Vector<float>(xs.Slice(i, w)) - vqx;
                var dy = new Vector<float>(ys.Slice(i, w)) - vqy;
                var dz = new Vector<float>(zs.Slice(i, w)) - vqz;
                var dsq = dx * dx + dy * dy + dz * dz;
                var mask = Vector.LessThanOrEqual(dsq, vmax);
                if (mask != Vector<int>.Zero)
                    for (var j = 0; j < w; j++)
                        if (mask[j] != 0)
                            topk.Add(dsq[j], baseIndex + i + j);
            }
        }
        for (; i < n; i++)
        {
            float dx = xs[i] - qx, dy = ys[i] - qy, dz = zs[i] - qz;
            var dsq = dx * dx + dy * dy + dz * dz;
            if (dsq <= maxChordSq && dsq < topk.Threshold)
                topk.Add(dsq, baseIndex + i);
        }
    }
}
