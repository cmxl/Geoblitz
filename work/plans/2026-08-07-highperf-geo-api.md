# HighPerf Geo API Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A max-performance ASP.NET Core geo compute API (no database): SIMD distance kernels over an in-memory GeoNames cities dataset, zero-allocation hot paths, in-process output caching as a second layer.

**Architecture:** `HighPerf.Geo` is a pure compute library (struct-of-arrays dataset in grid-cell order, CSR grid index, vectorized chord-distance kernels). `HighPerf.Api` is a thin minimal-API adapter (span query parsing, source-gen JSON, `Utf8JsonWriter` directly into the response `PipeWriter`, OutputCache with quantized keys). Spec: `work/specs/2026-08-07-highperf-geo-api-design.md`.

**Tech Stack:** .NET 10 (SDK 10.0.302 installed), ASP.NET Core minimal APIs, System.Numerics `Vector<T>`, xUnit v3 + `WebApplicationFactory`, BenchmarkDotNet, Serilog, k6.

## Global Constraints

- TFM `net10.0` everywhere; `LangVersion` latest; nullable enabled; warnings as errors.
- Test stack: xUnit v3 (`xunit.v3` package, NOT xunit 2.x) + `Microsoft.NET.Test.Sdk` + `xunit.runner.visualstudio`. NSubstitute only if mocking is needed (it isn't, so don't add it).
- No Newtonsoft.Json, no MediatR, no Moq. JSON is System.Text.Json source-generated only.
- Hot paths (`FindWithin`, `FindNearest`, kernels, nearest/within endpoints): no LINQ, no closures, no `string.Split`, no per-request heap allocations except what's explicitly documented (StringValues from Kestrel, diagnostic header value).
- Distance semantics: great-circle on a sphere, `EarthRadiusKm = 6371.0088`. Internally distances compare as **squared 3D chord distance** between unit vectors (monotonic with great-circle distance, SIMD-friendly, no trig per point). This refines the spec's "precomputed SinLat/CosLat" line: we precompute full unit vectors X/Y/Z instead, which subsumes it (Z = sin lat) and eliminates ALL per-point trig.
- Coordinates: lat ∈ [-90, 90], lon ∈ [-180, 180]. Validation limits: `count` 1..100, `radiusKm` (0, 500], `precision` 1..12, `minPopulation` ≥ 0, within-results cap 1000.
- All code, comments, commits in English. Commit after every task (git repo already initialized on `main`).
- Per the user's global quality gates, dispatch the `noobit:stack-reviewer` agent on the accumulated diff before committing each task (executor may batch small adjacent tasks into one review, but never commit unreviewed BLOCKER/MAJOR-risk code).

## File Structure (locked in)

```
Directory.Build.props                    # shared MSBuild: TFM, lang, analyzers
HighPerformance.sln
tools/prepare-dataset.ps1                # GeoNames download → 5-column TSV → gzip
src/HighPerf.Geo/
  Resources/cities.tsv.gz                # embedded resource (generated, committed)
  GeoMath.cs                             # haversine, unit vectors, chord conversions
  CityTableParser.cs                     # TSV bytes → ParsedCities (SoA, original order)
  ParsedCities.cs
  GeoDatabase.cs                         # Build (cell-order permutation, CellStart), LoadDefault, FindWithin, FindNearest
  ChordKernel.cs                         # vectorized ScanWithin / ScanNearest
  HitBuffer.cs                           # ArrayPool-backed growable (index, distSq) buffer
  TopK.cs                                # stackalloc-backed max-heap ref struct
  Geohash.cs                             # encode/decode, stackalloc buffers
src/HighPerf.Api/
  Program.cs                             # slim builder, DI, endpoints, output cache, exception handler
  QueryParams.cs                         # span-based query-string parsing
  ApiTypes.cs                            # DTO record structs + ApiProblem + AppJsonContext
  CityJson.cs                            # Utf8JsonWriter → PipeWriter response writing + PooledJson
  GeoCacheKey.cs                         # quantized cache key
  ComputeCounter.cs                      # X-Compute-Count diagnostics singleton
tests/HighPerf.Geo.Tests/                # one test file per Geo source file
tests/HighPerf.Api.Tests/
  ApiFixture.cs                          # shared WebApplicationFactory (dataset loads once)
  HealthTests.cs  DistanceEndpointTests.cs  CitiesEndpointTests.cs
  GeohashEndpointTests.cs  CachingTests.cs
benchmarks/HighPerf.Benchmarks/          # BenchmarkDotNet: kernels, queries, allocations
loadtest/{distance,nearest,within,mixed}.js
docs/{index,architecture,performance-techniques,api,benchmarks}.md
```

---

### Task 1: Solution scaffold

**Files:**
- Create: `HighPerformance.sln`, `Directory.Build.props`, `.gitignore`, all four project files + references

**Interfaces:**
- Produces: compiling empty solution; namespaces `HighPerf.Geo`, `HighPerf.Api`; test projects wired to xUnit v3.

- [ ] **Step 1: Create solution, projects, references**

```powershell
dotnet new gitignore
dotnet new sln -n HighPerformance
dotnet new classlib -n HighPerf.Geo -o src/HighPerf.Geo
dotnet new web -n HighPerf.Api -o src/HighPerf.Api
dotnet new classlib -n HighPerf.Geo.Tests -o tests/HighPerf.Geo.Tests
dotnet new classlib -n HighPerf.Api.Tests -o tests/HighPerf.Api.Tests
dotnet new console -n HighPerf.Benchmarks -o benchmarks/HighPerf.Benchmarks
dotnet sln add src/HighPerf.Geo src/HighPerf.Api tests/HighPerf.Geo.Tests tests/HighPerf.Api.Tests benchmarks/HighPerf.Benchmarks
dotnet add src/HighPerf.Api reference src/HighPerf.Geo
dotnet add tests/HighPerf.Geo.Tests reference src/HighPerf.Geo
dotnet add tests/HighPerf.Api.Tests reference src/HighPerf.Api
dotnet add benchmarks/HighPerf.Benchmarks reference src/HighPerf.Geo
```

Delete the template `Class1.cs` files from HighPerf.Geo and both test projects.

- [ ] **Step 2: Write Directory.Build.props**

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: Replace project files**

`src/HighPerf.Geo/HighPerf.Geo.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <InternalsVisibleTo Include="HighPerf.Geo.Tests" />
    <InternalsVisibleTo Include="HighPerf.Benchmarks" />
  </ItemGroup>
</Project>
```

`src/HighPerf.Api/HighPerf.Api.csproj` (add inside existing `<Project Sdk="Microsoft.NET.Sdk.Web">`):

```xml
  <PropertyGroup>
    <ServerGarbageCollection>true</ServerGarbageCollection>
    <InvariantGlobalization>true</InvariantGlobalization>
    <TieredPgo>true</TieredPgo>
  </PropertyGroup>
```

Then: `dotnet add src/HighPerf.Api package Serilog.AspNetCore`

Both test csproj files get this exact shape (`HighPerf.Geo.Tests.csproj` shown; Api.Tests is identical plus the `Microsoft.AspNetCore.Mvc.Testing` package and its project reference to HighPerf.Api instead of HighPerf.Geo):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\HighPerf.Geo\HighPerf.Geo.csproj" />
  </ItemGroup>
</Project>
```

Then add test packages (latest stable):

```powershell
dotnet add tests/HighPerf.Geo.Tests package xunit.v3
dotnet add tests/HighPerf.Geo.Tests package xunit.runner.visualstudio
dotnet add tests/HighPerf.Geo.Tests package Microsoft.NET.Test.Sdk
dotnet add tests/HighPerf.Api.Tests package xunit.v3
dotnet add tests/HighPerf.Api.Tests package xunit.runner.visualstudio
dotnet add tests/HighPerf.Api.Tests package Microsoft.NET.Test.Sdk
dotnet add tests/HighPerf.Api.Tests package Microsoft.AspNetCore.Mvc.Testing
dotnet add benchmarks/HighPerf.Benchmarks package BenchmarkDotNet
```

- [ ] **Step 4: Verify build**

Run: `dotnet build`
Expected: Build succeeded, 0 warnings, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "chore: scaffold solution (Geo lib, Api, xUnit v3 tests, benchmarks)"
```

---

### Task 2: Dataset preparation script + embedded resource

**Files:**
- Create: `tools/prepare-dataset.ps1`, `src/HighPerf.Geo/Resources/cities.tsv.gz` (generated output, committed)
- Modify: `src/HighPerf.Geo/HighPerf.Geo.csproj` (embed resource)

**Interfaces:**
- Produces: embedded resource `cities.tsv.gz` — gzip of UTF-8 TSV, LF line endings, exactly 5 tab-separated columns per line: `name  countryCode  latitude  longitude  population` (GeoNames columns 1, 8, 4, 5, 14). ~140k lines. Later tasks load it via `Assembly.GetManifestResourceStream("cities.tsv.gz")`.

- [ ] **Step 1: Write `tools/prepare-dataset.ps1`**

```powershell
$ErrorActionPreference = 'Stop'
$tmp = Join-Path $env:TEMP 'geonames'
New-Item -ItemType Directory -Force $tmp | Out-Null
$zip = Join-Path $tmp 'cities1000.zip'
if (-not (Test-Path $zip)) {
    Invoke-WebRequest 'https://download.geonames.org/export/dump/cities1000.zip' -OutFile $zip
}
Expand-Archive $zip -DestinationPath $tmp -Force
$inFile = Join-Path $tmp 'cities1000.txt'
$outDir = Join-Path $PSScriptRoot '..\src\HighPerf.Geo\Resources'
New-Item -ItemType Directory -Force $outDir | Out-Null
$outGz = Join-Path $outDir 'cities.tsv.gz'

$reader = [IO.StreamReader]::new($inFile, [Text.Encoding]::UTF8)
$fs = [IO.File]::Create($outGz)
$gz = [IO.Compression.GZipStream]::new($fs, [IO.Compression.CompressionLevel]::Optimal)
$writer = [IO.StreamWriter]::new($gz, [Text.UTF8Encoding]::new($false))
$writer.NewLine = "`n"
$count = 0
while ($null -ne ($line = $reader.ReadLine())) {
    $f = $line.Split("`t")
    if ($f.Count -lt 15) { continue }
    $writer.WriteLine(($f[1], $f[8], $f[4], $f[5], $f[14]) -join "`t")
    $count++
}
$writer.Dispose(); $reader.Dispose()
Write-Host "Wrote $count cities to $outGz"
```

- [ ] **Step 2: Run it and verify**

Run: `pwsh tools/prepare-dataset.ps1`
Expected: "Wrote N cities" with N > 100000; `src/HighPerf.Geo/Resources/cities.tsv.gz` exists, size roughly 1.5–4 MB.

- [ ] **Step 3: Embed the resource**

Add to `src/HighPerf.Geo/HighPerf.Geo.csproj`:

```xml
  <ItemGroup>
    <EmbeddedResource Include="Resources\cities.tsv.gz" LogicalName="cities.tsv.gz" />
  </ItemGroup>
```

Run: `dotnet build src/HighPerf.Geo` — succeeds.

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "feat: GeoNames dataset prep script + embedded cities.tsv.gz"
```

---

### Task 3: GeoMath — scalar reference math

**Files:**
- Create: `src/HighPerf.Geo/GeoMath.cs`
- Test: `tests/HighPerf.Geo.Tests/GeoMathTests.cs`

**Interfaces:**
- Produces (all `public static` on `public static class GeoMath`, namespace `HighPerf.Geo`):
  - `const double EarthRadiusKm = 6371.0088`
  - `double HaversineKm(double lat1, double lon1, double lat2, double lon2)`
  - `void ToUnitVector(double latDeg, double lonDeg, out float x, out float y, out float z)`
  - `float KmToChordSq(double km)` — squared 3D chord length for a great-circle distance (clamped to half circumference)
  - `double ChordSqToKm(float chordSq)`

- [ ] **Step 1: Write failing tests**

`tests/HighPerf.Geo.Tests/GeoMathTests.cs`:

