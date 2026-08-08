using System.Buffers;

namespace Geoblitz.Geo;

public struct HitBuffer : IDisposable
{
    private int[] _idx;
    private float[] _dsq;
    private int _count;

    public HitBuffer(int initialCapacity)
    {
        _idx = ArrayPool<int>.Shared.Rent(Math.Max(4, initialCapacity));
        _dsq = ArrayPool<float>.Shared.Rent(_idx.Length);
        _count = 0;
    }

    public readonly int Count => _count;
    public readonly ReadOnlySpan<int> Indices => _idx.AsSpan(0, _count);
    public readonly ReadOnlySpan<float> DistSq => _dsq.AsSpan(0, _count);
    public readonly int this[int i] => _idx[i];
    public readonly float DistSqAt(int i) => _dsq[i];

    public void Add(int index, float distSq)
    {
        if (_count == _idx.Length) Grow();
        _idx[_count] = index;
        _dsq[_count] = distSq;
        _count++;
    }

    public readonly void SortByDistance()
        => _dsq.AsSpan(0, _count).Sort(_idx.AsSpan(0, _count));

    private void Grow()
    {
        var newIdx = ArrayPool<int>.Shared.Rent(_idx.Length * 2);
        var newDsq = ArrayPool<float>.Shared.Rent(newIdx.Length);
        _idx.AsSpan(0, _count).CopyTo(newIdx);
        _dsq.AsSpan(0, _count).CopyTo(newDsq);
        ArrayPool<int>.Shared.Return(_idx);
        ArrayPool<float>.Shared.Return(_dsq);
        _idx = newIdx;
        _dsq = newDsq;
    }

    public void Dispose()
    {
        if (_idx is null) return;
        ArrayPool<int>.Shared.Return(_idx);
        ArrayPool<float>.Shared.Return(_dsq);
        _idx = null!;
        _dsq = null!;
        _count = 0;
    }
}
