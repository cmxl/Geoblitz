using HighPerf.Api;
using HighPerf.Geo;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Warning()
    .WriteTo.Console()
    .CreateLogger();

var builder = WebApplication.CreateSlimBuilder(args);
builder.Host.UseSerilog();
builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);
builder.Services.AddSingleton(GeoDatabase.LoadDefault());
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));

var app = builder.Build();

app.UseExceptionHandler(errApp => errApp.Run(async ctx =>
{
    ctx.Response.StatusCode = 500;
    ctx.Response.ContentType = "application/problem+json";
    await ctx.Response.WriteAsJsonAsync(
        new ApiProblem("Internal error", 500, "An unexpected error occurred."),
        AppJsonContext.Default.ApiProblem, contentType: "application/problem+json");
}));

app.MapGet("/healthz", () => Results.Text("ok"));

app.MapGet("/distance", (HttpContext ctx) =>
{
    var qs = (ctx.Request.QueryString.Value ?? "").AsSpan();
    if (!QueryParams.TryGetDouble(qs, "fromLat", out var fromLat) || fromLat is < -90 or > 90)
        return Problems.Validation("fromLat must be a number in [-90, 90]");
    if (!QueryParams.TryGetDouble(qs, "fromLon", out var fromLon) || fromLon is < -180 or > 180)
        return Problems.Validation("fromLon must be a number in [-180, 180]");
    if (!QueryParams.TryGetDouble(qs, "toLat", out var toLat) || toLat is < -90 or > 90)
        return Problems.Validation("toLat must be a number in [-90, 90]");
    if (!QueryParams.TryGetDouble(qs, "toLon", out var toLon) || toLon is < -180 or > 180)
        return Problems.Validation("toLon must be a number in [-180, 180]");
    return Results.Json(new DistanceResponse(GeoMath.HaversineKm(fromLat, fromLon, toLat, toLon)),
        AppJsonContext.Default.DistanceResponse);
});

app.MapGet("/cities/nearest", (HttpContext ctx, GeoDatabase db) =>
{
    var qs = (ctx.Request.QueryString.Value ?? "").AsSpan();
    if (!QueryParams.TryGetDouble(qs, "lat", out var lat) || lat is < -90 or > 90)
        return Problems.Validation("lat must be a number in [-90, 90]").ExecuteAsync(ctx);
    if (!QueryParams.TryGetDouble(qs, "lon", out var lon) || lon is < -180 or > 180)
        return Problems.Validation("lon must be a number in [-180, 180]").ExecuteAsync(ctx);
    var count = 5;
    if (QueryParams.TryGetRaw(qs, "count", out _) &&
        (!QueryParams.TryGetInt(qs, "count", out count) || count is < 1 or > 100))
        return Problems.Validation("count must be an integer in [1, 100]").ExecuteAsync(ctx);

    Span<GeoHit> hits = stackalloc GeoHit[100];
    var n = db.FindNearest(lat, lon, count, hits);
    CityJson.WriteCities(ctx.Response, db, hits[..n]);
    return ctx.Response.BodyWriter.FlushAsync().AsTask();
});

app.MapGet("/cities/within", (HttpContext ctx, GeoDatabase db) =>
{
    var qs = (ctx.Request.QueryString.Value ?? "").AsSpan();
    if (!QueryParams.TryGetDouble(qs, "lat", out var lat) || lat is < -90 or > 90)
        return Problems.Validation("lat must be a number in [-90, 90]").ExecuteAsync(ctx);
    if (!QueryParams.TryGetDouble(qs, "lon", out var lon) || lon is < -180 or > 180)
        return Problems.Validation("lon must be a number in [-180, 180]").ExecuteAsync(ctx);
    if (!QueryParams.TryGetDouble(qs, "radiusKm", out var radiusKm) || radiusKm is <= 0 or > 500)
        return Problems.Validation("radiusKm is required and must be in (0, 500]").ExecuteAsync(ctx);
    var minPopulation = 0;
    if (QueryParams.TryGetRaw(qs, "minPopulation", out _) &&
        (!QueryParams.TryGetInt(qs, "minPopulation", out minPopulation) || minPopulation < 0))
        return Problems.Validation("minPopulation must be a non-negative integer").ExecuteAsync(ctx);

    var buffer = System.Buffers.ArrayPool<GeoHit>.Shared.Rent(1000);
    try
    {
        var n = db.FindWithin(lat, lon, radiusKm, minPopulation, buffer.AsSpan(0, 1000));
        CityJson.WriteCities(ctx.Response, db, buffer.AsSpan(0, n));
    }
    finally
    {
        System.Buffers.ArrayPool<GeoHit>.Shared.Return(buffer);
    }
    return ctx.Response.BodyWriter.FlushAsync().AsTask();
});

app.Run();

public partial class Program;
