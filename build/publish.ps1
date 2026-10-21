#Requires -Version 7
<#
.SYNOPSIS
    Baut die portable Version von Lumora (vormals PhotoViewer V3; Ordner + ZIP, win-x64) per dotnet publish.

.DESCRIPTION
    Standard: self-contained (kein installiertes .NET nötig), kein Single-File, kein Trimming (WPF verträgt es nicht).
    Version = oberster Eintrag „## [x.y.z]“ in CHANGELOG.md (dieselbe Quelle liest PhotoViewerV3.Wpf.csproj); das
    Skript bricht ab, wenn die veröffentlichte EXE eine andere Produktversion trägt.

    Ergebnis (Standard-Ausgabe artifacts\ in der Repo-Wurzel, per .gitignore ausgenommen):
      artifacts\Lumora-<Version>-win-x64\        Ordner mit Lumora.exe
      artifacts\Lumora-<Version>-win-x64.zip     derselbe Ordner gepackt

    Nicht im Paket: FFmpeg (Video-Vorschaubilder) — wird wie im Debug-Build im PATH bzw. unter den winget-Links gesucht
    („winget install Gyan.FFmpeg“). Daten liegen unabhängig vom Paketordner unter %LocalAppData%\PhotoViewerV3
    (bzw. im Ordner aus database-location.json) — der Ordnername bleibt trotz des Produktnamens Lumora (ADR-0047).

.PARAMETER Runtime
    Runtime Identifier (nur win-x64 ist geprüft).

.PARAMETER OutputRoot
    Zielordner für Paketordner und ZIP; Standard: <Repo>\artifacts.

.PARAMETER FrameworkDependent
    Ohne mitgelieferte .NET-Runtime (kleiner, braucht aber die installierte „.NET 10 Desktop Runtime“ x64).

.PARAMETER NoZip
    Nur den Ordner erzeugen.

.EXAMPLE
    pwsh -File build\publish.ps1
#>
[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',
    [string]$OutputRoot = '',
    [switch]$FrameworkDependent,
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\PhotoViewerV3.Wpf\PhotoViewerV3.Wpf.csproj'
$changelog = Join-Path $repo 'CHANGELOG.md'
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'artifacts' }

# --- Version aus dem obersten CHANGELOG-Eintrag -------------------------------------------------------------------
$match = Select-String -LiteralPath $changelog -Pattern '^## \[(\d+\.\d+\.\d+)\]' | Select-Object -First 1
# In Zeichenketten keine typografischen Anführungszeichen: PowerShell liest „ und “ als Stringgrenze.
if (-not $match) { throw "Kein Eintrag '## [x.y.z]' in $changelog gefunden." }
$version = $match.Matches[0].Groups[1].Value

$flavor = if ($FrameworkDependent) { '-fdd' } else { '' }
$packageName = "Lumora-$version-$Runtime$flavor"
$outDir = Join-Path $OutputRoot $packageName
$zipPath = "$outDir.zip"

Write-Host "Lumora $version → $outDir ($(if ($FrameworkDependent) { 'framework-dependent' } else { 'self-contained' }))"
if (Test-Path -LiteralPath $outDir) { Remove-Item -LiteralPath $outDir -Recurse -Force }
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

# --- dotnet publish -----------------------------------------------------------------------------------------------
# VlcWindowsX86Enabled=false: das LibVLC-Paket kopiert bei AnyCPU sonst auch libvlc\win-x86 (~90 MB) mit.
# SatelliteResourceLanguages=de: nur deutsche Ressourcen der Runtime/WPF/WPF-UI statt aller Sprachen.
$publishArgs = @(
    'publish', $project,
    '-c', 'Release',
    '-r', $Runtime,
    '--self-contained', $(if ($FrameworkDependent) { 'false' } else { 'true' }),
    '-o', $outDir,
    "-p:Version=$version",
    '-p:PublishSingleFile=false',
    '-p:PublishTrimmed=false',
    '-p:PublishReadyToRun=false',
    '-p:VlcWindowsX86Enabled=false',
    '-p:SatelliteResourceLanguages=de',
    '--nologo'
)
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish fehlgeschlagen (ExitCode $LASTEXITCODE)." }

# --- Prüfen -------------------------------------------------------------------------------------------------------
$exe = Join-Path $outDir 'Lumora.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "EXE fehlt im Paket: $exe" }
$productName = (Get-Item -LiteralPath $exe).VersionInfo.ProductName
if ($productName -ne 'Lumora') { throw "Produktname der EXE ($productName) ist nicht 'Lumora'." }

$productVersion = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
if ($productVersion -ne $version) {
    throw "Produktversion der EXE ($productVersion) weicht vom CHANGELOG ($version) ab."
}

$required = @('e_sqlite3.dll', 'libvlc\win-x64\libvlc.dll', 'libvlc\win-x64\plugins')
if (-not $FrameworkDependent) { $required += @('coreclr.dll', 'PresentationFramework.dll') }
foreach ($item in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $outDir $item))) { throw "Pflichtbestandteil fehlt im Paket: $item" }
}
if (Test-Path -LiteralPath (Join-Path $outDir 'libvlc\win-x86')) { throw 'libvlc\win-x86 sollte nicht im x64-Paket liegen.' }
# Werkzeuge für 'dotnet ef' gehören nicht ins Paket (Data-Projekt: EF-Design-Paket ohne contentfiles, 0.2.31).
$designOnly = @(Get-ChildItem -LiteralPath $outDir -Directory -Filter 'BuildHost-*') +
              @(Get-ChildItem -LiteralPath $outDir -File -Filter 'Microsoft.EntityFrameworkCore.Design.dll')
if ($designOnly.Count -gt 0) { throw "Design-Zeit-Bestandteile im Paket: $($designOnly.Name -join ', ')" }

$files = Get-ChildItem -LiteralPath $outDir -Recurse -File
$folderMb = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "Ordner: $($files.Count) Dateien, $folderMb MB — Produktversion $productVersion OK"

# --- ZIP ----------------------------------------------------------------------------------------------------------
if (-not $NoZip) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $outDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    $zipMb = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 1)
    Write-Host "ZIP:    $zipPath ($zipMb MB)"
}

Write-Host "Fertig: $exe"
