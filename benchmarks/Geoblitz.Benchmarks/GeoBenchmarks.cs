using BenchmarkDotNet.Attributes;
using Geoblitz.Geo;

namespace Geoblitz.Benchmarks;

[MemoryDiagnoser]
public class GeoBenchmarks
{
    private GeoDatabase _db = null!;
    private GeoHit[] _hits = null!;
    private float _qx, _qy, _qz;

    [GlobalSetup]
    public void Setup()
    {
        _db = GeoDatabase.LoadDefault();
        _hits = new GeoHit[1000];
        GeoMath.ToUnitVector(52.52, 13.405, out _qx, out _qy, out _qz);
    }

    [Benchmark(Baseline = true)]
    public double Scalar_HaversineFullScan()
    {
        double best = double.MaxValue;
        for (var i = 0; i < _db.Count; i++)
        {
            var d = GeoMath.HaversineKm(52.52, 13.405, _db.GetLat(i), _db.GetLon(i));
            if (d < best) best = d;
        }
        return best;
    }

    /// <summary>Same full scan as <see cref="Scalar_HaversineFullScan"/>, but producing the same
    /// output as the chord benchmarks below (every city within 100 km, collected into a
    /// <see cref="HitBuffer"/>). This is the apples-to-apples trig baseline: comparing it to
    /// <see cref="Scalar_ChordFullScan"/> isolates the cost of the per-point Sin/Cos/Atan2.</summary>
    [Benchmark]
    public int Scalar_HaversineFullScanCollect()
    {
        var hits = new HitBuffer(1024);
        try
        {
            for (var i = 0; i < _db.Count; i++)
            {
                var d = GeoMath.HaversineKm(52.52, 13.405, _db.GetLat(i), _db.GetLon(i));
                if (d <= 100.0) hits.Add(i, (float)(d * d));
            }
            return hits.Count;
        }
        finally { hits.Dispose(); }
    }

    /// <summary>Trig-free chord-distance full scan with a plain scalar loop — deliberately mirrors
    /// the scalar tail of <see cref="ChordKernel.ScanWithin"/> so that
    /// <see cref="Simd_ChordFullScan"/> divided by this benchmark is the <em>vectorization</em>
    /// factor alone, with the trig-elimination factor already factored out by
    /// <see cref="Scalar_HaversineFullScanCollect"/>.</summary>
    [Benchmark]
    public int Scalar_ChordFullScan()
    {
        var hits = new HitBuffer(1024);
        try
        {
            var (xs, ys, zs) = (_db.X, _db.Y, _db.Z);
            var maxChordSq = GeoMath.KmToChordSq(100);
            for (var i = 0; i < xs.Length; i++)
            {
                float dx = xs[i] - _qx, dy = ys[i] - _qy, dz = zs[i] - _qz;
                var dsq = dx * dx + dy * dy + dz * dz;
                if (dsq <= maxChordSq) hits.Add(i, dsq);
            }
            return hits.Count;
        }
        finally { hits.Dispose(); }
    }

    [Benchmark]
    public int Simd_ChordFullScan()
    {
        var hits = new HitBuffer(1024);
        try
        {
            ChordKernel.ScanWithin(_db.X, _db.Y, _db.Z, _qx, _qy, _qz,
                GeoMath.KmToChordSq(100), 0, ref hits);
            return hits.Count;
        }
        finally { hits.Dispose(); }
    }

    [Benchmark]
    public int Grid_FindWithin100km()
        => _db.FindWithin(52.52, 13.405, 100, 0, _hits);

    [Benchmark]
    public int Grid_FindNearest10()
        => _db.FindNearest(52.52, 13.405, 10, _hits.AsSpan(0, 10));

    [Benchmark]
    public int Grid_FindNearest10_SparseOcean()
        => _db.FindNearest(-45.0, -140.0, 10, _hits.AsSpan(0, 10)); // forces radius expansion rounds

    [Benchmark]
    public double Scalar_HaversineSinglePair()
        => GeoMath.HaversineKm(52.52, 13.405, 48.1374, 11.5755);
}
