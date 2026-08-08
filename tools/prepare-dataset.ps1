$ErrorActionPreference = 'Stop'
$tmp = Join-Path $env:TEMP 'geonames'
New-Item -ItemType Directory -Force $tmp | Out-Null
$zip = Join-Path $tmp 'cities1000.zip'
if (-not (Test-Path $zip)) {
    Invoke-WebRequest 'https://download.geonames.org/export/dump/cities1000.zip' -OutFile $zip
}
Expand-Archive $zip -DestinationPath $tmp -Force
$inFile = Join-Path $tmp 'cities1000.txt'
$outDir = Join-Path $PSScriptRoot '..\src\Geoblitz.Geo\Resources'
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