```csharp
using HighPerf.Geo;
using Xunit;

namespace HighPerf.Geo.Tests;

public class GeoMathTests
{
    [Fact]
    public void Haversine_BerlinToMunich_WithinOneKm()
        => Assert.InRange(GeoMath.HaversineKm(52.5200, 13.4050, 48.1374, 11.5755), 503.0, 506.0);

    [Fact]
    public void Haversine_SamePoint_IsZero()
        => Assert.Equal(0.0, GeoMath.HaversineKm(48.0, 11.0, 48.0, 11.0), 3);

    [Fact]
    public void Haversine_PoleToPole_IsHalfCircumference()
        => Assert.InRange(GeoMath.HaversineKm(90, 0, -90, 0), 20015.0, 20016.0);

    [Fact]
    public void Haversine_AcrossAntimeridian_OneDegreeAtEquator()
        => Assert.InRange(GeoMath.HaversineKm(0, 179.5, 0, -179.5), 111.0, 111.4);

    [Fact]
    public void UnitVector_HasLengthOne()
    {
        GeoMath.ToUnitVector(48.1374, 11.5755, out var x, out var y, out var z);
        Assert.Equal(1.0, Math.Sqrt((double)x * x + (double)y * y + (double)z * z), 5);
    }

    [Fact]
    public void ChordSq_RoundTrips_Km()
    {
        foreach (var km in new[] { 0.5, 10, 111, 1000, 5000, 15000 })
            Assert.Equal(km, GeoMath.ChordSqToKm(GeoMath.KmToChordSq(km)), 1);
    }

    [Fact]
    public void ChordDistance_MatchesHaversine_ForRandomPairs()
    {
        var rng = new Random(42);
        for (var i = 0; i < 500; i++)
        {
            double lat1 = rng.NextDouble() * 180 - 90, lon1 = rng.NextDouble() * 360 - 180;
            double lat2 = rng.NextDouble() * 180 - 90, lon2 = rng.NextDouble() * 360 - 180;
            GeoMath.ToUnitVector(lat1, lon1, out var x1, out var y1, out var z1);
            GeoMath.ToUnitVector(lat2, lon2, out var x2, out var y2, out var z2);
            float dx = x1 - x2, dy = y1 - y2, dz = z1 - z2;
            var viaChord = GeoMath.ChordSqToKm(dx * dx + dy * dy + dz * dz);
            var haversine = GeoMath.HaversineKm(lat1, lon1, lat2, lon2);
            Assert.True(Math.Abs(viaChord - haversine) <= Math.Max(1.0, haversine * 0.005),
                $"chord {viaChord} vs haversine {haversine}");
        }
    }
}
```

- [ ] **Step 2: Run tests, verify they fail**

Run: `dotnet test tests/HighPerf.Geo.Tests`
Expected: compile error "GeoMath does not exist" — that counts as the failing state.

- [ ] **Step 3: Implement `src/HighPerf.Geo/GeoMath.cs`**

```csharp
namespace HighPerf.Geo;

/// <summary>Spherical geo math. Internal comparisons use squared 3D chord distance
/// between unit vectors — monotonic with great-circle distance, no per-point trig.</summary>
public static class GeoMath
{
    public const double EarthRadiusKm = 6371.0088;
    private const double DegToRad = Math.PI / 180.0;

    public static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        double p1 = lat1 * DegToRad, p2 = lat2 * DegToRad;
        double dp = p2 - p1, dl = (lon2 - lon1) * DegToRad;
        var a = Math.Sin(dp / 2) * Math.Sin(dp / 2)
              + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }

    public static void ToUnitVector(double latDeg, double lonDeg, out float x, out float y, out float z)
    {
        double lat = latDeg * DegToRad, lon = lonDeg * DegToRad;
        var cosLat = Math.Cos(lat);
        x = (float)(cosLat * Math.Cos(lon));
        y = (float)(cosLat * Math.Sin(lon));
        z = (float)Math.Sin(lat);
    }

    public static float KmToChordSq(double km)
    {
        var half = Math.Min(km, EarthRadiusKm * Math.PI) / (2 * EarthRadiusKm);
        var chord = 2 * Math.Sin(half);
        return (float)(chord * chord);
    }

    public static double ChordSqToKm(float chordSq)
    {
        var chord = Math.Sqrt(Math.Clamp(chordSq, 0f, 4f));
        return 2 * EarthRadiusKm * Math.Asin(chord / 2);
    }
}
```

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test tests/HighPerf.Geo.Tests`
Expected: all GeoMathTests PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: GeoMath — haversine reference, unit vectors, chord conversions"
```

---

### Task 4: CityTableParser — TSV bytes → struct-of-arrays

**Files:**
- Create: `src/HighPerf.Geo/ParsedCities.cs`, `src/HighPerf.Geo/CityTableParser.cs`
- Test: `tests/HighPerf.Geo.Tests/CityTableParserTests.cs`

**Interfaces:**
- Produces:

```csharp
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

public static class CityTableParser
{
    public static ParsedCities LoadGzip(Stream gzipStream);   // decompress + Parse
    public static ParsedCities Parse(ReadOnlySpan<byte> tsv); // 5-col TSV, LF lines
}
```

- Input format (from Task 2): `name \t country \t lat \t lon \t population \n`. Lines with fewer than 5 fields or unparseable lat/lon are skipped. Empty/unparseable population → 0.

- [ ] **Step 1: Write failing tests**

`tests/HighPerf.Geo.Tests/CityTableParserTests.cs`:

```csharp
using System.IO.Compression;
using System.Text;
using HighPerf.Geo;
using Xunit;

namespace HighPerf.Geo.Tests;

public class CityTableParserTests
{
    private const string Sample =
        "Berlin\tDE\t52.52437\t13.41053\t3644826\n" +
        "Suva\tFJ\t-18.14161\t178.44149\t88271\n" +
        "São Paulo\tBR\t-23.5475\t-46.63611\t10021295\n" +
        "BadLine\tXX\tnotanumber\t1.0\t5\n" +
        "NoPop\tUS\t40.0\t-75.0\t\n";

    private static ParsedCities ParseSample() => CityTableParser.Parse(Encoding.UTF8.GetBytes(Sample));

    [Fact]
    public void Parses_ValidLines_SkipsBad()
    {
        var p = ParseSample();
        Assert.Equal(4, p.Count);
        Assert.Equal(52.52437f, p.Lat[0], 4);
        Assert.Equal(178.44149f, p.Lon[1], 4);
        Assert.Equal(10021295, p.Population[2]);
        Assert.Equal(0, p.Population[3]);
        Assert.Equal("DE", p.Country[0]);
        Assert.Equal("BR", p.Country[2]);
    }

    [Fact]
    public void Names_AreExactUtf8()
    {
        var p = ParseSample();
        Assert.Equal("Berlin", Encoding.UTF8.GetString(p.GetNameUtf8(0)));
        Assert.Equal("São Paulo", Encoding.UTF8.GetString(p.GetNameUtf8(2)));
        Assert.Equal(p.Count + 1, p.NameOffsets.Length);
    }

    [Fact]
    public void CountryCodes_AreInterned()
    {
        var bytes = Encoding.UTF8.GetBytes("A\tDE\t1\t1\t1\nB\tDE\t2\t2\t2\n");
        var p = CityTableParser.Parse(bytes);
        Assert.Same(p.Country[0], p.Country[1]);
    }

    [Fact]
    public void LoadGzip_RoundTrips()
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(Encoding.UTF8.GetBytes(Sample));
        ms.Position = 0;
        var p = CityTableParser.LoadGzip(ms);
        Assert.Equal(4, p.Count);
    }

    [Fact]
    public void EmptyInput_YieldsZeroCities()
    {
        var p = CityTableParser.Parse(ReadOnlySpan<byte>.Empty);
        Assert.Equal(0, p.Count);
        Assert.Single(p.NameOffsets);
    }
}
```

- [ ] **Step 2: Run tests, verify compile failure**

Run: `dotnet test tests/HighPerf.Geo.Tests`
Expected: FAIL — `ParsedCities`/`CityTableParser` not defined.

- [ ] **Step 3: Implement**

`src/HighPerf.Geo/ParsedCities.cs`: exactly the class from Interfaces above.

`src/HighPerf.Geo/CityTableParser.cs`:

```csharp
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
```

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test tests/HighPerf.Geo.Tests`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: span-based TSV city parser into struct-of-arrays"
```

---

### Task 5: GeoDatabase.Build — grid-cell ordering + CSR offsets

**Files:**
- Create: `src/HighPerf.Geo/GeoDatabase.cs`
- Test: `tests/HighPerf.Geo.Tests/GeoDatabaseBuildTests.cs`

**Interfaces:**
- Produces (`public sealed class GeoDatabase`, namespace `HighPerf.Geo`):
  - `static GeoDatabase Build(ParsedCities cities, double cellSizeDeg = 1.0)`
  - `static GeoDatabase LoadDefault()` — embedded resource `cities.tsv.gz` → `CityTableParser.LoadGzip` → `Build`
  - `int Count`
  - `float GetLat(int i)`, `float GetLon(int i)`, `int GetPopulation(int i)`, `string GetCountry(int i)`, `ReadOnlySpan<byte> GetNameUtf8(int i)`
  - internal (for tests/kernels/benchmarks): `float[] X, Y, Z; int[] CellStart; int LatCells, LonCells; double CellSizeDeg; int CellOfLat(double), CellOfLon(double)`
  - **Point data is permuted into cell order**: all points of grid cell c occupy indices `[CellStart[c], CellStart[c+1])` in every array. Cell id = `cellLat * LonCells + cellLon` — cells of one latitude row are contiguous ids, so a run of cells in a row is one contiguous data range.
- Consumes: `ParsedCities`, `GeoMath.ToUnitVector` (Tasks 3–4).

- [ ] **Step 1: Write failing tests**

`tests/HighPerf.Geo.Tests/GeoDatabaseBuildTests.cs`:

```csharp
using System.Text;
using HighPerf.Geo;
using Xunit;

namespace HighPerf.Geo.Tests;

public class GeoDatabaseBuildTests
{
    internal static ParsedCities Cities(params (string Name, double Lat, double Lon, int Pop)[] pts)
    {
        var sb = new StringBuilder();
        foreach (var p in pts)
            sb.Append(p.Name).Append("\tXX\t")
              .Append(p.Lat.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\t')
              .Append(p.Lon.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\t')
              .Append(p.Pop).Append('\n');
        return CityTableParser.Parse(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    [Fact]
    public void CellStart_IsMonotonic_AndCoversAllPoints()
    {
        var db = GeoDatabase.Build(Cities(("A", 0.5, 0.5, 1), ("B", 0.6, 0.4, 2), ("C", 50.2, 8.1, 3), ("D", -33.9, 151.2, 4)));
        Assert.Equal(180 * 360 + 1, db.CellStart.Length);
        for (var c = 0; c < db.CellStart.Length - 1; c++)
            Assert.True(db.CellStart[c] <= db.CellStart[c + 1]);
        Assert.Equal(4, db.CellStart[^1]);
    }

    [Fact]
    public void EveryPoint_LiesInItsOwnCellRange()
    {
        var db = GeoDatabase.Build(Cities(("A", 0.5, 0.5, 1), ("B", 0.6, 0.4, 2), ("C", 50.2, 8.1, 3),
                                           ("D", -33.9, 151.2, 4), ("E", 89.9, 179.9, 5), ("F", -89.9, -179.9, 6)));
        for (var i = 0; i < db.Count; i++)
        {
            var cell = db.CellOfLat(db.GetLat(i)) * db.LonCells + db.CellOfLon(db.GetLon(i));
            Assert.InRange(i, db.CellStart[cell], db.CellStart[cell + 1] - 1);
        }
    }

    [Fact]
    public void SameCellPoints_AreAdjacent_AndDataSurvivesPermutation()
    {
        var db = GeoDatabase.Build(Cities(("C", 50.2, 8.1, 3), ("A", 0.5, 0.5, 1), ("B", 0.6, 0.4, 2)));
        var names = new string[db.Count];
        for (var i = 0; i < db.Count; i++) names[i] = Encoding.UTF8.GetString(db.GetNameUtf8(i));
        var ai = Array.IndexOf(names, "A");
        var bi = Array.IndexOf(names, "B");
        Assert.Equal(1, Math.Abs(ai - bi)); // A and B share a 1-degree cell -> adjacent after permutation
        Assert.Equal(1, db.GetPopulation(ai));
        Assert.Equal(0.5f, db.GetLat(ai), 3);
    }

    [Fact]
    public void UnitVectors_MatchLatLon()
    {
        var db = GeoDatabase.Build(Cities(("A", 48.1374, 11.5755, 1)));
        GeoMath.ToUnitVector(db.GetLat(0), db.GetLon(0), out var x, out var y, out var z);
        Assert.Equal(x, db.X[0], 5);
        Assert.Equal(y, db.Y[0], 5);
        Assert.Equal(z, db.Z[0], 5);
    }

    [Fact]
    public void CellOf_ClampsEdges()
    {
        var db = GeoDatabase.Build(Cities(("A", 0, 0, 1)));
        Assert.Equal(179, db.CellOfLat(90));
        Assert.Equal(0, db.CellOfLat(-90));
        Assert.Equal(359, db.CellOfLon(180));
        Assert.Equal(0, db.CellOfLon(-180));
    }
}
```

- [ ] **Step 2: Run tests, verify compile failure**

Run: `dotnet test tests/HighPerf.Geo.Tests`
Expected: FAIL — `GeoDatabase` not defined.

- [ ] **Step 3: Implement `src/HighPerf.Geo/GeoDatabase.cs`**

