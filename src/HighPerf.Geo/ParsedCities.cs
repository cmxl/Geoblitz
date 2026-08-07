namespace HighPerf.Geo;

public sealed class ParsedCities
{
    public required int Count { get; init; }
    public required float[] Lat { get; init; }        // length Count
    public required float[] Lon { get; init; }
    public required int[] Population { get; init; }
    public required byte[] NameBlob { get; init; }     // UTF-8 names back to back
    public required int[] NameOffsets { get; init; }   // length Count + 1
    public required string[] Country { get; init; }    // interned 2-letter codes
    public ReadOnlySpan<byte> GetNameUtf8(int i) => NameBlob.AsSpan(NameOffsets[i], NameOffsets[i + 1] - NameOffsets[i]);
}
