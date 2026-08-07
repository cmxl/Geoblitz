using System.IO.Compression;
using System.Text;
using HighPerf.Geo;
using Xunit;

namespace HighPerf.Geo.Tests;

public class CityTableParserTests
{
    private const string Sample =
        "Berlin\tDE\t52.52437\t13.41053\t3644826\n" +
        "Suva\tFJ\t-18.14161\t178.44149\t88271\n" +
        "São Paulo\tBR\t-23.5475\t-46.63611\t10021295\n" +
        "BadLine\tXX\tnotanumber\t1.0\t5\n" +
        "NoPop\tUS\t40.0\t-75.0\t\n";

    private static ParsedCities ParseSample() => CityTableParser.Parse(Encoding.UTF8.GetBytes(Sample));

    [Fact]
    public void Parses_ValidLines_SkipsBad()
    {
        var p = ParseSample();
        Assert.Equal(4, p.Count);
        Assert.Equal(52.52437f, p.Lat[0], 4);
        Assert.Equal(178.44149f, p.Lon[1], 4);
        Assert.Equal(10021295, p.Population[2]);
        Assert.Equal(0, p.Population[3]);
        Assert.Equal("DE", p.Country[0]);
        Assert.Equal("BR", p.Country[2]);
    }

    [Fact]
    public void Names_AreExactUtf8()
    {
        var p = ParseSample();
        Assert.Equal("Berlin", Encoding.UTF8.GetString(p.GetNameUtf8(0)));
        Assert.Equal("São Paulo", Encoding.UTF8.GetString(p.GetNameUtf8(2)));
        Assert.Equal(p.Count + 1, p.NameOffsets.Length);
    }

    [Fact]
    public void CountryCodes_AreInterned()
    {
        var bytes = Encoding.UTF8.GetBytes("A\tDE\t1\t1\t1\nB\tDE\t2\t2\t2\n");
        var p = CityTableParser.Parse(bytes);
        Assert.Same(p.Country[0], p.Country[1]);
    }

    [Fact]
    public void LoadGzip_RoundTrips()
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(Encoding.UTF8.GetBytes(Sample));
        ms.Position = 0;
        var p = CityTableParser.LoadGzip(ms);
        Assert.Equal(4, p.Count);
    }

    [Fact]
    public void EmptyInput_YieldsZeroCities()
    {
        var p = CityTableParser.Parse(ReadOnlySpan<byte>.Empty);
        Assert.Equal(0, p.Count);
        Assert.Single(p.NameOffsets);
    }
}
