using System.Buffers;

namespace Geoblitz.Geo;

/// <summary>Immutable in-memory city database. Struct-of-arrays, permuted into grid-cell
/// order at build time so each cell is one contiguous, SIMD-scannable range.</summary>
public sealed class GeoDatabase
{
    internal readonly float[] X, Y, Z;
    private readonly float[] _lat, _lon;
    private readonly int[] _population;
    private readonly string[] _country;
    private readonly byte[] _nameBlob;
    private readonly int[] _nameOffsets;
    internal readonly int[] CellStart;

    public int Count { get; }
    internal int LatCells { get; }
    internal int LonCells { get; }
    internal double CellSizeDeg { get; }

    private GeoDatabase(ParsedCities src, double cellSizeDeg)
    {
        CellSizeDeg = cellSizeDeg;
        LatCells = (int)Math.Round(180 / cellSizeDeg);
        LonCells = (int)Math.Round(360 / cellSizeDeg);
        Count = src.Count;
        var cells = LatCells * LonCells;

        // counting sort by cell id
        var cellOf = new int[Count];
        var counts = new int[cells + 1];
        for (var i = 0; i < Count; i++)
        {
            var c = CellOfLat(src.Lat[i]) * LonCells + CellOfLon(src.Lon[i]);
            cellOf[i] = c;
            counts[c + 1]++;
        }
        for (var c = 0; c < cells; c++) counts[c + 1] += counts[c];
        CellStart = counts;

        _lat = new float[Count]; _lon = new float[Count];
        X = new float[Count]; Y = new float[Count]; Z = new float[Count];
        _population = new int[Count]; _country = new string[Count];
        _nameOffsets = new int[Count + 1];
        _nameBlob = new byte[src.NameBlob.Length];

        var next = new int[cells];
        Array.Copy(CellStart, next, cells);
        var order = new int[Count]; // order[dest] = source index
        for (var i = 0; i < Count; i++) order[next[cellOf[i]]++] = i;

        var blobPos = 0;
        for (var d = 0; d < Count; d++)
        {
            var s = order[d];
            _lat[d] = src.Lat[s]; _lon[d] = src.Lon[s];
            GeoMath.ToUnitVector(src.Lat[s], src.Lon[s], out X[d], out Y[d], out Z[d]);
            _population[d] = src.Population[s];
            _country[d] = src.Country[s];
            var name = src.GetNameUtf8(s);
            name.CopyTo(_nameBlob.AsSpan(blobPos));
            _nameOffsets[d] = blobPos;
            blobPos += name.Length;
        }
        _nameOffsets[Count] = blobPos;
    }

    public static GeoDatabase Build(ParsedCities cities, double cellSizeDeg = 1.0) => new(cities, cellSizeDeg);

    public static GeoDatabase LoadDefault()
    {
        using var stream = typeof(GeoDatabase).Assembly.GetManifestResourceStream("cities.tsv.gz")
            ?? throw new InvalidOperationException("Embedded resource 'cities.tsv.gz' not found.");
        return Build(CityTableParser.LoadGzip(stream));
    }

    public float GetLat(int i) => _lat[i];
    public float GetLon(int i) => _lon[i];
    public int GetPopulation(int i) => _population[i];
    public string GetCountry(int i) => _country[i];
    public ReadOnlySpan<byte> GetNameUtf8(int i) => _nameBlob.AsSpan(_nameOffsets[i], _nameOffsets[i + 1] - _nameOffsets[i]);

    internal int CellOfLat(double lat) => Math.Clamp((int)((lat + 90.0) / CellSizeDeg), 0, LatCells - 1);
    internal int CellOfLon(double lon) => Math.Clamp((int)((lon + 180.0) / CellSizeDeg), 0, LonCells - 1);

    // km per degree of latitude along a meridian; derived (not hand-rounded) so that
    // radiusKm / KmPerLatDegree never *under*-estimates the latitude span of the radius,
    // which the candidate-range superset guarantee depends on.
    private const double KmPerLatDegree = GeoMath.EarthRadiusKm * Math.PI / 180.0;

    /// <summary>Result counts up to this many hits keep the selection heap on the stack; larger
    /// result spans rent it from <see cref="ArrayPool{T}"/> (allocation-free once the pool is warm).</summary>
    private const int StackSelectionCapacity = 256;

