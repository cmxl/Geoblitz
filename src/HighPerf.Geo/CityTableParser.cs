using System.Buffers;
using System.Buffers.Text;
using System.IO.Compression;
using System.Text;

namespace HighPerf.Geo;

public static class CityTableParser
{
    private const byte Tab = (byte)'\t', Lf = (byte)'\n', Cr = (byte)'\r';

    public static ParsedCities LoadGzip(Stream gzipStream)
    {
        using var gz = new GZipStream(gzipStream, CompressionMode.Decompress);
        using var ms = new MemoryStream(8 * 1024 * 1024);
        gz.CopyTo(ms);
        return Parse(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    }

    public static ParsedCities Parse(ReadOnlySpan<byte> tsv)
    {
        var maxLines = tsv.Count(Lf) + 1;
        var lat = new float[maxLines];
        var lon = new float[maxLines];
        var pop = new int[maxLines];
        var country = new string[maxLines];
        var nameOffsets = new int[maxLines + 1];
        var nameBlob = new ArrayBufferWriter<byte>(Math.Max(64, tsv.Length / 4));
        var countryCache = new Dictionary<int, string>(300);
        var n = 0;

        while (!tsv.IsEmpty)
        {
            var nl = tsv.IndexOf(Lf);
            var line = nl < 0 ? tsv : tsv[..nl];
            tsv = nl < 0 ? default : tsv[(nl + 1)..];
            if (!line.IsEmpty && line[^1] == Cr) line = line[..^1];
            if (line.IsEmpty) continue;

            // name \t country \t lat \t lon \t population
            if (!NextField(ref line, out var nameField)) continue;
            if (!NextField(ref line, out var countryField)) continue;
            if (!NextField(ref line, out var latField)) continue;
            if (!NextField(ref line, out var lonField)) continue;
            var popField = line; // rest of line

            if (!Utf8Parser.TryParse(latField, out float latVal, out _)) continue;
            if (!Utf8Parser.TryParse(lonField, out float lonVal, out _)) continue;
            if (!Utf8Parser.TryParse(popField, out long popVal, out _)) popVal = 0;

            lat[n] = latVal;
            lon[n] = lonVal;
            pop[n] = (int)Math.Clamp(popVal, 0, int.MaxValue);
            country[n] = InternCountry(countryField, countryCache);
            nameBlob.Write(nameField);
            nameOffsets[n + 1] = nameOffsets[n] + nameField.Length;
            n++;
        }

        return new ParsedCities
        {
            Count = n,
            Lat = lat.AsSpan(0, n).ToArray(),
            Lon = lon.AsSpan(0, n).ToArray(),
            Population = pop.AsSpan(0, n).ToArray(),
            Country = country.AsSpan(0, n).ToArray(),
            NameBlob = nameBlob.WrittenSpan.ToArray(),
            NameOffsets = nameOffsets.AsSpan(0, n + 1).ToArray(),
        };
    }

    private static bool NextField(ref ReadOnlySpan<byte> line, out ReadOnlySpan<byte> field)
    {
        var t = line.IndexOf(Tab);
        if (t < 0) { field = default; return false; }
        field = line[..t];
        line = line[(t + 1)..];
        return true;
    }

    private static string InternCountry(ReadOnlySpan<byte> code, Dictionary<int, string> cache)
    {
        var key = code.Length switch
        {
            0 => 0,
            1 => code[0],
            _ => (code[0] << 8) | code[1],
        };
        if (!cache.TryGetValue(key, out var s))
            cache[key] = s = Encoding.ASCII.GetString(code);
        return s;
    }
}
