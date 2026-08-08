namespace Geoblitz.Geo;

public ref struct TopK
{
    private readonly Span<float> _keys;
    private readonly Span<int> _idx;
    private int _count;

    public TopK(Span<float> keys, Span<int> indices)
    {
        if (keys.Length != indices.Length || keys.IsEmpty)
            throw new ArgumentException("keys/indices must be same non-zero length");
        _keys = keys;
        _idx = indices;
        _count = 0;
    }

    public readonly int Count => _count;
    public readonly int Capacity => _keys.Length;
    public readonly float Threshold => _count == _keys.Length ? _keys[0] : float.PositiveInfinity;

    public void Add(float key, int index)
    {
        if (_count < _keys.Length)
        {
            _keys[_count] = key;
            _idx[_count] = index;
            _count++;
            SiftUp(_count - 1);
        }
        else if (key < _keys[0])
        {
            _keys[0] = key;
            _idx[0] = index;
            SiftDown();
        }
    }

    public readonly int CopySortedTo(Span<float> keysOut, Span<int> indicesOut)
    {
        _keys[.._count].CopyTo(keysOut);
        _idx[.._count].CopyTo(indicesOut);
        keysOut[.._count].Sort(indicesOut[.._count]);
        return _count;
    }

    private void SiftUp(int i)
    {
        while (i > 0)
        {
            var parent = (i - 1) / 2;
            if (_keys[i] <= _keys[parent]) break;
            (_keys[i], _keys[parent]) = (_keys[parent], _keys[i]);
            (_idx[i], _idx[parent]) = (_idx[parent], _idx[i]);
            i = parent;
        }
    }

    private void SiftDown()
    {
        var i = 0;
        while (true)
        {
            int l = 2 * i + 1, r = l + 1, largest = i;
            if (l < _count && _keys[l] > _keys[largest]) largest = l;
            if (r < _count && _keys[r] > _keys[largest]) largest = r;
            if (largest == i) break;
            (_keys[i], _keys[largest]) = (_keys[largest], _keys[i]);
            (_idx[i], _idx[largest]) = (_idx[largest], _idx[i]);
            i = largest;
        }
    }
}
