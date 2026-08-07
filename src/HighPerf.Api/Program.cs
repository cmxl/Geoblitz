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
builder.Services.AddSingleton<ComputeCounter>();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));
builder.Services.AddOutputCache(o =>
{
    o.AddPolicy("Geo", b => b
        .Expire(TimeSpan.FromMinutes(10))
        .SetVaryByQuery([])
        .VaryByValue((ctx, _) => ValueTask.FromResult(
            new KeyValuePair<string, string>("geo", GeoCacheKey.Compute(ctx)))));
});

var app = builder.Build();

app.UseExceptionHandler(errApp => errApp.Run(async ctx =>
{
    ctx.Response.StatusCode = 500;
    ctx.Response.ContentType = "application/problem+json";
    await ctx.Response.WriteAsJsonAsync(
        new ApiProblem("Internal error", 500, "An unexpected error occurred."),
        AppJsonContext.Default.ApiProblem, contentType: "application/problem+json");
}));

app.UseOutputCache();

app.MapGet("/healthz", () => Results.Text("ok"));

app.MapGet("/distance", (HttpContext ctx, ComputeCounter counter) =>
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
    ctx.Response.Headers["X-Compute-Count"] = counter.Increment().ToString();
    return Results.Json(new DistanceResponse(GeoMath.HaversineKm(fromLat, fromLon, toLat, toLon)),
        AppJsonContext.Default.DistanceResponse);
}).CacheOutput("Geo");

app.MapGet("/cities/nearest", (HttpContext ctx, GeoDatabase db, ComputeCounter counter) =>
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

    ctx.Response.Headers["X-Compute-Count"] = counter.Increment().ToString();
    Span<GeoHit> hits = stackalloc GeoHit[100];
    var n = db.FindNearest(lat, lon, count, hits);
    CityJson.WriteCities(ctx.Response, db, hits[..n]);
    return ctx.Response.BodyWriter.FlushAsync().AsTask();
}).CacheOutput("Geo");

app.MapGet("/cities/within", (HttpContext ctx, GeoDatabase db, ComputeCounter counter) =>
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

    ctx.Response.Headers["X-Compute-Count"] = counter.Increment().ToString();
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
}).CacheOutput("Geo");

app.MapGet("/geohash/encode", (HttpContext ctx, ComputeCounter counter) =>
{
    var qs = (ctx.Request.QueryString.Value ?? "").AsSpan();
    if (!QueryParams.TryGetDouble(qs, "lat", out var lat) || lat is < -90 or > 90)
        return Problems.Validation("lat must be a number in [-90, 90]");
    if (!QueryParams.TryGetDouble(qs, "lon", out var lon) || lon is < -180 or > 180)
        return Problems.Validation("lon must be a number in [-180, 180]");
    var precision = 9;
    if (QueryParams.TryGetRaw(qs, "precision", out _) &&
        (!QueryParams.TryGetInt(qs, "precision", out precision) || precision is < 1 or > Geohash.MaxPrecision))
        return Problems.Validation("precision must be an integer in [1, 12]");

    ctx.Response.Headers["X-Compute-Count"] = counter.Increment().ToString();
    Span<char> buffer = stackalloc char[Geohash.MaxPrecision];
    var n = Geohash.Encode(lat, lon, precision, buffer);
    return Results.Json(new GeohashEncodeResponse(new string(buffer[..n])),
        AppJsonContext.Default.GeohashEncodeResponse);
}).CacheOutput("Geo");

app.MapGet("/geohash/decode", (HttpContext ctx, ComputeCounter counter) =>
{
    var qs = (ctx.Request.QueryString.Value ?? "").AsSpan();
    if (!QueryParams.TryGetRaw(qs, "hash", out var hash)
        || !Geohash.TryDecode(hash, out var lat, out var lon, out var latErr, out var lonErr))
        return Problems.Validation("hash is required and must be a valid geohash (1-12 base32 chars)");
    ctx.Response.Headers["X-Compute-Count"] = counter.Increment().ToString();
    return Results.Json(new GeohashDecodeResponse(lat, lon, latErr, lonErr),
        AppJsonContext.Default.GeohashDecodeResponse);
}).CacheOutput("Geo");

app.Run();

public partial class Program;