    public int FindWithin(double lat, double lon, double radiusKm, int minPopulation, Span<GeoHit> results)
    {
        var k = Math.Min(results.Length, Count);
        if (k <= 0) return 0;

        GeoMath.ToUnitVector(lat, lon, out var qx, out var qy, out var qz);
        var maxChordSq = GeoMath.KmToChordSq(radiusKm);

        Span<DataRange> ranges = LatCells <= 256 ? stackalloc DataRange[2 * 256] : new DataRange[2 * LatCells];
        var rangeCount = GetCandidateRanges(lat, lon, radiusKm, ranges);

        // Bounded selection: keep only the k closest qualifying hits in a max-heap instead of
        // collecting every match and sorting it. A 500 km query matching ~20k cities now costs
        // O(candidates + matches·log k) with a heap that prunes most candidates outright, rather
        // than O(matches·log matches) over a buffer that has to grow to hold all of them.
        float[]? rentedKeys = null;
        int[]? rentedIdx = null;
        Span<float> keys = k <= StackSelectionCapacity
            ? stackalloc float[StackSelectionCapacity]
            : (rentedKeys = ArrayPool<float>.Shared.Rent(k));
        Span<int> idx = k <= StackSelectionCapacity
            ? stackalloc int[StackSelectionCapacity]
            : (rentedIdx = ArrayPool<int>.Shared.Rent(k));

        try
        {
            var topk = new TopK(keys[..k], idx[..k]);
            for (var r = 0; r < rangeCount; r++)
            {
                var (start, end) = (ranges[r].Start, ranges[r].End);
                if (start == end) continue;
                var len = end - start;
                // minPopulation is applied inside the scan, i.e. BEFORE selection, so the retained
                // set is "the closest k points that pass the filter" — identical semantics to
                // filtering a fully sorted match list while truncating it.
                ChordKernel.ScanWithinTopK(
                    X.AsSpan(start, len), Y.AsSpan(start, len), Z.AsSpan(start, len),
                    _population.AsSpan(start, len), minPopulation,
                    qx, qy, qz, maxChordSq, start, ref topk);
            }

            var n = topk.Count;
            keys[..n].Sort(idx[..n]); // squared chord is monotonic in great-circle distance
            for (var i = 0; i < n; i++)
                results[i] = new GeoHit(idx[i], (float)GeoMath.ChordSqToKm(keys[i]));
            return n;
        }
        finally
        {
            if (rentedKeys is not null) ArrayPool<float>.Shared.Return(rentedKeys);
            if (rentedIdx is not null) ArrayPool<int>.Shared.Return(rentedIdx);
        }
    }

    private const double HalfCircumferenceKm = 20016.0;

    public int FindNearest(double lat, double lon, int k, Span<GeoHit> results)
    {
        k = Math.Min(Math.Min(k, Count), Math.Min(results.Length, 128));
        if (k <= 0) return 0;

        GeoMath.ToUnitVector(lat, lon, out var qx, out var qy, out var qz);
        Span<float> heapKeys = stackalloc float[128];
        Span<int> heapIdx = stackalloc int[128];
        Span<DataRange> ranges = LatCells <= 256 ? stackalloc DataRange[2 * 256] : new DataRange[2 * LatCells];
        Span<float> sortedKeys = stackalloc float[128];
        Span<int> sortedIdx = stackalloc int[128];

        var radiusKm = 50.0;
        while (true)
        {
            var topk = new TopK(heapKeys[..k], heapIdx[..k]);
            var maxChordSq = GeoMath.KmToChordSq(radiusKm);
            var rangeCount = GetCandidateRanges(lat, lon, radiusKm, ranges);
            for (var r = 0; r < rangeCount; r++)
            {
                var (start, end) = (ranges[r].Start, ranges[r].End);
                if (start == end) continue;
                ChordKernel.ScanNearest(
                    X.AsSpan(start, end - start), Y.AsSpan(start, end - start), Z.AsSpan(start, end - start),
                    qx, qy, qz, maxChordSq, start, ref topk);
            }

            var done = radiusKm >= HalfCircumferenceKm
                || (topk.Count == k && GeoMath.ChordSqToKm(topk.Threshold) <= radiusKm);
            if (done)
            {
                var n = topk.CopySortedTo(sortedKeys, sortedIdx);
                for (var i = 0; i < n; i++)
                    results[i] = new GeoHit(sortedIdx[i], (float)GeoMath.ChordSqToKm(sortedKeys[i]));
                return n;
            }
            radiusKm = Math.Min(radiusKm * 4, HalfCircumferenceKm);
        }
    }

