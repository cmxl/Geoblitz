namespace HighPerf.Geo;

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

    private const double KmPerLatDegree = 111.195; // EarthRadius * pi / 180

    public int FindWithin(double lat, double lon, double radiusKm, int minPopulation, Span<GeoHit> results)
    {
        GeoMath.ToUnitVector(lat, lon, out var qx, out var qy, out var qz);
        var maxChordSq = GeoMath.KmToChordSq(radiusKm);

        Span<DataRange> ranges = LatCells <= 256 ? stackalloc DataRange[2 * 256] : new DataRange[2 * LatCells];
        var rangeCount = GetCandidateRanges(lat, lon, radiusKm, ranges);

        var hits = new HitBuffer(1024);
        try
        {
            for (var r = 0; r < rangeCount; r++)
            {
                var (start, end) = (ranges[r].Start, ranges[r].End);
                if (start == end) continue;
                ChordKernel.ScanWithin(
                    X.AsSpan(start, end - start), Y.AsSpan(start, end - start), Z.AsSpan(start, end - start),
                    qx, qy, qz, maxChordSq, start, ref hits);
            }

            hits.SortByDistance();
            var written = 0;
            for (var i = 0; i < hits.Count && written < results.Length; i++)
            {
                var idx = hits[i];
                if (_population[idx] < minPopulation) continue;
                results[written++] = new GeoHit(idx, (float)GeoMath.ChordSqToKm(hits.DistSqAt(i)));
            }
            return written;
        }
        finally
        {
            hits.Dispose();
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

    internal int GetCandidateRanges(double lat, double lon, double radiusKm, Span<DataRange> ranges)
    {
        var latDegRadius = radiusKm / KmPerLatDegree;
        var li0 = CellOfLat(lat - latDegRadius);
        var li1 = CellOfLat(lat + latDegRadius);
        var count = 0;

        for (var li = li0; li <= li1; li++)
        {
            var rowBase = li * LonCells;
            // widest |lat| edge of this row decides the longitude window (superset-safe)
            var edge0 = Math.Abs(-90.0 + li * CellSizeDeg);
            var edge1 = Math.Abs(-90.0 + (li + 1) * CellSizeDeg);
            var maxAbsLat = Math.Min(89.9, Math.Max(edge0, edge1));
            var lonDegRadius = latDegRadius / Math.Cos(maxAbsLat * Math.PI / 180.0);

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
