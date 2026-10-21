#Requires -Version 7
<#
.SYNOPSIS
    Verdichtet die GeoNames-Rohdaten zu der eingebetteten Ortsnamen-Ressource von Lumora (ADR-0050).

.DESCRIPTION
    Eingaben (Download von https://download.geonames.org/export/dump/, Lizenz CC BY 4.0):
      cities5000.zip (oder entpackt cities5000.txt), admin1CodesASCII.txt, countryInfo.txt
    dazu die kuratierten deutschen Namen in diesem Ordner: countries-de.tsv, admin1-de.tsv, cities-de.tsv.

    Ausgabe: UTF-8-TSV, gzip-komprimiert (Standard src/Lumora.Services/Places/Data/geonames.tsv.gz):
      # Kopfzeile (Quelle, Lizenz, Anzahl)
      C <Land> <Name deutsch> <Name englisch>
      R <Land.Admin1> <Name> <GeoNames-Name>
      (ohne Stadtteile PPLX und historische Orte PPLH/PPLQ/PPLW)
      P <GeoNames-Id> <Anzeigename> <weitere Namen, mit | getrennt> <Breite> <Länge> <Land> <Admin1> <Einwohner>
    sortiert nach Code bzw. Id, Zeilenende \n. Koordinaten mit 5 Nachkommastellen (≈ 1 m). Der Anzeigename ist der
    deutsche Name aus cities-de.tsv, sonst der GeoNames-Name; GeoNames- und ASCII-Name bleiben für die Suche erhalten.

    Reproduzierbar: gleiche Eingaben → gleicher TSV-Inhalt (das Skript gibt dessen SHA-256 aus; ADR-0050 nennt den Wert
    der eingecheckten Ressource). Die gzip-Bytes hängen zusätzlich von der .NET-Version ab.

.EXAMPLE
    pwsh -File build/geonames/Build-GeoNames.ps1 -SourceDir C:\Temp\geonames
#>
param(
    [Parameter(Mandatory = $true)][string]$SourceDir,
    [string]$OutFile
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$here = $PSScriptRoot
$repo = Resolve-Path (Join-Path $here '..\..')
if (-not $OutFile) { $OutFile = Join-Path $repo 'src\Lumora.Services\Places\Data\geonames.tsv.gz' }
$inv = [System.Globalization.CultureInfo]::InvariantCulture
$utf8 = [System.Text.UTF8Encoding]::new($false)

function Read-Tsv([string]$path) {
    foreach ($line in [System.IO.File]::ReadLines($path, $utf8)) {
        if ($line.Length -eq 0 -or $line.StartsWith('#')) { continue }
        , ($line -split "`t")
    }
}

function Clean([string]$text) { return ($text -replace "[`t`r`n|]", ' ').Trim() }

# ---- Kuratierte deutsche Namen ----------------------------------------------------------------
$countryDe = @{}
foreach ($c in Read-Tsv (Join-Path $here 'countries-de.tsv')) { $countryDe[$c[0]] = $c[1] }
$admin1De = @{}
foreach ($c in Read-Tsv (Join-Path $here 'admin1-de.tsv')) { $admin1De[$c[0]] = $c[1] }
$cityDe = [ordered]@{}
foreach ($c in Read-Tsv (Join-Path $here 'cities-de.tsv')) { $cityDe["$($c[0])`t$($c[1])"] = $c[2] }

# ---- Länder -----------------------------------------------------------------------------------
$countries = [System.Collections.Generic.SortedDictionary[string, string[]]]::new([System.StringComparer]::Ordinal)
foreach ($c in Read-Tsv (Join-Path $SourceDir 'countryInfo.txt')) {
    $code = $c[0]; $english = Clean $c[4]
    $german = if ($countryDe.ContainsKey($code)) { $countryDe[$code] } else { $english }
    $countries[$code] = @($german, $english)
}

# ---- Regionen (admin1) ------------------------------------------------------------------------
$regions = [System.Collections.Generic.SortedDictionary[string, string[]]]::new([System.StringComparer]::Ordinal)
foreach ($c in Read-Tsv (Join-Path $SourceDir 'admin1CodesASCII.txt')) {
    $key = $c[0]; $name = Clean $c[1]
    $display = if ($admin1De.ContainsKey($key)) { $admin1De[$key] } else { $name }
    $regions[$key] = @($display, $name)
}

# ---- Orte --------------------------------------------------------------------------------------
$citiesTxt = Join-Path $SourceDir 'cities5000.txt'
$tempDir = $null
if (-not (Test-Path $citiesTxt)) {
    $zip = Join-Path $SourceDir 'cities5000.zip'
    if (-not (Test-Path $zip)) { throw "Weder cities5000.txt noch cities5000.zip in $SourceDir" }
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("lumora-geonames-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tempDir | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $tempDir)
    $citiesTxt = Join-Path $tempDir 'cities5000.txt'
}

try {
    $rows = [System.Collections.Generic.List[object]]::new()
    $best = @{} # Schlüssel aus cities-de.tsv → einwohnerstärkster Ort dieses Namens im Land
    $skipped = 0
    foreach ($c in Read-Tsv $citiesTxt) {
        # Stadtteile (PPLX) und historische/verlassene/zerstörte Orte weglassen: „Wien“ statt „Innere Stadt“.
        if ($c[7] -in 'PPLX', 'PPLH', 'PPLQ', 'PPLW') { $skipped++; continue }
        $row = [pscustomobject]@{
            Id = [int]$c[0]; Name = Clean $c[1]; Ascii = Clean $c[2]
            Lat = [double]::Parse($c[4], $inv); Lon = [double]::Parse($c[5], $inv)
            Country = $c[8]; Admin1 = $c[10]; Population = [long]$c[14]
        }
        $rows.Add($row)
        $key = "$($row.Country)`t$($row.Name)"
        if ($cityDe.Contains($key) -and (-not $best.ContainsKey($key) -or $best[$key].Population -lt $row.Population)) { $best[$key] = $row }
    }

    # Deutsche Exonyme
    $exonym = @{}
    foreach ($key in $cityDe.Keys) {
        if (-not $best.ContainsKey($key)) { Write-Warning ('cities-de.tsv: kein Ort ''{0}''' -f ($key -replace "`t", ' / ')); continue }
        $exonym[$best[$key].Id] = $cityDe[$key]
    }

    $sb = [System.Text.StringBuilder]::new(8MB)
    [void]$sb.Append("# Lumora GeoNames v1 · Quelle: GeoNames (geonames.org), CC BY 4.0 · cities5000 · $($rows.Count) Orte`n")
    foreach ($kv in $countries.GetEnumerator()) { [void]$sb.Append("C`t$($kv.Key)`t$($kv.Value[0])`t$($kv.Value[1])`n") }
    foreach ($kv in $regions.GetEnumerator()) { [void]$sb.Append("R`t$($kv.Key)`t$($kv.Value[0])`t$($kv.Value[1])`n") }
    foreach ($r in ($rows | Sort-Object Id)) {
        $display = if ($exonym.ContainsKey($r.Id)) { $exonym[$r.Id] } else { $r.Name }
        $alt = @($r.Name, $r.Ascii) | Where-Object { $_ -and $_ -ne $display } | Select-Object -Unique
        $lat = $r.Lat.ToString('0.#####', $inv); $lon = $r.Lon.ToString('0.#####', $inv)
        [void]$sb.Append("P`t$($r.Id)`t$display`t$($alt -join '|')`t$lat`t$lon`t$($r.Country)`t$($r.Admin1)`t$($r.Population)`n")
    }

    $bytes = $utf8.GetBytes($sb.ToString())
    $sha = [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    New-Item -ItemType Directory -Force -Path (Split-Path $OutFile) | Out-Null
    $out = [System.IO.File]::Create($OutFile)
    try {
        $gz = [System.IO.Compression.GZipStream]::new($out, [System.IO.Compression.CompressionLevel]::SmallestSize)
        $gz.Write($bytes, 0, $bytes.Length)
        $gz.Dispose()
    }
    finally { $out.Dispose() }

    $size = (Get-Item $OutFile).Length
    Write-Host ("Orte: {0:N0} (ohne {4:N0} Stadtteile/historische) · Länder: {1} · Regionen: {2} · deutsche Exonyme: {3}" -f $rows.Count, $countries.Count, $regions.Count, $exonym.Count, $skipped)
    Write-Host ("TSV: {0:N0} Bytes, SHA-256 {1}" -f $bytes.Length, $sha)
    Write-Host ("Ressource: {0} ({1:N0} Bytes)" -f $OutFile, $size)
    if ($size -gt 3MB) { throw "Ressource größer als 3 MB ($size Bytes)" }
}
finally {
    if ($tempDir) { Remove-Item -Recurse -Force $tempDir }
}
