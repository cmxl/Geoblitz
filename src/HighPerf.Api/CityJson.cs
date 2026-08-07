using System.IO.Pipelines;
using System.Text.Json;
using HighPerf.Geo;

namespace HighPerf.Api;

internal static class PooledJson
{
    [ThreadStatic] private static Utf8JsonWriter? _cached;

    public static Utf8JsonWriter Rent(PipeWriter output)
    {
        var writer = _cached;
        _cached = null;
        if (writer is null)
            return new Utf8JsonWriter(output, new JsonWriterOptions { SkipValidation = true });
        writer.Reset(output);
        return writer;
    }

    public static void Return(Utf8JsonWriter writer) => _cached = writer;
}

internal static class CityJson
{
    public static void WriteCities(HttpResponse response, GeoDatabase db, ReadOnlySpan<GeoHit> hits)
    {
        response.ContentType = "application/json; charset=utf-8";
        var writer = PooledJson.Rent(response.BodyWriter);
        try
        {
            writer.WriteStartObject();
            writer.WriteNumber("count"u8, hits.Length);
            writer.WriteStartArray("cities"u8);
            foreach (var hit in hits)
            {
                writer.WriteStartObject();
                writer.WriteString("name"u8, db.GetNameUtf8(hit.Index));
                writer.WriteString("country"u8, db.GetCountry(hit.Index));
                writer.WriteNumber("population"u8, db.GetPopulation(hit.Index));
                writer.WriteNumber("lat"u8, db.GetLat(hit.Index));
                writer.WriteNumber("lon"u8, db.GetLon(hit.Index));
                writer.WriteNumber("distanceKm"u8, MathF.Round(hit.DistanceKm, 3));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush(); // commits to PipeWriter buffers; actual I/O flush is the caller's FlushAsync
        }
        finally
        {
            PooledJson.Return(writer);
        }
    }
}
