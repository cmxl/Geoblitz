using System.Text.Json.Serialization;

namespace Geoblitz.Api;

internal readonly record struct ApiProblem(string Title, int Status, string Detail);

internal readonly record struct DistanceResponse(double Kilometers);

internal readonly record struct GeohashEncodeResponse(string Geohash);
internal readonly record struct GeohashDecodeResponse(double Lat, double Lon, double LatError, double LonError);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ApiProblem))]
[JsonSerializable(typeof(DistanceResponse))]
[JsonSerializable(typeof(GeohashEncodeResponse))]
[JsonSerializable(typeof(GeohashDecodeResponse))]
internal partial class AppJsonContext : JsonSerializerContext;

internal static class Problems
{
    public static IResult Validation(string detail)
        => Results.Json(new ApiProblem("Invalid request", 400, detail),
            AppJsonContext.Default.ApiProblem, contentType: "application/problem+json", statusCode: 400);
}
