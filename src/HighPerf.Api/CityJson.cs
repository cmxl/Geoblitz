using System.Diagnostics;
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
            // The exact body size is known here, before the headers go out, so declare it and keep
            // the compute path off chunked framing (Kestrel otherwise falls back to chunked once the
            // body has been advanced without a declared length). The output cache stores headers
            // verbatim, so the cached replay inherits this Content-Length instead of chunking too.
            // BytesPending alone is NOT the body size: Utf8JsonWriter.Grow() Advance()s the
            // PipeWriter whenever the current buffer fills up (which /cities/within routinely does),
            // moving those bytes into BytesCommitted. Only the sum is the whole body. Advance()
            // does not start the response, so the headers are still mutable at this point.
            Debug.Assert(!response.HasStarted, "Content-Length must be set before the headers are sent");
            response.ContentLength = writer.BytesCommitted + writer.BytesPending;
            writer.Flush(); // commits to PipeWriter buffers; actual I/O flush is the caller's FlushAsync
        }
        finally
        {
            PooledJson.Return(writer);
        }
    }
}