```csharp
namespace HighPerf.Geo;

/// <summary>Immutable in-memory city database. Struct-of-arrays, permuted into grid-cell
/// order at build time so each cell is one contiguous, SIMD-scannable range.</summary>
public sealed class GeoDatabase
{
    internal readonly float[] X, Y, Z;
    private readonly float[] _lat, _lon;
    private readonly int[] _population;
    private readonly string[] _country;
    private readonly byte[] _nameBlob;
    private readonly int[] _nameOffsets;
    internal readonly int[] CellStart;

    public int Count { get; }
    internal int LatCells { get; }
    internal int LonCells { get; }
    internal double CellSizeDeg { get; }

    private GeoDatabase(ParsedCities src, double cellSizeDeg)
    {
        CellSizeDeg = cellSizeDeg;
        LatCells = (int)Math.Round(180 / cellSizeDeg);
        LonCells = (int)Math.Round(360 / cellSizeDeg);
        Count = src.Count;
        var cells = LatCells * LonCells;

        // counting sort by cell id
        var cellOf = new int[Count];
        var counts = new int[cells + 1];
        for (var i = 0; i < Count; i++)
        {
            var c = CellOfLat(src.Lat[i]) * LonCells + CellOfLon(src.Lon[i]);
            cellOf[i] = c;
            counts[c + 1]++;
        }
        for (var c = 0; c < cells; c++) counts[c + 1] += counts[c];
        CellStart = counts;

        _lat = new float[Count]; _lon = new float[Count];
        X = new float[Count]; Y = new float[Count]; Z = new float[Count];
        _population = new int[Count]; _country = new string[Count];
        _nameOffsets = new int[Count + 1];
        _nameBlob = new byte[src.NameBlob.Length];

        var next = new int[cells];
        Array.Copy(CellStart, next, cells);
        var order = new int[Count]; // order[dest] = source index
        for (var i = 0; i < Count; i++) order[next[cellOf[i]]++] = i;

        var blobPos = 0;
        for (var d = 0; d < Count; d++)
        {
            var s = order[d];
            _lat[d] = src.Lat[s]; _lon[d] = src.Lon[s];
            GeoMath.ToUnitVector(src.Lat[s], src.Lon[s], out X[d], out Y[d], out Z[d]);
            _population[d] = src.Population[s];
            _country[d] = src.Country[s];
            var name = src.GetNameUtf8(s);
            name.CopyTo(_nameBlob.AsSpan(blobPos));
            _nameOffsets[d] = blobPos;
            blobPos += name.Length;
        }
        _nameOffsets[Count] = blobPos;
    }

    public static GeoDatabase Build(ParsedCities cities, double cellSizeDeg = 1.0) => new(cities, cellSizeDeg);

    public static GeoDatabase LoadDefault()
    {
        using var stream = typeof(GeoDatabase).Assembly.GetManifestResourceStream("cities.tsv.gz")
            ?? throw new InvalidOperationException("Embedded resource 'cities.tsv.gz' not found.");
        return Build(CityTableParser.LoadGzip(stream));
    }

    public float GetLat(int i) => _lat[i];
    public float GetLon(int i) => _lon[i];
    public int GetPopulation(int i) => _population[i];
    public string GetCountry(int i) => _country[i];
    public ReadOnlySpan<byte> GetNameUtf8(int i) => _nameBlob.AsSpan(_nameOffsets[i], _nameOffsets[i + 1] - _nameOffsets[i]);

    internal int CellOfLat(double lat) => Math.Clamp((int)((lat + 90.0) / CellSizeDeg), 0, LatCells - 1);
    internal int CellOfLon(double lon) => Math.Clamp((int)((lon + 180.0) / CellSizeDeg), 0, LonCells - 1);
}
```

Permutation logic, for the reviewer: `order[next[cellOf[i]]++] = i` places each source index `i` into its destination slot, so `order[dest] = source`; the copy loop reads `order[d]`. The `EveryPoint_LiesInItsOwnCellRange` test proves it.

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test tests/HighPerf.Geo.Tests`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: GeoDatabase build — cell-ordered SoA layout with CSR grid offsets"
```

---

### Task 6: ChordKernel.ScanWithin + HitBuffer

**Files:**
- Create: `src/HighPerf.Geo/HitBuffer.cs`, `src/HighPerf.Geo/ChordKernel.cs`
- Test: `tests/HighPerf.Geo.Tests/ChordKernelTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace HighPerf.Geo;

/// ArrayPool-backed growable (index, chordSq) pair buffer. Struct — pass by ref only.
public struct HitBuffer : IDisposable
{
    public HitBuffer(int initialCapacity);       // rents from ArrayPool<int>/<float>
    public int Count { get; }
    public ReadOnlySpan<int> Indices { get; }    // first Count entries
    public ReadOnlySpan<float> DistSq { get; }
    public void Add(int index, float distSq);    // grows by renting double capacity
    public void SortByDistance();                // co-sorts DistSq/Indices ascending
    public int this[int i] { get; }              // index i after sort (reads Indices)
    public float DistSqAt(int i);
    public void Dispose();                       // returns arrays to pool
}

public static class ChordKernel
{
    /// Appends (baseIndex + i, dsq) for every point with dsq <= maxChordSq.
    public static void ScanWithin(
        ReadOnlySpan<float> xs, ReadOnlySpan<float> ys, ReadOnlySpan<float> zs,
        float qx, float qy, float qz, float maxChordSq,
        int baseIndex, ref HitBuffer hits);
}
```

- Consumes: nothing new. Vectorized with `System.Numerics.Vector<float>` (runtime-width: 256-bit on AVX2, 512-bit where the runtime enables it), scalar tail loop.

- [ ] **Step 1: Write failing tests**

`tests/HighPerf.Geo.Tests/ChordKernelTests.cs`:

```csharp
using HighPerf.Geo;
using Xunit;

namespace HighPerf.Geo.Tests;

public class ChordKernelTests
{
    private static (float[] xs, float[] ys, float[] zs) RandomPoints(int n, int seed)
    {
        var rng = new Random(seed);
        var xs = new float[n]; var ys = new float[n]; var zs = new float[n];
        for (var i = 0; i < n; i++)
            GeoMath.ToUnitVector(rng.NextDouble() * 180 - 90, rng.NextDouble() * 360 - 180,
                out xs[i], out ys[i], out zs[i]);
        return (xs, ys, zs);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(7)] [InlineData(8)]
    [InlineData(9)] [InlineData(31)] [InlineData(33)] [InlineData(1000)]
    public void ScanWithin_MatchesScalarReference_AllSizes(int n)
    {
        var (xs, ys, zs) = RandomPoints(n, seed: n + 1);
        GeoMath.ToUnitVector(48.0, 11.0, out var qx, out var qy, out var qz);
        var maxChordSq = GeoMath.KmToChordSq(3000);

        var expected = new List<(int Idx, float D)>();
        for (var i = 0; i < n; i++)
        {
            float dx = xs[i] - qx, dy = ys[i] - qy, dz = zs[i] - qz;
            var d = dx * dx + dy * dy + dz * dz;
            if (d <= maxChordSq) expected.Add((100 + i, d));
        }

        var hits = new HitBuffer(4);
        try
        {
            ChordKernel.ScanWithin(xs, ys, zs, qx, qy, qz, maxChordSq, baseIndex: 100, ref hits);
            Assert.Equal(expected.Count, hits.Count);
            for (var i = 0; i < hits.Count; i++)
            {
                Assert.Equal(expected[i].Idx, hits.Indices[i]);
                Assert.Equal(expected[i].D, hits.DistSq[i], 6);
            }
        }
        finally { hits.Dispose(); }
    }

    [Fact]
    public void HitBuffer_GrowsPastInitialCapacity()
    {
        var hits = new HitBuffer(2);
        try
        {
            for (var i = 0; i < 100; i++) hits.Add(i, 100 - i);
            Assert.Equal(100, hits.Count);
            Assert.Equal(99, hits.Indices[99]);
            Assert.Equal(1f, hits.DistSq[99], 5);
        }
        finally { hits.Dispose(); }
    }

    [Fact]
    public void HitBuffer_SortByDistance_CoSortsIndices()
    {
        var hits = new HitBuffer(4);
        try
        {
            hits.Add(10, 3f); hits.Add(11, 1f); hits.Add(12, 2f);
            hits.SortByDistance();
            Assert.Equal(new[] { 11, 12, 10 }, hits.Indices.ToArray());
            Assert.Equal(new[] { 1f, 2f, 3f }, hits.DistSq.ToArray());
        }
        finally { hits.Dispose(); }
    }

    [Fact]
    public void ScanWithin_NoMatches_LeavesBufferEmpty()
    {
        var (xs, ys, zs) = RandomPoints(50, 7);
        GeoMath.ToUnitVector(48.0, 11.0, out var qx, out var qy, out var qz);
        var hits = new HitBuffer(4);
        try
        {
            ChordKernel.ScanWithin(xs, ys, zs, qx, qy, qz, maxChordSq: 0f, 0, ref hits);
            Assert.Equal(0, hits.Count);
        }
        finally { hits.Dispose(); }
    }
}
```

- [ ] **Step 2: Run tests, verify compile failure**

Run: `dotnet test tests/HighPerf.Geo.Tests`
Expected: FAIL — types not defined.

- [ ] **Step 3: Implement**

`src/HighPerf.Geo/HitBuffer.cs`:

```csharp
using System.Buffers;

namespace HighPerf.Geo;

public struct HitBuffer : IDisposable
{
    private int[] _idx;
    private float[] _dsq;
    private int _count;

    public HitBuffer(int initialCapacity)
    {
        _idx = ArrayPool<int>.Shared.Rent(Math.Max(4, initialCapacity));
        _dsq = ArrayPool<float>.Shared.Rent(_idx.Length);
        _count = 0;
    }

    public readonly int Count => _count;
    public readonly ReadOnlySpan<int> Indices => _idx.AsSpan(0, _count);
    public readonly ReadOnlySpan<float> DistSq => _dsq.AsSpan(0, _count);
    public readonly int this[int i] => _idx[i];
    public readonly float DistSqAt(int i) => _dsq[i];

    public void Add(int index, float distSq)
    {
        if (_count == _idx.Length) Grow();
        _idx[_count] = index;
        _dsq[_count] = distSq;
        _count++;
    }

    public readonly void SortByDistance()
        => _dsq.AsSpan(0, _count).Sort(_idx.AsSpan(0, _count));

    private void Grow()
    {
        var newIdx = ArrayPool<int>.Shared.Rent(_idx.Length * 2);
        var newDsq = ArrayPool<float>.Shared.Rent(newIdx.Length);
        _idx.AsSpan(0, _count).CopyTo(newIdx);
        _dsq.AsSpan(0, _count).CopyTo(newDsq);
        ArrayPool<int>.Shared.Return(_idx);
        ArrayPool<float>.Shared.Return(_dsq);
        _idx = newIdx;
        _dsq = newDsq;
    }

    public void Dispose()
    {
        if (_idx is null) return;
        ArrayPool<int>.Shared.Return(_idx);
        ArrayPool<float>.Shared.Return(_dsq);
        _idx = null!;
        _dsq = null!;
        _count = 0;
    }
}
```

`src/HighPerf.Geo/ChordKernel.cs`:

```csharp
using System.Numerics;

namespace HighPerf.Geo;

public static class ChordKernel
{
    public static void ScanWithin(
        ReadOnlySpan<float> xs, ReadOnlySpan<float> ys, ReadOnlySpan<float> zs,
        float qx, float qy, float qz, float maxChordSq,
        int baseIndex, ref HitBuffer hits)
    {
        int n = xs.Length, i = 0;
        var w = Vector<float>.Count;
        if (Vector.IsHardwareAccelerated && n >= w)
        {
            var vqx = new Vector<float>(qx);
            var vqy = new Vector<float>(qy);
            var vqz = new Vector<float>(qz);
            var vmax = new Vector<float>(maxChordSq);
            for (; i <= n - w; i += w)
            {
                var dx = new Vector<float>(xs.Slice(i, w)) - vqx;
                var dy = new Vector<float>(ys.Slice(i, w)) - vqy;
                var dz = new Vector<float>(zs.Slice(i, w)) - vqz;
                var dsq = dx * dx + dy * dy + dz * dz;
                var mask = Vector.LessThanOrEqual(dsq, vmax);
                if (mask != Vector<int>.Zero)
                    for (var j = 0; j < w; j++)
                        if (mask[j] != 0)
                            hits.Add(baseIndex + i + j, dsq[j]);
            }
        }
        for (; i < n; i++)
        {
            float dx = xs[i] - qx, dy = ys[i] - qy, dz = zs[i] - qz;
            var dsq = dx * dx + dy * dy + dz * dz;
            if (dsq <= maxChordSq)
                hits.Add(baseIndex + i, dsq);
        }
    }
}
```

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test tests/HighPerf.Geo.Tests`
Expected: all PASS (including every size in the Theory — tail-handling proof).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: vectorized chord-distance scan kernel + pooled hit buffer"
```

---

### Task 7: TopK heap + ChordKernel.ScanNearest

**Files:**
- Create: `src/HighPerf.Geo/TopK.cs`
- Modify: `src/HighPerf.Geo/ChordKernel.cs` (add ScanNearest)
- Test: `tests/HighPerf.Geo.Tests/TopKTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace HighPerf.Geo;

/// Fixed-capacity max-heap over (key, index) keeping the K SMALLEST keys.
/// Backed by caller-provided spans (stackalloc at call sites). Zero allocations.
public ref struct TopK
{
    public TopK(Span<float> keys, Span<int> indices); // equal lengths, capacity = length
    public int Count { get; }
    public int Capacity { get; }
    /// Largest kept key when full, else float.PositiveInfinity. New candidates >= Threshold are rejected.
    public float Threshold { get; }
    public void Add(float key, int index);
    /// Writes entries ascending by key into the destination spans; returns count written.
    public int CopySortedTo(Span<float> keysOut, Span<int> indicesOut);
}

// added to ChordKernel:
public static void ScanNearest(
    ReadOnlySpan<float> xs, ReadOnlySpan<float> ys, ReadOnlySpan<float> zs,
    float qx, float qy, float qz, float maxChordSq,
    int baseIndex, ref TopK topk);
```

