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
}
