using System.IO.Compression;
using System.Text;
using Geoblitz.Geo;
using Xunit;

namespace Geoblitz.Geo.Tests;

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

    [Fact]
    public void CrLfLineEndings_AreHandled()
    {
        // \r immediately before \n must be stripped from the last field of the line, not become
        // part of the population number or a trailing empty line.
        var bytes = Encoding.UTF8.GetBytes(
            "Berlin\tDE\t52.52437\t13.41053\t3644826\r\n" +
            "Munich\tDE\t48.1374\t11.5755\t1471508\r\n");
        var p = CityTableParser.Parse(bytes);
        Assert.Equal(2, p.Count);
        Assert.Equal("Berlin", Encoding.UTF8.GetString(p.GetNameUtf8(0)));
        Assert.Equal("Munich", Encoding.UTF8.GetString(p.GetNameUtf8(1)));
        Assert.Equal(3644826, p.Population[0]);
        Assert.Equal(1471508, p.Population[1]);
    }

    [Fact]
    public void TruncatedFinalLine_WithoutTrailingNewline_IsStillParsed()
    {
        // the last line has no trailing \n at all — IndexOf(Lf) returns -1 and the remainder of the
        // span must still be treated as one (complete, well-formed) line, not dropped.
        var bytes = Encoding.UTF8.GetBytes(
            "Berlin\tDE\t52.52437\t13.41053\t3644826\n" +
            "Munich\tDE\t48.1374\t11.5755\t1471508"); // no trailing newline
        var p = CityTableParser.Parse(bytes);
        Assert.Equal(2, p.Count);
        Assert.Equal("Munich", Encoding.UTF8.GetString(p.GetNameUtf8(1)));
        Assert.Equal(1471508, p.Population[1]);
    }

    /// <summary>Regression test: <see cref="CityTableParser.Parse"/> must strip an optional
    /// leading UTF-8 BOM (EF BB BF); without that, the BOM bytes become part of the first
    /// line's name field and the first city decodes as "﻿Berlin" instead of "Berlin".</summary>
    [Fact]
    public void Utf8Bom_AtStartOfInput_DoesNotCorruptFirstCityName()
    {
        byte[] bom = [0xEF, 0xBB, 0xBF];
        var bytes = bom.Concat(Encoding.UTF8.GetBytes(Sample)).ToArray();
        var p = CityTableParser.Parse(bytes);
        Assert.Equal(4, p.Count);
        Assert.Equal("Berlin", Encoding.UTF8.GetString(p.GetNameUtf8(0)));
    }

    [Fact]
    public void TruncatedFinalLine_MissingFields_IsSkippedNotCrashed()
    {
        // final line cut off mid-record (missing lon and population) with no trailing newline —
        // NextField must fail closed and the incomplete record must be dropped, not throw or
        // corrupt the previous record.
        var bytes = Encoding.UTF8.GetBytes(
            "Berlin\tDE\t52.52437\t13.41053\t3644826\n" +
            "Munich\tDE\t48.1374"); // cut off after latitude, no tab even
        var p = CityTableParser.Parse(bytes);
        Assert.Equal(1, p.Count);
        Assert.Equal("Berlin", Encoding.UTF8.GetString(p.GetNameUtf8(0)));
    }
}
