using BenchmarkDotNet.Attributes;
using HighPerf.Geo;

namespace HighPerf.Benchmarks;

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
