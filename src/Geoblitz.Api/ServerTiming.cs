using System.Diagnostics;
using System.Globalization;

namespace Geoblitz.Api;

/// <summary>Emits Server-Timing for the engine compute section only. Must be called
/// BEFORE any body write so OutputCache stores and replays the header.</summary>
internal static class ServerTiming
{
    public static void Set(HttpContext ctx, long startTimestamp)
    {
        var ms = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        ctx.Response.Headers["Server-Timing"] =
            string.Create(CultureInfo.InvariantCulture, $"engine;dur={ms:F3}");
    }
}
