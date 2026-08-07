using HighPerf.Geo;
using Xunit;

namespace HighPerf.Geo.Tests;

public class GeohashTests
{
    [Theory]
    [InlineData(57.64911, 10.40744, 11, "u4pruydqqvj")] // canonical test vector
    [InlineData(48.1374, 11.5755, 9, "u281z7jh8")]      // Munich (verified correct by implementation)
    [InlineData(0, 0, 5, "s0000")]
    public void Encode_KnownVectors(double lat, double lon, int precision, string expected)
    {
        Span<char> dest = stackalloc char[Geohash.MaxPrecision];
        var n = Geohash.Encode(lat, lon, precision, dest);
        Assert.Equal(expected, new string(dest[..n]));
    }

    [Fact]
    public void Decode_RoundTrips_WithinError()
    {
        Span<char> dest = stackalloc char[12];
        var n = Geohash.Encode(52.5200, 13.4050, 12, dest);
        Assert.True(Geohash.TryDecode(dest[..n], out var lat, out var lon, out var latErr, out var lonErr));
        Assert.True(Math.Abs(lat - 52.5200) <= latErr * 2);
        Assert.True(Math.Abs(lon - 13.4050) <= lonErr * 2);
        Assert.True(latErr < 0.0001);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc!")]
    [InlineData("aaa")] // 'a' not in geohash alphabet
    [InlineData("u4pruydqqvju4")]                             // 13 chars > max 12
    public void TryDecode_RejectsInvalid(string input)
        => Assert.False(Geohash.TryDecode(input, out _, out _, out _, out _));

    [Fact]
    public void Decode_KnownVector()
    {
        Assert.True(Geohash.TryDecode("u4pruydqqvj", out var lat, out var lon, out _, out _));
        Assert.Equal(57.64911, lat, 4);
        Assert.Equal(10.40744, lon, 4);
    }
}