- `ScanNearest` = same loop as `ScanWithin` but candidates go through `topk.Add` and the vector threshold uses `Math.Min(maxChordSq, topk.Threshold)` re-broadcast per vector iteration (shrinks as the heap fills — cheap pruning).

- [ ] **Step 1: Write failing tests**

`tests/HighPerf.Geo.Tests/TopKTests.cs`:

```csharp
using HighPerf.Geo;
using Xunit;

namespace HighPerf.Geo.Tests;

public class TopKTests
{
    [Fact]
    public void Keeps_K_Smallest_SortedAscending()
    {
        var rng = new Random(1);
        var keys = new float[200];
        for (var i = 0; i < keys.Length; i++) keys[i] = (float)rng.NextDouble();

        var topk = new TopK(stackalloc float[5], stackalloc int[5]);
        for (var i = 0; i < keys.Length; i++) topk.Add(keys[i], i);

        Span<float> outK = stackalloc float[5];
        Span<int> outI = stackalloc int[5];
        var n = topk.CopySortedTo(outK, outI);

        var expected = keys.Select((k, i) => (k, i)).OrderBy(t => t.k).Take(5).ToArray();
        Assert.Equal(5, n);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(expected[i].k, outK[i], 6);
            Assert.Equal(expected[i].i, outI[i]);
        }
    }

    [Fact]
    public void FewerAddsThanCapacity_ReturnsAll()
    {
        var topk = new TopK(stackalloc float[10], stackalloc int[10]);
        topk.Add(3f, 30); topk.Add(1f, 10); topk.Add(2f, 20);
        Span<float> outK = stackalloc float[10];
        Span<int> outI = stackalloc int[10];
        var n = topk.CopySortedTo(outK, outI);
        Assert.Equal(3, n);
        Assert.Equal(new[] { 10, 20, 30 }, outI[..n].ToArray());
    }

    [Fact]
    public void Threshold_IsInfinity_UntilFull_ThenMaxKept()
    {
        var topk = new TopK(stackalloc float[2], stackalloc int[2]);
        Assert.Equal(float.PositiveInfinity, topk.Threshold);
        topk.Add(5f, 1);
        Assert.Equal(float.PositiveInfinity, topk.Threshold);
        topk.Add(3f, 2);
        Assert.Equal(5f, topk.Threshold);
        topk.Add(1f, 3); // evicts 5
        Assert.Equal(3f, topk.Threshold);
    }

    [Fact]
    public void ScanNearest_MatchesBruteForce()
    {
        var rng = new Random(9);
        const int n = 500;
        var xs = new float[n]; var ys = new float[n]; var zs = new float[n];
        for (var i = 0; i < n; i++)
            GeoMath.ToUnitVector(rng.NextDouble() * 180 - 90, rng.NextDouble() * 360 - 180,
                out xs[i], out ys[i], out zs[i]);
        GeoMath.ToUnitVector(10, 20, out var qx, out var qy, out var qz);

        var topk = new TopK(stackalloc float[8], stackalloc int[8]);
        ChordKernel.ScanNearest(xs, ys, zs, qx, qy, qz, float.PositiveInfinity, 0, ref topk);
        Span<float> outK = stackalloc float[8];
        Span<int> outI = stackalloc int[8];
        var count = topk.CopySortedTo(outK, outI);

        var brute = Enumerable.Range(0, n)
            .Select(i => (Idx: i, D: (xs[i] - qx) * (xs[i] - qx) + (ys[i] - qy) * (ys[i] - qy) + (zs[i] - qz) * (zs[i] - qz)))
            .OrderBy(t => t.D).Take(8).ToArray();

        Assert.Equal(8, count);
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(brute[i].Idx, outI[i]);
            Assert.Equal(brute[i].D, outK[i], 6);
        }
    }
}
```

(LINQ is fine in tests — the no-LINQ constraint applies to production hot paths only.)

- [ ] **Step 2: Run tests, verify compile failure**

Run: `dotnet test tests/HighPerf.Geo.Tests`
Expected: FAIL — `TopK`/`ScanNearest` not defined.

- [ ] **Step 3: Implement**

`src/HighPerf.Geo/TopK.cs`:

```csharp
namespace HighPerf.Geo;

public ref struct TopK
{
    private readonly Span<float> _keys;
    private readonly Span<int> _idx;
    private int _count;

    public TopK(Span<float> keys, Span<int> indices)
    {
        if (keys.Length != indices.Length || keys.IsEmpty)
            throw new ArgumentException("keys/indices must be same non-zero length");
        _keys = keys;
        _idx = indices;
        _count = 0;
    }

    public readonly int Count => _count;
    public readonly int Capacity => _keys.Length;
    public readonly float Threshold => _count == _keys.Length ? _keys[0] : float.PositiveInfinity;

    public void Add(float key, int index)
    {
        if (_count < _keys.Length)
        {
            _keys[_count] = key;
            _idx[_count] = index;
            _count++;
            SiftUp(_count - 1);
        }
        else if (key < _keys[0])
        {
            _keys[0] = key;
            _idx[0] = index;
            SiftDown();
        }
    }

    public readonly int CopySortedTo(Span<float> keysOut, Span<int> indicesOut)
    {
        _keys[.._count].CopyTo(keysOut);
        _idx[.._count].CopyTo(indicesOut);
        keysOut[.._count].Sort(indicesOut[.._count]);
        return _count;
    }

    private void SiftUp(int i)
    {
        while (i > 0)
        {
            var parent = (i - 1) / 2;
            if (_keys[i] <= _keys[parent]) break;
            (_keys[i], _keys[parent]) = (_keys[parent], _keys[i]);
            (_idx[i], _idx[parent]) = (_idx[parent], _idx[i]);
            i = parent;
        }
    }

    private void SiftDown()
    {
        var i = 0;
        while (true)
        {
            int l = 2 * i + 1, r = l + 1, largest = i;
            if (l < _count && _keys[l] > _keys[largest]) largest = l;
            if (r < _count && _keys[r] > _keys[largest]) largest = r;
            if (largest == i) break;
            (_keys[i], _keys[largest]) = (_keys[largest], _keys[i]);
            (_idx[i], _idx[largest]) = (_idx[largest], _idx[i]);
            i = largest;
        }
    }
}
```

Add to `src/HighPerf.Geo/ChordKernel.cs`:

```csharp
    public static void ScanNearest(
        ReadOnlySpan<float> xs, ReadOnlySpan<float> ys, ReadOnlySpan<float> zs,
        float qx, float qy, float qz, float maxChordSq,
        int baseIndex, ref TopK topk)
    {
        int n = xs.Length, i = 0;
        var w = Vector<float>.Count;
        if (Vector.IsHardwareAccelerated && n >= w)
        {
            var vqx = new Vector<float>(qx);
            var vqy = new Vector<float>(qy);
            var vqz = new Vector<float>(qz);
            for (; i <= n - w; i += w)
            {
                var limit = Math.Min(maxChordSq, topk.Threshold);
                var vmax = new Vector<float>(limit);
                var dx = new Vector<float>(xs.Slice(i, w)) - vqx;
                var dy = new Vector<float>(ys.Slice(i, w)) - vqy;
                var dz = new Vector<float>(zs.Slice(i, w)) - vqz;
                var dsq = dx * dx + dy * dy + dz * dz;
                var mask = Vector.LessThanOrEqual(dsq, vmax);
                if (mask != Vector<int>.Zero)
                    for (var j = 0; j < w; j++)
                        if (mask[j] != 0)
                            topk.Add(dsq[j], baseIndex + i + j);
            }
        }
        for (; i < n; i++)
        {
            float dx = xs[i] - qx, dy = ys[i] - qy, dz = zs[i] - qz;
            var dsq = dx * dx + dy * dy + dz * dz;
            if (dsq <= maxChordSq && dsq < topk.Threshold)
                topk.Add(dsq, baseIndex + i);
        }
    }
```

Note: `TopK.Add` also guards internally (`key < _keys[0]` when full), so the vector-path `<= vmax` filter being slightly stale is safe — it only lets through candidates the heap then rejects.

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test tests/HighPerf.Geo.Tests`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: stackalloc top-k max-heap + pruned nearest-scan kernel"
```

---

### Task 8: GeoDatabase.FindWithin — radius query over grid ranges

**Files:**
- Modify: `src/HighPerf.Geo/GeoDatabase.cs`
- Test: `tests/HighPerf.Geo.Tests/FindWithinTests.cs`

**Interfaces:**
- Produces:

```csharp
public readonly record struct GeoHit(int Index, float DistanceKm);          // GeoHit.cs or top of GeoDatabase.cs

// on GeoDatabase:
public int FindWithin(double lat, double lon, double radiusKm, int minPopulation, Span<GeoHit> results);
// returns hits written (<= results.Length), sorted by distance ascending; ties arbitrary.

internal readonly record struct DataRange(int Start, int End);              // [Start, End) into the SoA arrays
internal int GetCandidateRanges(double lat, double lon, double radiusKm, Span<DataRange> ranges);
// superset guarantee: every point within radiusKm lies inside one of the returned ranges.
// Max 2 ranges per latitude row (antimeridian wrap) -> callers pass Span[2 * LatCells].
```

- Consumes: `ChordKernel.ScanWithin`, `HitBuffer`, `GeoMath` (Tasks 3, 6).
- Algorithm: latitude row window from `radiusKm / 111.195` degrees; per row, longitude window from `latDegRadius / cos(rowLatEdgeClosestToEquator...)` — use the row edge with the LARGEST `|lat|` for the widest window (safe superset), clamp: if window ≥ 180° scan the whole row. Wrap negative/overflow lon cells into up to 2 segments. Each cell segment `[c0..c1]` in a row maps to the single contiguous data range `[CellStart[rowBase + c0], CellStart[rowBase + c1 + 1])`.

- [ ] **Step 1: Write failing tests**

`tests/HighPerf.Geo.Tests/FindWithinTests.cs`:

```csharp
using System.Text;
using HighPerf.Geo;
using Xunit;

namespace HighPerf.Geo.Tests;

public class FindWithinTests
{
    private static GeoDatabase Db(params (string Name, double Lat, double Lon, int Pop)[] pts)
        => GeoDatabase.Build(GeoDatabaseBuildTests.Cities(pts));

    private static string Name(GeoDatabase db, int index) => Encoding.UTF8.GetString(db.GetNameUtf8(index));

    [Fact]
    public void Finds_PointsInRadius_SortedByDistance()
    {
        var db = Db(("Munich", 48.1374, 11.5755, 1_471_508),
                    ("Freising", 48.4028, 11.7489, 45_227),
                    ("Berlin", 52.5244, 13.4105, 3_644_826));
        Span<GeoHit> hits = stackalloc GeoHit[10];
        var n = db.FindWithin(48.2, 11.6, 60, 0, hits);
        Assert.Equal(2, n);
        Assert.Equal("Munich", Name(db, hits[0].Index));
        Assert.Equal("Freising", Name(db, hits[1].Index));
        Assert.True(hits[0].DistanceKm < hits[1].DistanceKm);
        Assert.InRange(hits[0].DistanceKm, 6.0, 9.0); // ~7 km
    }

    [Fact]
    public void AntimeridianWrap_FindsBothSides()
    {
        var db = Db(("East", 0, 179.9, 1), ("West", 0, -179.9, 1), ("Far", 0, 0, 1));
        Span<GeoHit> hits = stackalloc GeoHit[10];
        var n = db.FindWithin(0, 179.99, 50, 0, hits);
        Assert.Equal(2, n);
        var names = new[] { Name(db, hits[0].Index), Name(db, hits[1].Index) };
        Assert.Contains("East", names);
        Assert.Contains("West", names);
    }

    [Fact]
    public void NearPole_WideLongitudeSpread_AllFound()
    {
        var db = Db(("P1", 89.0, 0, 1), ("P2", 89.0, 90, 1), ("P3", 89.0, 170, 1), ("Equator", 0, 0, 1));
        Span<GeoHit> hits = stackalloc GeoHit[10];
        var n = db.FindWithin(89.5, 45, 500, 0, hits);
        Assert.Equal(3, n);
    }

    [Fact]
    public void MinPopulation_Filters()
    {
        var db = Db(("Big", 48.0, 11.0, 1_000_000), ("Small", 48.01, 11.01, 500));
        Span<GeoHit> hits = stackalloc GeoHit[10];
        var n = db.FindWithin(48.0, 11.0, 50, 10_000, hits);
        Assert.Equal(1, n);
        Assert.Equal("Big", Name(db, hits[0].Index));
    }

    [Fact]
    public void ResultSpanSmallerThanMatches_ReturnsClosestOnes()
    {
        var db = Db(("A", 48.0, 11.0, 1), ("B", 48.1, 11.0, 1), ("C", 48.2, 11.0, 1), ("D", 48.3, 11.0, 1));
        Span<GeoHit> hits = stackalloc GeoHit[2];
        var n = db.FindWithin(48.0, 11.0, 500, 0, hits);
        Assert.Equal(2, n);
        Assert.Equal("A", Name(db, hits[0].Index));
        Assert.Equal("B", Name(db, hits[1].Index));
    }

    [Fact]
    public void NoMatches_ReturnsZero()
    {
        var db = Db(("A", 48.0, 11.0, 1));
        Span<GeoHit> hits = stackalloc GeoHit[4];
        Assert.Equal(0, db.FindWithin(-48.0, -11.0, 100, 0, hits));
    }

    [Fact]
    public void MatchesBruteForce_OnRandomData()
    {
        var rng = new Random(123);
        var pts = new (string, double, double, int)[3000];
        for (var i = 0; i < pts.Length; i++)
            pts[i] = ($"P{i}", rng.NextDouble() * 180 - 90, rng.NextDouble() * 360 - 180, 0);
        var db = GeoDatabase.Build(GeoDatabaseBuildTests.Cities(pts));

        var hits = new GeoHit[3000];
        for (var q = 0; q < 20; q++)
        {
            double qLat = rng.NextDouble() * 180 - 90, qLon = rng.NextDouble() * 360 - 180;
            const double radius = 400;
            var n = db.FindWithin(qLat, qLon, radius, 0, hits);

            var brute = 0;
            for (var i = 0; i < db.Count; i++)
                if (GeoMath.HaversineKm(qLat, qLon, db.GetLat(i), db.GetLon(i)) <= radius + 0.5)
                    brute++;
            // chord vs haversine float rounding can differ at the exact boundary; allow off-by-boundary
            Assert.InRange(n, brute - 2, brute + 2);
        }
    }

    [Fact]
    public void RealDataset_Berlin15km_FindsBerlinFirst()
    {
        var db = GeoDatabase.LoadDefault();
        Assert.True(db.Count > 100_000);
        var hits = new GeoHit[1000];
        var n = db.FindWithin(52.5200, 13.4050, 15, 0, hits);
        Assert.True(n > 0);
        Assert.Equal("Berlin", Encoding.UTF8.GetString(db.GetNameUtf8(hits[0].Index)));
        Assert.True(hits[0].DistanceKm < 3);
    }
}
```

