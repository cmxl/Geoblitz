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

app.Run();

public partial class Program;