    /// <summary>Candidate data ranges (at most two per latitude row, so antimeridian wrap stays two
    /// segments) whose union is a <b>superset</b> of every point within <paramref name="radiusKm"/>.
    /// <para>Longitude window: for a point at latitude φ and a query at latitude φq, being within an
    /// angular radius δ requires (haversine, dropping the non-negative Δφ term)
    /// <c>sin(Δλ/2) ≤ sin(δ/2) / sqrt(cos φ · cos φq)</c>. Bounding
    /// <c>sqrt(cos φ · cos φq) ≥ cos(max(|φ|, |φq|))</c> — valid for every φ in the row, and including
    /// the <em>query</em> latitude, which the flat-earth form omitted — gives
    /// <c>Δλ ≤ 2·asin(sin(δ/2) / cos(max(|φ_row|, |φq|)))</c>. When that ratio reaches 1 no longitude
    /// is excluded and the row must be scanned whole; that is what makes the pole rows (|φ| → 90°,
    /// cos → 0) correct for any radius.</para></summary>
    internal int GetCandidateRanges(double lat, double lon, double radiusKm, Span<DataRange> ranges)
    {
        var latDegRadius = radiusKm / KmPerLatDegree;
        var li0 = CellOfLat(lat - latDegRadius);
        var li1 = CellOfLat(lat + latDegRadius);
        var count = 0;

        var sinHalfDelta = Math.Sin(Math.Min(radiusKm / GeoMath.EarthRadiusKm, Math.PI) / 2.0);
        var absQueryLat = Math.Abs(lat);

        for (var li = li0; li <= li1; li++)
        {
            var rowBase = li * LonCells;
            // widest |lat| of this row *and* of the query decides the longitude window
            var edge0 = Math.Abs(-90.0 + li * CellSizeDeg);
            var edge1 = Math.Abs(-90.0 + (li + 1) * CellSizeDeg);
            var maxAbsLat = Math.Min(90.0, Math.Max(Math.Max(edge0, edge1), absQueryLat));
            var cosLat = Math.Cos(maxAbsLat * (Math.PI / 180.0));
            var ratio = cosLat <= 0.0 ? double.PositiveInfinity : sinHalfDelta / cosLat;

            if (ratio >= 1.0) // no longitude can be excluded for this row
            {
                ranges[count++] = new DataRange(CellStart[rowBase], CellStart[rowBase + LonCells]);
                continue;
            }

            var lonDegRadius = 2.0 * Math.Asin(ratio) * (180.0 / Math.PI);
            if (lonDegRadius * 2 >= 360.0 - CellSizeDeg)
            {
                ranges[count++] = new DataRange(CellStart[rowBase], CellStart[rowBase + LonCells]);
                continue;
            }

            var c0 = (int)Math.Floor((lon - lonDegRadius + 180.0) / CellSizeDeg);
            var c1 = (int)Math.Floor((lon + lonDegRadius + 180.0) / CellSizeDeg);
            if (c1 - c0 + 1 >= LonCells)
            {
                ranges[count++] = new DataRange(CellStart[rowBase], CellStart[rowBase + LonCells]);
                continue;
            }

            var w0 = ((c0 % LonCells) + LonCells) % LonCells;
            var w1 = ((c1 % LonCells) + LonCells) % LonCells;
            if (w0 <= w1)
            {
                ranges[count++] = new DataRange(CellStart[rowBase + w0], CellStart[rowBase + w1 + 1]);
            }
            else // wraps the antimeridian: two segments
            {
                ranges[count++] = new DataRange(CellStart[rowBase + w0], CellStart[rowBase + LonCells]);
                ranges[count++] = new DataRange(CellStart[rowBase], CellStart[rowBase + w1 + 1]);
            }
        }
        return count;
    }
}

internal readonly record struct DataRange(int Start, int End);