- [ ] **Step 2: Run tests, verify compile failure**

Run: `dotnet test tests/HighPerf.Geo.Tests`
Expected: FAIL — `FindWithin`/`GeoHit` not defined.

- [ ] **Step 3: Implement in `GeoDatabase.cs`** (plus `GeoHit` record in `src/HighPerf.Geo/GeoHit.cs`)

```csharp
namespace HighPerf.Geo;

public readonly record struct GeoHit(int Index, float DistanceKm);
```

Add to `GeoDatabase`:

```csharp
    private const double KmPerLatDegree = 111.195; // EarthRadius * pi / 180

    public int FindWithin(double lat, double lon, double radiusKm, int minPopulation, Span<GeoHit> results)
    {
        GeoMath.ToUnitVector(lat, lon, out var qx, out var qy, out var qz);
        var maxChordSq = GeoMath.KmToChordSq(radiusKm);

        Span<DataRange> ranges = LatCells <= 256 ? stackalloc DataRange[2 * 256] : new DataRange[2 * LatCells];
        var rangeCount = GetCandidateRanges(lat, lon, radiusKm, ranges);

        var hits = new HitBuffer(1024);
        try
        {
            for (var r = 0; r < rangeCount; r++)
            {
                var (start, end) = (ranges[r].Start, ranges[r].End);
                if (start == end) continue;
                ChordKernel.ScanWithin(
                    X.AsSpan(start, end - start), Y.AsSpan(start, end - start), Z.AsSpan(start, end - start),
                    qx, qy, qz, maxChordSq, start, ref hits);
            }

            hits.SortByDistance();
            var written = 0;
            for (var i = 0; i < hits.Count && written < results.Length; i++)
            {
                var idx = hits[i];
                if (_population[idx] < minPopulation) continue;
                results[written++] = new GeoHit(idx, (float)GeoMath.ChordSqToKm(hits.DistSqAt(i)));
            }
            return written;
        }
        finally
        {
            hits.Dispose();
        }
    }

    internal int GetCandidateRanges(double lat, double lon, double radiusKm, Span<DataRange> ranges)
    {
        var latDegRadius = radiusKm / KmPerLatDegree;
        var li0 = CellOfLat(lat - latDegRadius);
        var li1 = CellOfLat(lat + latDegRadius);
        var count = 0;

        for (var li = li0; li <= li1; li++)
        {
            var rowBase = li * LonCells;
            // widest |lat| edge of this row decides the longitude window (superset-safe)
            var edge0 = Math.Abs(-90.0 + li * CellSizeDeg);
            var edge1 = Math.Abs(-90.0 + (li + 1) * CellSizeDeg);
            var maxAbsLat = Math.Min(89.9, Math.Max(edge0, edge1));
            var lonDegRadius = latDegRadius / Math.Cos(maxAbsLat * Math.PI / 180.0);

            if (lonDegRadius * 2 >= 360.0 - CellSizeDeg)
            {
                ranges[count++] = new DataRange(CellStart[rowBase], CellStart[rowBase + LonCells]);
                continue;
            }

            var c0 = (int)Math.Floor((lon - lonDegRadius + 180.0) / CellSizeDeg);
            var c1 = (int)Math.Floor((lon + lonDegRadius + 180.0) / CellSizeDeg);
            if (c1 - c0 + 1 >= LonCells)
            {
                ranges[count++] = new DataRange(CellStart[rowBase], CellStart[rowBase + LonCells]);
                continue;
            }

            var w0 = ((c0 % LonCells) + LonCells) % LonCells;
            var w1 = ((c1 % LonCells) + LonCells) % LonCells;
            if (w0 <= w1)
            {
                ranges[count++] = new DataRange(CellStart[rowBase + w0], CellStart[rowBase + w1 + 1]);
            }
            else // wraps the antimeridian: two segments
            {
                ranges[count++] = new DataRange(CellStart[rowBase + w0], CellStart[rowBase + LonCells]);
                ranges[count++] = new DataRange(CellStart[rowBase], CellStart[rowBase + w1 + 1]);
            }
        }
        return count;
    }
```

And the range record (bottom of GeoDatabase.cs or its own file):

```csharp
internal readonly record struct DataRange(int Start, int End);
```

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test tests/HighPerf.Geo.Tests`
Expected: all PASS, including the real-dataset Berlin smoke test.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: FindWithin radius query — grid candidate ranges + SIMD scan"
```

---

### Task 9: GeoDatabase.FindNearest — progressive-radius k-NN

**Files:**
- Modify: `src/HighPerf.Geo/GeoDatabase.cs`
- Test: `tests/HighPerf.Geo.Tests/FindNearestTests.cs`

**Interfaces:**
- Produces:

```csharp
// on GeoDatabase:
public int FindNearest(double lat, double lon, int k, Span<GeoHit> results);
// k clamped to [0, min(k, Count, results.Length, 128)]; results sorted ascending by distance.
```

- Consumes: `ChordKernel.ScanNearest`, `TopK`, `GetCandidateRanges` (Tasks 7–8).
- Algorithm: radius starts at 50 km, multiplies by 4 per round. A round scans all candidate ranges for the current radius into a fresh `TopK`. Done when (heap full AND k-th distance ≤ scanned radius) OR radius covered the whole planet (≥ 20016 km). Rescanning per round is acceptable: worst case is one full-dataset SIMD scan (~140k points), still sub-millisecond-ish; correctness is trivially auditable.

- [ ] **Step 1: Write failing tests**

`tests/HighPerf.Geo.Tests/FindNearestTests.cs`:

```csharp
using System.Text;
using HighPerf.Geo;
using Xunit;

namespace HighPerf.Geo.Tests;

public class FindNearestTests
{
    [Fact]
    public void MatchesBruteForce_OnRandomData()
    {
        var rng = new Random(77);
        var pts = new (string, double, double, int)[5000];
        for (var i = 0; i < pts.Length; i++)
            pts[i] = ($"P{i}", rng.NextDouble() * 180 - 90, rng.NextDouble() * 360 - 180, 0);
        var db = GeoDatabase.Build(GeoDatabaseBuildTests.Cities(pts));

        Span<GeoHit> hits = stackalloc GeoHit[10];
        for (var q = 0; q < 20; q++)
        {
            double qLat = rng.NextDouble() * 180 - 90, qLon = rng.NextDouble() * 360 - 180;
            var n = db.FindNearest(qLat, qLon, 10, hits);
            Assert.Equal(10, n);

            var brute = Enumerable.Range(0, db.Count)
                .Select(i => (Idx: i, D: GeoMath.HaversineKm(qLat, qLon, db.GetLat(i), db.GetLon(i))))
                .OrderBy(t => t.D).Take(10).ToArray();

            for (var i = 0; i < 10; i++)
                Assert.True(Math.Abs(hits[i].DistanceKm - brute[i].D) < 1.5,
                    $"q{q} rank {i}: got {hits[i].DistanceKm}, brute {brute[i].D}");
        }
    }

    [Fact]
    public void KLargerThanDataset_ReturnsAll()
    {
        var db = GeoDatabase.Build(GeoDatabaseBuildTests.Cities(("A", 1, 1, 0), ("B", 2, 2, 0)));
        Span<GeoHit> hits = stackalloc GeoHit[10];
        Assert.Equal(2, db.FindNearest(0, 0, 10, hits));
    }

    [Fact]
    public void KZero_ReturnsZero()
    {
        var db = GeoDatabase.Build(GeoDatabaseBuildTests.Cities(("A", 1, 1, 0)));
        Span<GeoHit> hits = stackalloc GeoHit[4];
        Assert.Equal(0, db.FindNearest(0, 0, 0, hits));
    }

    [Fact]
    public void SparseRegion_StillFindsK_AcrossExpansions()
    {
        // nearest neighbors far beyond the initial 50 km radius
        var db = GeoDatabase.Build(GeoDatabaseBuildTests.Cities(
            ("Far1", 30, 30, 0), ("Far2", 35, 35, 0), ("Far3", -40, -40, 0)));
        Span<GeoHit> hits = stackalloc GeoHit[3];
        var n = db.FindNearest(0, 0, 3, hits);
        Assert.Equal(3, n);
        Assert.True(hits[0].DistanceKm <= hits[1].DistanceKm && hits[1].DistanceKm <= hits[2].DistanceKm);
    }

    [Fact]
    public void RealDataset_NearestToBerlin_IsBerlin()
    {
        var db = GeoDatabase.LoadDefault();
        Span<GeoHit> hits = stackalloc GeoHit[1];
        var n = db.FindNearest(52.5200, 13.4050, 1, hits);
        Assert.Equal(1, n);
        Assert.Equal("Berlin", Encoding.UTF8.GetString(db.GetNameUtf8(hits[0].Index)));
    }
}
```

- [ ] **Step 2: Run tests, verify compile failure**

Run: `dotnet test tests/HighPerf.Geo.Tests`
Expected: FAIL — `FindNearest` not defined.

- [ ] **Step 3: Implement in `GeoDatabase.cs`**

```csharp
    private const double HalfCircumferenceKm = 20016.0;

    public int FindNearest(double lat, double lon, int k, Span<GeoHit> results)
    {
        k = Math.Min(Math.Min(k, Count), Math.Min(results.Length, 128));
        if (k <= 0) return 0;

        GeoMath.ToUnitVector(lat, lon, out var qx, out var qy, out var qz);
        Span<float> heapKeys = stackalloc float[128];
        Span<int> heapIdx = stackalloc int[128];
        Span<DataRange> ranges = LatCells <= 256 ? stackalloc DataRange[2 * 256] : new DataRange[2 * LatCells];
        Span<float> sortedKeys = stackalloc float[128];
        Span<int> sortedIdx = stackalloc int[128];

        var radiusKm = 50.0;
        while (true)
        {
            var topk = new TopK(heapKeys[..k], heapIdx[..k]);
            var maxChordSq = GeoMath.KmToChordSq(radiusKm);
            var rangeCount = GetCandidateRanges(lat, lon, radiusKm, ranges);
            for (var r = 0; r < rangeCount; r++)
            {
                var (start, end) = (ranges[r].Start, ranges[r].End);
                if (start == end) continue;
                ChordKernel.ScanNearest(
                    X.AsSpan(start, end - start), Y.AsSpan(start, end - start), Z.AsSpan(start, end - start),
                    qx, qy, qz, maxChordSq, start, ref topk);
            }

            var done = radiusKm >= HalfCircumferenceKm
                || (topk.Count == k && GeoMath.ChordSqToKm(topk.Threshold) <= radiusKm);
            if (done)
            {
                var n = topk.CopySortedTo(sortedKeys, sortedIdx);
                for (var i = 0; i < n; i++)
                    results[i] = new GeoHit(sortedIdx[i], (float)GeoMath.ChordSqToKm(sortedKeys[i]));
                return n;
            }
            radiusKm = Math.Min(radiusKm * 4, HalfCircumferenceKm);
        }
    }
```

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test tests/HighPerf.Geo.Tests`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: FindNearest k-NN — progressive radius expansion with stackalloc top-k"
```

---

### Task 10: Geohash encode/decode

**Files:**
- Create: `src/HighPerf.Geo/Geohash.cs`
- Test: `tests/HighPerf.Geo.Tests/GeohashTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace HighPerf.Geo;

public static class Geohash
{
    public const int MaxPrecision = 12;
    /// Writes `precision` base32 chars into dest; returns chars written. dest.Length >= precision.
    public static int Encode(double lat, double lon, int precision, Span<char> dest);
    /// False on empty input, length > 12, or invalid base32 char (a, i, l, o excluded by the alphabet).
    public static bool TryDecode(ReadOnlySpan<char> hash, out double lat, out double lon, out double latErr, out double lonErr);
}
```

- [ ] **Step 1: Write failing tests**

`tests/HighPerf.Geo.Tests/GeohashTests.cs`:

```csharp
using HighPerf.Geo;
using Xunit;

namespace HighPerf.Geo.Tests;

public class GeohashTests
{
    [Theory]
    [InlineData(57.64911, 10.40744, 11, "u4pruydqqvj")] // canonical test vector
    [InlineData(48.1374, 11.5755, 9, "u281ys9w3")]      // Munich (verify against geohash.org while implementing)
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
    [InlineData("")] [InlineData("abc!")] [InlineData("aaa")] // 'a' not in geohash alphabet
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
```

If the Munich expected string turns out wrong when the correct implementation passes the canonical vector but fails Munich, verify with an external geohash reference and fix the TEST constant, not the implementation (the canonical `u4pruydqqvj` vector is authoritative).

- [ ] **Step 2: Run tests, verify compile failure**

Run: `dotnet test tests/HighPerf.Geo.Tests`
Expected: FAIL — `Geohash` not defined.

- [ ] **Step 3: Implement `src/HighPerf.Geo/Geohash.cs`**

```csharp
namespace HighPerf.Geo;

public static class Geohash
{
    public const int MaxPrecision = 12;
    private const string Base32 = "0123456789bcdefghjkmnpqrstuvwxyz";

    public static int Encode(double lat, double lon, int precision, Span<char> dest)
    {
        double latLo = -90, latHi = 90, lonLo = -180, lonHi = 180;
        var evenBit = true;
        int bit = 0, ch = 0, written = 0;
        while (written < precision)
        {
            if (evenBit)
            {
                var mid = (lonLo + lonHi) / 2;
                if (lon >= mid) { ch = (ch << 1) | 1; lonLo = mid; } else { ch <<= 1; lonHi = mid; }
            }
            else
            {
                var mid = (latLo + latHi) / 2;
                if (lat >= mid) { ch = (ch << 1) | 1; latLo = mid; } else { ch <<= 1; latHi = mid; }
            }
            evenBit = !evenBit;
            if (++bit == 5)
            {
                dest[written++] = Base32[ch];
                bit = 0;
                ch = 0;
            }
        }
        return written;
    }

    public static bool TryDecode(ReadOnlySpan<char> hash, out double lat, out double lon, out double latErr, out double lonErr)
    {
        lat = lon = latErr = lonErr = 0;
        if (hash.IsEmpty || hash.Length > MaxPrecision) return false;

        double latLo = -90, latHi = 90, lonLo = -180, lonHi = 180;
        var evenBit = true;
        foreach (var c in hash)
        {
            var v = Base32.IndexOf(char.ToLowerInvariant(c));
            if (v < 0) return false;
            for (var b = 4; b >= 0; b--)
            {
                var bit = (v >> b) & 1;
                if (evenBit)
                {
                    var mid = (lonLo + lonHi) / 2;
                    if (bit == 1) lonLo = mid; else lonHi = mid;
                }
                else
                {
                    var mid = (latLo + latHi) / 2;
                    if (bit == 1) latLo = mid; else latHi = mid;
                }
                evenBit = !evenBit;
            }
        }
        lat = (latLo + latHi) / 2;
        lon = (lonLo + lonHi) / 2;
        latErr = (latHi - latLo) / 2;
        lonErr = (lonHi - lonLo) / 2;
        return true;
    }
}
```

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test tests/HighPerf.Geo.Tests`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: geohash encode/decode with stackalloc buffers"
```

---

### Task 11: API host — slim builder, DI, /healthz

**Files:**
- Modify: `src/HighPerf.Api/Program.cs`
- Create: `tests/HighPerf.Api.Tests/ApiFixture.cs`, `tests/HighPerf.Api.Tests/HealthTests.cs`

**Interfaces:**
- Produces: running host with `GeoDatabase` singleton; `public partial class Program` for `WebApplicationFactory`; shared xUnit fixture `ApiFixture` (collection `"api"`) that every API test class uses so the 140k-city dataset loads once per test run.
- Consumes: `GeoDatabase.LoadDefault()` (Task 5).

- [ ] **Step 1: Write failing test**

`tests/HighPerf.Api.Tests/ApiFixture.cs`:

```csharp
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace HighPerf.Api.Tests;

public sealed class ApiFixture : WebApplicationFactory<Program>
{
}

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>;
```

`tests/HighPerf.Api.Tests/HealthTests.cs`:

```csharp
using Xunit;

namespace HighPerf.Api.Tests;

[Collection("api")]
public class HealthTests(ApiFixture fixture)
{
    [Fact]
    public async Task Healthz_Returns200Ok()
    {
        using var client = fixture.CreateClient();
        var response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        Assert.Equal("ok", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
```

- [ ] **Step 2: Run test, verify failure**

Run: `dotnet test tests/HighPerf.Api.Tests`
Expected: FAIL — `Program` not accessible / healthz missing.

- [ ] **Step 3: Implement `src/HighPerf.Api/Program.cs`**

```csharp
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

var app = builder.Build();

app.MapGet("/healthz", () => Results.Text("ok"));

app.Run();

public partial class Program;
```

- [ ] **Step 4: Run test, verify pass**

Run: `dotnet test tests/HighPerf.Api.Tests`
Expected: PASS. If `CreateSlimBuilder` conflicts with `WebApplicationFactory` content-root resolution, set `builder.UseSetting("contentRoot", AppContext.BaseDirectory)` inside an `ApiFixture.ConfigureWebHost` override — try without it first.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: API host — slim builder, Serilog, GeoDatabase singleton, healthz"
```

---

### Task 12: Span query parsing, /distance endpoint, ProblemDetails + exception handler

**Files:**
- Create: `src/HighPerf.Api/QueryParams.cs`, `src/HighPerf.Api/ApiTypes.cs`
- Modify: `src/HighPerf.Api/Program.cs`
- Test: `tests/HighPerf.Api.Tests/DistanceEndpointTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace HighPerf.Api;

internal static class QueryParams   // allocation-free scan over Request.QueryString.Value
{
    public static bool TryGetDouble(ReadOnlySpan<char> queryString, ReadOnlySpan<char> name, out double value);
    public static bool TryGetInt(ReadOnlySpan<char> queryString, ReadOnlySpan<char> name, out int value);
    public static bool TryGetRaw(ReadOnlySpan<char> queryString, ReadOnlySpan<char> name, out ReadOnlySpan<char> raw);
    // TryGet* return false when the key is absent OR unparseable; callers distinguish "absent + default" via TryGetRaw.
}

internal readonly record struct ApiProblem(string Title, int Status, string Detail);
internal readonly record struct DistanceResponse(double Kilometers);

[JsonSerializable(typeof(ApiProblem))]
[JsonSerializable(typeof(DistanceResponse))]
internal partial class AppJsonContext : JsonSerializerContext;

internal static class Problems
{
    public static IResult Validation(string detail); // 400, application/problem+json, source-gen serialized
}
```

- Endpoint: `GET /distance?fromLat=&fromLon=&toLat=&toLon=` → `{"kilometers": 504.2}`; any missing/invalid/out-of-range param → 400 ApiProblem.
- Note: query values here are plain numbers, so percent-decoding is deliberately skipped in `QueryParams` (documented limitation; geohash values are base32 alnum — also safe).

- [ ] **Step 1: Write failing tests**

`tests/HighPerf.Api.Tests/DistanceEndpointTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Xunit;

namespace HighPerf.Api.Tests;

[Collection("api")]
public class DistanceEndpointTests(ApiFixture fixture)
{
    [Fact]
    public async Task BerlinToMunich_ReturnsAbout504()
    {
        using var client = fixture.CreateClient();
        var res = await client.GetAsync("/distance?fromLat=52.52&fromLon=13.405&toLat=48.1374&toLon=11.5755",
            TestContext.Current.CancellationToken);
        res.EnsureSuccessStatusCode();
        Assert.Equal("application/json", res.Content.Headers.ContentType!.MediaType);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.InRange(doc.RootElement.GetProperty("kilometers").GetDouble(), 503.0, 506.0);
    }

    [Theory]
    [InlineData("/distance")]                                                    // all missing
    [InlineData("/distance?fromLat=91&fromLon=0&toLat=0&toLon=0")]               // lat out of range
    [InlineData("/distance?fromLat=0&fromLon=181&toLat=0&toLon=0")]              // lon out of range
    [InlineData("/distance?fromLat=abc&fromLon=0&toLat=0&toLon=0")]              // not a number
    public async Task InvalidInput_Returns400Problem(string url)
    {
        using var client = fixture.CreateClient();
        var res = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType!.MediaType);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(400, doc.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("detail").GetString()));
    }
}
```

- [ ] **Step 2: Run tests, verify failure**

Run: `dotnet test tests/HighPerf.Api.Tests`
Expected: FAIL — 404 for /distance.

- [ ] **Step 3: Implement**

`src/HighPerf.Api/QueryParams.cs`:

```csharp
using System.Globalization;

namespace HighPerf.Api;

/// <summary>Allocation-free query-string lookup over the raw QueryString span.
/// Values must not be percent-encoded (all our params are numbers / base32).</summary>
internal static class QueryParams
{
    public static bool TryGetDouble(ReadOnlySpan<char> queryString, ReadOnlySpan<char> name, out double value)
    {
        value = 0;
        return TryGetRaw(queryString, name, out var raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public static bool TryGetInt(ReadOnlySpan<char> queryString, ReadOnlySpan<char> name, out int value)
    {
        value = 0;
        return TryGetRaw(queryString, name, out var raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    public static bool TryGetRaw(ReadOnlySpan<char> queryString, ReadOnlySpan<char> name, out ReadOnlySpan<char> raw)
    {
        raw = default;
        var qs = queryString;
        if (!qs.IsEmpty && qs[0] == '?') qs = qs[1..];
        while (!qs.IsEmpty)
        {
            var amp = qs.IndexOf('&');
            var pair = amp < 0 ? qs : qs[..amp];
            qs = amp < 0 ? default : qs[(amp + 1)..];
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;
            if (pair[..eq].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                raw = pair[(eq + 1)..];
                return true;
            }
        }
        return false;
    }
}
```

`src/HighPerf.Api/ApiTypes.cs`:

```csharp
using System.Text.Json.Serialization;

namespace HighPerf.Api;

internal readonly record struct ApiProblem(string Title, int Status, string Detail);

internal readonly record struct DistanceResponse(double Kilometers);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ApiProblem))]
[JsonSerializable(typeof(DistanceResponse))]
internal partial class AppJsonContext : JsonSerializerContext;

internal static class Problems
{
    public static IResult Validation(string detail)
        => Results.Json(new ApiProblem("Invalid request", 400, detail),
            AppJsonContext.Default.ApiProblem, contentType: "application/problem+json", statusCode: 400);
}
```

In `Program.cs`, after `var app = builder.Build();` add the exception handler and endpoint; also register the JSON context on the builder:

```csharp
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));
```

```csharp
app.UseExceptionHandler(errApp => errApp.Run(async ctx =>
{
    ctx.Response.StatusCode = 500;
    ctx.Response.ContentType = "application/problem+json";
    await ctx.Response.WriteAsJsonAsync(
        new ApiProblem("Internal error", 500, "An unexpected error occurred."),
        AppJsonContext.Default.ApiProblem, contentType: "application/problem+json");
}));

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
```

Add `using HighPerf.Api;` at the top of Program.cs.

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test tests/HighPerf.Api.Tests`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: /distance endpoint — span query parsing, source-gen JSON, problem details"
```

---

### Task 13: /cities/nearest + /cities/within — streaming Utf8JsonWriter responses

**Files:**
- Create: `src/HighPerf.Api/CityJson.cs`
- Modify: `src/HighPerf.Api/Program.cs`
- Test: `tests/HighPerf.Api.Tests/CitiesEndpointTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace HighPerf.Api;

internal static class CityJson
{
    /// Writes {"count":n,"cities":[{name,country,population,lat,lon,distanceKm}...]}
    /// synchronously into response.BodyWriter buffers (no flush).
    public static void WriteCities(HttpResponse response, GeoDatabase db, ReadOnlySpan<GeoHit> hits);
}

internal static class PooledJson    // thread-cached Utf8JsonWriter (SkipValidation)
{
    public static Utf8JsonWriter Rent(PipeWriter output);
    public static void Return(Utf8JsonWriter writer);
}
```

- Endpoints:
  - `GET /cities/nearest?lat=&lon=&count=` — count default 5, valid 1..100.
  - `GET /cities/within?lat=&lon=&radiusKm=&minPopulation=` — radiusKm required in (0, 500]; minPopulation default 0, ≥ 0; up to 1000 results.
- Handler pattern (zero per-request allocation apart from Kestrel internals): non-async lambda taking `HttpContext` + injected `GeoDatabase`, `stackalloc GeoHit[...]`/pooled buffer, sync `WriteCities`, then `return response.BodyWriter.FlushAsync().AsTask();` — allowed because `stackalloc` happens in the synchronous part only.
- Consumes: `GeoDatabase.FindNearest`/`FindWithin`/`GetNameUtf8`/`GetCountry`/`GetPopulation`/`GetLat`/`GetLon`, `GeoHit`, `QueryParams`, `Problems` (Tasks 8, 9, 12).

- [ ] **Step 1: Write failing tests**

`tests/HighPerf.Api.Tests/CitiesEndpointTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Xunit;

namespace HighPerf.Api.Tests;

[Collection("api")]
public class CitiesEndpointTests(ApiFixture fixture)
{
    [Fact]
    public async Task Nearest_Berlin_ReturnsBerlinFirst_SortedAscending()
    {
        using var client = fixture.CreateClient();
        var res = await client.GetAsync("/cities/nearest?lat=52.52&lon=13.405&count=5",
            TestContext.Current.CancellationToken);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = doc.RootElement;
        Assert.Equal(5, root.GetProperty("count").GetInt32());
        var cities = root.GetProperty("cities");
        Assert.Equal(5, cities.GetArrayLength());
        Assert.Equal("Berlin", cities[0].GetProperty("name").GetString());
        Assert.Equal("DE", cities[0].GetProperty("country").GetString());
        Assert.True(cities[0].GetProperty("population").GetInt32() > 1_000_000);
        var prev = -1.0;
        foreach (var c in cities.EnumerateArray())
        {
            var d = c.GetProperty("distanceKm").GetDouble();
            Assert.True(d >= prev);
            prev = d;
        }
    }

    [Fact]
    public async Task Nearest_DefaultCount_Is5()
    {
        using var client = fixture.CreateClient();
        var res = await client.GetAsync("/cities/nearest?lat=48.1374&lon=11.5755",
            TestContext.Current.CancellationToken);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(5, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Within_Munich30km_MinPopulationFilters()
    {
        using var client = fixture.CreateClient();
        var res = await client.GetAsync("/cities/within?lat=48.1374&lon=11.5755&radiusKm=30&minPopulation=1000000",
            TestContext.Current.CancellationToken);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var cities = doc.RootElement.GetProperty("cities");
        Assert.True(cities.GetArrayLength() >= 1);
        foreach (var c in cities.EnumerateArray())
            Assert.True(c.GetProperty("population").GetInt32() >= 1_000_000);
    }

    [Theory]
    [InlineData("/cities/nearest?lat=91&lon=0")]
    [InlineData("/cities/nearest?lat=0&lon=0&count=0")]
    [InlineData("/cities/nearest?lat=0&lon=0&count=101")]
    [InlineData("/cities/within?lat=0&lon=0")]                       // radius missing
    [InlineData("/cities/within?lat=0&lon=0&radiusKm=0")]
    [InlineData("/cities/within?lat=0&lon=0&radiusKm=501")]
    [InlineData("/cities/within?lat=0&lon=0&radiusKm=10&minPopulation=-1")]
    public async Task InvalidInput_Returns400(string url)
    {
        using var client = fixture.CreateClient();
        var res = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Names_WithNonAscii_AreValidJson()
    {
        using var client = fixture.CreateClient();
        // São Paulo region — exercises UTF-8 name blob escaping
        var res = await client.GetAsync("/cities/nearest?lat=-23.5475&lon=-46.63611&count=3",
            TestContext.Current.CancellationToken);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("São Paulo", doc.RootElement.GetProperty("cities")[0].GetProperty("name").GetString());
    }
}
```

- [ ] **Step 2: Run tests, verify failure**

Run: `dotnet test tests/HighPerf.Api.Tests`
Expected: FAIL — 404s.

- [ ] **Step 3: Implement**

`src/HighPerf.Api/CityJson.cs`:

```csharp
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
```

Add to `Program.cs` (before `app.Run();`):

```csharp
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
```

Note the validation branches call `.ExecuteAsync(ctx)` because these handlers return `Task`, not `IResult` — both paths must produce a `Task`.

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test tests/HighPerf.Api.Tests`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: nearest/within endpoints — pooled Utf8JsonWriter streaming into BodyWriter"
```

---

### Task 14: /geohash/encode + /geohash/decode endpoints

**Files:**
- Modify: `src/HighPerf.Api/Program.cs`, `src/HighPerf.Api/ApiTypes.cs`
- Test: `tests/HighPerf.Api.Tests/GeohashEndpointTests.cs`

**Interfaces:**
- Produces: DTOs added to `ApiTypes.cs` + `AppJsonContext`:

```csharp
internal readonly record struct GeohashEncodeResponse(string Geohash);
internal readonly record struct GeohashDecodeResponse(double Lat, double Lon, double LatError, double LonError);
// add [JsonSerializable(typeof(GeohashEncodeResponse))] and [JsonSerializable(typeof(GeohashDecodeResponse))]
```

- `GET /geohash/encode?lat=&lon=&precision=` — precision default 9, valid 1..12 → `{"geohash":"u281ys9w3"}`.
- `GET /geohash/decode?hash=u4pruydqqvj` → `{"lat":..,"lon":..,"latError":..,"lonError":..}`; invalid/missing hash → 400.
- Consumes: `Geohash` (Task 10), `QueryParams`/`Problems` (Task 12).

- [ ] **Step 1: Write failing tests**

`tests/HighPerf.Api.Tests/GeohashEndpointTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Xunit;

namespace HighPerf.Api.Tests;

[Collection("api")]
public class GeohashEndpointTests(ApiFixture fixture)
{
    [Fact]
    public async Task Encode_KnownVector()
    {
        using var client = fixture.CreateClient();
        var res = await client.GetAsync("/geohash/encode?lat=57.64911&lon=10.40744&precision=11",
            TestContext.Current.CancellationToken);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("u4pruydqqvj", doc.RootElement.GetProperty("geohash").GetString());
    }

    [Fact]
    public async Task Encode_DefaultPrecision_Is9()
    {
        using var client = fixture.CreateClient();
        var res = await client.GetAsync("/geohash/encode?lat=48.1374&lon=11.5755",
            TestContext.Current.CancellationToken);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(9, doc.RootElement.GetProperty("geohash").GetString()!.Length);
    }

    [Fact]
    public async Task Decode_KnownVector()
    {
        using var client = fixture.CreateClient();
        var res = await client.GetAsync("/geohash/decode?hash=u4pruydqqvj", TestContext.Current.CancellationToken);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(57.64911, doc.RootElement.GetProperty("lat").GetDouble(), 4);
        Assert.Equal(10.40744, doc.RootElement.GetProperty("lon").GetDouble(), 4);
        Assert.True(doc.RootElement.GetProperty("latError").GetDouble() > 0);
    }

    [Theory]
    [InlineData("/geohash/encode?lat=91&lon=0")]
    [InlineData("/geohash/encode?lat=0&lon=0&precision=0")]
    [InlineData("/geohash/encode?lat=0&lon=0&precision=13")]
    [InlineData("/geohash/decode")]
    [InlineData("/geohash/decode?hash=aaa")]
    public async Task InvalidInput_Returns400(string url)
    {
        using var client = fixture.CreateClient();
        var res = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
```

- [ ] **Step 2: Run tests, verify failure** — `dotnet test tests/HighPerf.Api.Tests` → 404s.

- [ ] **Step 3: Implement** — add DTOs + context entries to `ApiTypes.cs`, then in `Program.cs`:

```csharp
app.MapGet("/geohash/encode", (HttpContext ctx) =>
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

    Span<char> buffer = stackalloc char[Geohash.MaxPrecision];
    var n = Geohash.Encode(lat, lon, precision, buffer);
    return Results.Json(new GeohashEncodeResponse(new string(buffer[..n])),
        AppJsonContext.Default.GeohashEncodeResponse);
});

app.MapGet("/geohash/decode", (HttpContext ctx) =>
{
    var qs = (ctx.Request.QueryString.Value ?? "").AsSpan();
    if (!QueryParams.TryGetRaw(qs, "hash", out var hash)
        || !Geohash.TryDecode(hash, out var lat, out var lon, out var latErr, out var lonErr))
        return Problems.Validation("hash is required and must be a valid geohash (1-12 base32 chars)");
    return Results.Json(new GeohashDecodeResponse(lat, lon, latErr, lonErr),
        AppJsonContext.Default.GeohashDecodeResponse);
});
```

- [ ] **Step 4: Run tests, verify pass** — `dotnet test tests/HighPerf.Api.Tests`.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: geohash encode/decode endpoints"
```

---

### Task 15: Output caching — quantized keys + compute counter

**Files:**
- Create: `src/HighPerf.Api/GeoCacheKey.cs`, `src/HighPerf.Api/ComputeCounter.cs`
- Modify: `src/HighPerf.Api/Program.cs` (AddOutputCache, UseOutputCache, `.CacheOutput("Geo")` on all 5 geo endpoints, counter header in each handler)
- Test: `tests/HighPerf.Api.Tests/CachingTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace HighPerf.Api;

internal sealed class ComputeCounter
{
    public long Increment(); // Interlocked; returns new value
}

internal static class GeoCacheKey
{
    /// Normalized key: lat/lon/from*/to* quantized to 3 decimals (~110 m buckets),
    /// plus count/radiusKm/minPopulation/precision/hash verbatim. Invariant culture.
    public static string Compute(HttpContext ctx);
}
```

- Wiring in `Program.cs`:

```csharp
builder.Services.AddSingleton<ComputeCounter>();
builder.Services.AddOutputCache(o =>
{
    o.AddPolicy("Geo", b => b
        .Expire(TimeSpan.FromMinutes(10))
        .SetVaryByQuery([])
        .VaryByValue((ctx, _) => ValueTask.FromResult(
            new KeyValuePair<string, string>("geo", GeoCacheKey.Compute(ctx)))));
});
// after UseExceptionHandler:
app.UseOutputCache();
// each geo endpoint chain: .CacheOutput("Geo")   (healthz stays uncached)
```

- Every geo handler sets `ctx.Response.Headers["X-Compute-Count"] = counter.Increment().ToString();` BEFORE writing the body (inject `ComputeCounter counter` into the delegates). Because OutputCache stores and replays headers, a replayed (cached) response carries the ORIGINAL count — two responses with equal X-Compute-Count prove a cache hit. This replaces the spec's vague "X-Cache header" with something OutputCache supports naturally; documented in docs task.
- `GeoCacheKey.Compute` allocates one small string per MISS-path evaluation — accepted and documented (the alternative, caching raw query strings, makes the cache useless: `lat=48.13701` vs `lat=48.13702` would be distinct entries).

`GeoCacheKey.cs` implementation:

```csharp
using System.Globalization;

namespace HighPerf.Api;

internal static class GeoCacheKey
{
    public static string Compute(HttpContext ctx)
    {
        var qs = (ctx.Request.QueryString.Value ?? "").AsSpan();
        var lat = Quantized(qs, "lat");
        var lon = Quantized(qs, "lon");
        var fromLat = Quantized(qs, "fromLat");
        var fromLon = Quantized(qs, "fromLon");
        var toLat = Quantized(qs, "toLat");
        var toLon = Quantized(qs, "toLon");
        QueryParams.TryGetInt(qs, "count", out var count);
        QueryParams.TryGetDouble(qs, "radiusKm", out var radius);
        QueryParams.TryGetInt(qs, "minPopulation", out var minPop);
        QueryParams.TryGetInt(qs, "precision", out var precision);
        QueryParams.TryGetRaw(qs, "hash", out var hash);
        return string.Create(CultureInfo.InvariantCulture,
            $"{lat}|{lon}|{fromLat}|{fromLon}|{toLat}|{toLon}|{count}|{radius}|{minPop}|{precision}|{hash}");
    }

    private static double Quantized(ReadOnlySpan<char> qs, string name)
        => QueryParams.TryGetDouble(qs, name, out var v) ? Math.Round(v, 3) : double.NaN;
}
```

- [ ] **Step 1: Write failing tests**

`tests/HighPerf.Api.Tests/CachingTests.cs`:

```csharp
using Xunit;

namespace HighPerf.Api.Tests;

[Collection("api")]
public class CachingTests(ApiFixture fixture)
{
    private static string CountHeader(HttpResponseMessage res)
        => Assert.Single(res.Headers.GetValues("X-Compute-Count"));

    [Fact]
    public async Task IdenticalRequests_SecondIsServedFromCache()
    {
        using var client = fixture.CreateClient();
        const string url = "/cities/nearest?lat=50.001&lon=8.001&count=7";
        var first = await client.GetAsync(url, TestContext.Current.CancellationToken);
        var second = await client.GetAsync(url, TestContext.Current.CancellationToken);
        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();
        Assert.Equal(CountHeader(first), CountHeader(second)); // replayed header == cache hit
        Assert.Equal(await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
                     await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NearbyCoordinates_SameQuantizedBucket_ShareCacheEntry()
    {
        using var client = fixture.CreateClient();
        var a = await client.GetAsync("/cities/nearest?lat=51.00010&lon=9.00010&count=3",
            TestContext.Current.CancellationToken);
        var b = await client.GetAsync("/cities/nearest?lat=51.00012&lon=9.00012&count=3",
            TestContext.Current.CancellationToken); // rounds to same 3-decimal bucket
        Assert.Equal(CountHeader(a), CountHeader(b));
    }

    [Fact]
    public async Task DifferentCount_IsACacheMiss()
    {
        using var client = fixture.CreateClient();
        var a = await client.GetAsync("/cities/nearest?lat=52.100&lon=10.100&count=3",
            TestContext.Current.CancellationToken);
        var b = await client.GetAsync("/cities/nearest?lat=52.100&lon=10.100&count=4",
            TestContext.Current.CancellationToken);
        Assert.NotEqual(CountHeader(a), CountHeader(b));
    }

    [Fact]
    public async Task DifferentBucket_IsACacheMiss()
    {
        using var client = fixture.CreateClient();
        var a = await client.GetAsync("/cities/nearest?lat=53.101&lon=10.100&count=3",
            TestContext.Current.CancellationToken);
        var b = await client.GetAsync("/cities/nearest?lat=53.109&lon=10.100&count=3",
            TestContext.Current.CancellationToken);
        Assert.NotEqual(CountHeader(a), CountHeader(b));
    }
}
```

Use coordinates unique to this test file (as above) so entries cached by other test classes can't collide.

- [ ] **Step 2: Run tests, verify failure** — X-Compute-Count header absent → tests fail.

- [ ] **Step 3: Implement** — `ComputeCounter` (`Interlocked.Increment` on a `long` field), `GeoCacheKey` as above, wire OutputCache + `.CacheOutput("Geo")` + header writes into all five geo endpoints. Order in pipeline: `UseExceptionHandler` → `UseOutputCache` → endpoints.

`ComputeCounter.cs`:

```csharp
namespace HighPerf.Api;

internal sealed class ComputeCounter
{
    private long _value;
    public long Increment() => Interlocked.Increment(ref _value);
}
```

In each geo handler, first line after validation passes:

```csharp
ctx.Response.Headers["X-Compute-Count"] = counter.Increment().ToString();
```

(inject `ComputeCounter counter` as an additional delegate parameter alongside `GeoDatabase db`).

- [ ] **Step 4: Run ALL tests, verify pass** — `dotnet test` (whole solution; earlier endpoint tests must still pass with caching active — they use distinct coordinates, and identical replays return identical bodies anyway).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: output caching with quantized coordinate keys + compute counter"
```

---

### Task 16: Benchmarks — prove the numbers

**Files:**
- Create: `benchmarks/HighPerf.Benchmarks/Program.cs`, `benchmarks/HighPerf.Benchmarks/GeoBenchmarks.cs`

**Interfaces:**
- Consumes: `GeoDatabase` internals (`X/Y/Z`, InternalsVisibleTo), `ChordKernel`, `GeoMath`, `HitBuffer`, `TopK`.

- [ ] **Step 1: Write the benchmark suite**

`benchmarks/HighPerf.Benchmarks/Program.cs`:

```csharp
using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(HighPerf.Benchmarks.GeoBenchmarks).Assembly).Run(args);
```

`benchmarks/HighPerf.Benchmarks/GeoBenchmarks.cs`:

```csharp
using BenchmarkDotNet.Attributes;
using HighPerf.Geo;

namespace HighPerf.Benchmarks;

[MemoryDiagnoser]
public class GeoBenchmarks
{
    private GeoDatabase _db = null!;
    private GeoHit[] _hits = null!;
    private float _qx, _qy, _qz;

    [GlobalSetup]
    public void Setup()
    {
        _db = GeoDatabase.LoadDefault();
        _hits = new GeoHit[1000];
        GeoMath.ToUnitVector(52.52, 13.405, out _qx, out _qy, out _qz);
    }

    [Benchmark(Baseline = true)]
    public double Scalar_HaversineFullScan()
    {
        double best = double.MaxValue;
        for (var i = 0; i < _db.Count; i++)
        {
            var d = GeoMath.HaversineKm(52.52, 13.405, _db.GetLat(i), _db.GetLon(i));
            if (d < best) best = d;
        }
        return best;
    }

    [Benchmark]
    public int Simd_ChordFullScan()
    {
        var hits = new HitBuffer(1024);
        try
        {
            ChordKernel.ScanWithin(_db.X, _db.Y, _db.Z, _qx, _qy, _qz,
                GeoMath.KmToChordSq(100), 0, ref hits);
            return hits.Count;
        }
        finally { hits.Dispose(); }
    }

    [Benchmark]
    public int Grid_FindWithin100km()
        => _db.FindWithin(52.52, 13.405, 100, 0, _hits);

    [Benchmark]
    public int Grid_FindNearest10()
        => _db.FindNearest(52.52, 13.405, 10, _hits.AsSpan(0, 10));

    [Benchmark]
    public int Grid_FindNearest10_SparseOcean()
        => _db.FindNearest(-45.0, -140.0, 10, _hits.AsSpan(0, 10)); // forces radius expansion rounds

    [Benchmark]
    public double Scalar_HaversineSinglePair()
        => GeoMath.HaversineKm(52.52, 13.405, 48.1374, 11.5755);
}
```

- [ ] **Step 2: Smoke-run**

Run: `dotnet run -c Release --project benchmarks/HighPerf.Benchmarks -- --filter "*GeoBenchmarks*" --job short`
Expected: completes; `Grid_FindWithin100km` and `Grid_FindNearest10` report **0 B allocated** (Gen0 column empty) — that's the zero-allocation proof. `Simd_ChordFullScan` should beat `Scalar_HaversineFullScan` by roughly an order of magnitude; `Grid_*` should beat `Simd_ChordFullScan` again. If allocations appear, find and fix them before proceeding (MemoryDiagnoser column tells you the bytes; typical culprits: boxed spans, closure captures, forgotten `ref`).

- [ ] **Step 3: Save results snapshot**

Copy the summary table into `benchmarks/RESULTS.md` with the machine description (CPU, RAM, OS, .NET version) and the command used.

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "feat: BenchmarkDotNet suite proving SIMD speedup and zero-allocation queries"
```

---

### Task 17: k6 load-test scripts

**Files:**
- Create: `loadtest/distance.js`, `loadtest/nearest.js`, `loadtest/within.js`, `loadtest/mixed.js`, `loadtest/README.md`

- [ ] **Step 1: Write the scripts**

`loadtest/mixed.js` (the other three are single-endpoint variants of the same shape — same options block, one URL each, cache-busting random coordinates as below):

```javascript
import http from 'k6/http';
import { check } from 'k6';

export const options = {
    scenarios: {
        mixed: { executor: 'constant-vus', vus: 32, duration: '30s' },
    },
    thresholds: {
        http_req_duration: ['p(95)<20', 'p(99)<50'], // ms — tune after first real run
        http_req_failed: ['rate<0.001'],
    },
};

const BASE = __ENV.BASE_URL || 'http://localhost:5000';

// ~30% repeated hot coordinates (cache hits), ~70% random (compute path)
function coords() {
    if (Math.random() < 0.3) return { lat: 52.52, lon: 13.405 };
    return { lat: (Math.random() * 180 - 90).toFixed(3), lon: (Math.random() * 360 - 180).toFixed(3) };
}

export default function () {
    const { lat, lon } = coords();
    const pick = Math.random();
    let res;
    if (pick < 0.4) {
        res = http.get(`${BASE}/cities/nearest?lat=${lat}&lon=${lon}&count=10`);
    } else if (pick < 0.7) {
        res = http.get(`${BASE}/cities/within?lat=${lat}&lon=${lon}&radiusKm=100`);
    } else if (pick < 0.9) {
        res = http.get(`${BASE}/distance?fromLat=${lat}&fromLon=${lon}&toLat=48.137&toLon=11.575`);
    } else {
        res = http.get(`${BASE}/geohash/encode?lat=${lat}&lon=${lon}`);
    }
    check(res, { 'status 200': (r) => r.status === 200 });
}
```

`loadtest/README.md`: how to run — `dotnet run -c Release --project src/HighPerf.Api` in one terminal, `k6 run loadtest/mixed.js` in another; `BASE_URL` env var to point elsewhere; note that thresholds are initial guesses to be calibrated against the first real run.

- [ ] **Step 2: Validate**

If `k6` is installed (`k6 version` succeeds): start the API (`dotnet run -c Release --project src/HighPerf.Api` in background), run `k6 run --vus 2 --duration 5s loadtest/mixed.js`, expect 0 failed checks, then stop the API. If k6 is NOT installed: run `node --check loadtest/mixed.js` for a syntax check (or skip if node is also absent) and note in the commit message that scripts are unvalidated.

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "feat: k6 load-test scripts (per-endpoint + mixed traffic)"
```

---

### Task 18: Documentation

**Files:**
- Create: `docs/index.md`, `docs/architecture.md`, `docs/performance-techniques.md`, `docs/api.md`, `docs/benchmarks.md`, `README.md`

- [ ] **Step 1: Write the docs** — required content per file (write full prose, cross-reference all files from `docs/index.md` and back):

- `README.md`: one-paragraph project pitch, quickstart (`pwsh tools/prepare-dataset.ps1` only if regenerating data; `dotnet run -c Release --project src/HighPerf.Api`; example curl for each endpoint), link to `docs/`.
- `docs/index.md`: purpose (learning/reference showcase), links to the other four docs + spec + plan.
- `docs/architecture.md`: solution layout; startup flow (embedded gz → span parse → cell-order permutation → CSR grid); request flow for `/cities/nearest` end to end. Two Mermaid diagrams: component diagram (Api → Geo, OutputCache in front) and a sequence diagram for a nearest query (validate → cache check → grid ranges → SIMD scan → top-k → Utf8JsonWriter).
- `docs/performance-techniques.md` — the core deliverable. One section per technique, each with What / Why / Where (file:member) / Measured (numbers from `benchmarks/RESULTS.md` or the k6 run):
  1. Struct-of-arrays + cell-order permutation (cache locality, no pointer chasing)
  2. Unit-vector chord distance (zero per-point trig; monotonic mapping)
  3. `Vector<float>` SIMD scans with scalar tails
  4. CSR grid index → contiguous candidate ranges
  5. `stackalloc` top-k heap + `ArrayPool` hit buffers (zero-allocation queries)
  6. Span-based query parsing (no StringValues materialization on hot paths)
  7. Source-generated JSON + pooled `Utf8JsonWriter` → `PipeWriter`
  8. Output caching with quantized keys (the hit-rate insight; X-Compute-Count observability)
  9. Host tuning: CreateSlimBuilder, ServerGC, InvariantGlobalization, no Server header, TieredPGO
  10. What we deliberately did NOT do: Redis L2 (network hop > recompute), Native AOT (loses dynamic PGO), FusionCache sub-result layer (benchmark gate not met — revisit if profiles show repeated sub-computation)
- `docs/api.md`: all six endpoints — parameters, defaults, limits, example request/response JSON, error shape (`application/problem+json`), caching semantics (10-min TTL, ~110 m coordinate buckets).
- `docs/benchmarks.md`: how to run BDN suite and k6 scripts, how to read MemoryDiagnoser output, latest snapshot copied from `benchmarks/RESULTS.md`.

- [ ] **Step 2: Verify docs match reality** — every file path, member name, endpoint, and limit mentioned in docs exists in code (grep each); Mermaid blocks render (fence syntax valid).

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "docs: architecture, performance techniques catalog, API reference, benchmarks"
```

---

### Task 19: Final verification sweep

- [ ] **Step 1: Full clean run** — `dotnet build -c Release` then `dotnet test -c Release`: everything green, zero warnings.
- [ ] **Step 2: Manual smoke** — `dotnet run -c Release --project src/HighPerf.Api`, curl all six endpoints (valid + one invalid each), confirm responses and `X-Compute-Count` repeat behavior; stop the server.
- [ ] **Step 3: Final review** — dispatch `noobit:stack-reviewer` on `git diff $(git rev-list --max-parents=0 HEAD)..HEAD` (whole project); fix BLOCKER/MAJOR findings; then dispatch `noobit:test-guardian` to catch untested changed behavior.
- [ ] **Step 4: Commit any fixes**

```bash
git add -A && git commit -m "chore: review fixes from final verification sweep"
```

---

## Plan Self-Review Notes (already applied)

- Spec coverage: dataset/loader (T2, T4), SoA + grid (T5), SIMD kernels (T6–T7), radius/k-NN (T8–T9), geohash (T10, T14), endpoints + validation + ProblemDetails + exception handler (T11–T14), output cache + quantization + observability (T15), benchmarks incl. zero-alloc proof (T16), k6 (T17), docs (T18). FusionCache L1 sub-result layer: intentionally NOT implemented — the spec gates it behind a demonstrated benchmark win; documented in T18 item 10.
- Spec deviations, both documented in T15/T18: (a) `X-Cache` hit/miss header → `X-Compute-Count` replay semantics (OutputCache replays stored headers; a literal HIT/MISS header can't be set on the replay path without a custom IOutputCachePolicy — not worth it); (b) SinLat/CosLat arrays → full unit-vector X/Y/Z arrays (strictly better, see Global Constraints).
- Type consistency: `GeoHit(int Index, float DistanceKm)`, `HitBuffer`, `TopK`, `DataRange`, `QueryParams.TryGet*`, `Problems.Validation`, `AppJsonContext` used with identical signatures across tasks — verified.
- Known risk flags for the executor: exact package versions unpinned (use latest stable; xunit.v3 API surface for `TestContext.Current.CancellationToken` requires xunit.v3 ≥ 1.0); `CreateSlimBuilder` + WebApplicationFactory note in T11 Step 4; Munich geohash test constant note in T10.
