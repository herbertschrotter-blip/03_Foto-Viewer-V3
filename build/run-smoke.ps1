#Requires -Version 7
<#
.SYNOPSIS
    Integrations-Rauchtest (SmokeTestRunner) gegen eine beliebige PhotoViewerV3.exe — Debug-Build oder portables Paket.

.DESCRIPTION
    Kopiert die Testbibliothek in einen frischen Arbeitsordner (der Rauchtest verschiebt/löscht dort Dateien), startet
    die EXE mit eigenem AppData-Root (PHOTOVIEWERV3_APPDATA, frische DB) und den Rauchtest-Variablen, wartet auf das
    Ende (Timeout → Kill) und gibt smoke-result.txt sowie einen Log-Auszug aus. Keine Maus-/Tastatureingaben.
    ExitCode des Skripts: 0 = alle Schritte OK, 1 = Fehler/Timeout.

.PARAMETER Exe
    Pfad zur PhotoViewerV3.exe, z. B. artifacts\PhotoViewerV3-0.2.26-win-x64\PhotoViewerV3.exe oder
    src\PhotoViewerV3.Wpf\bin\Debug\net10.0-windows\PhotoViewerV3.exe.

.PARAMETER TestLib
    Ordner der Testbibliothek (wird nur gelesen; mindestens 6 Dateien).

.PARAMETER WorkDir
    Arbeitsordner für Bibliothekskopie, AppData und Screenshots; Standard: %TEMP%\PhotoViewerV3-smoke-<Tag>.

.PARAMETER NoShots
    Keine PNG-Screenshots der Hauptansichten (PHOTOVIEWERV3_SMOKE_SHOTS bleibt leer).

.EXAMPLE
    pwsh -File build\run-smoke.ps1 -Exe artifacts\PhotoViewerV3-0.2.26-win-x64\PhotoViewerV3.exe -TestLib D:\testlib
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$TestLib,
    [string]$Tag = 'run',
    [string]$WorkDir = '',
    [int]$TimeoutSec = 600,
    [switch]$NoShots
)
$ErrorActionPreference = 'Stop'

$exePath = (Resolve-Path -LiteralPath $Exe).Path
$testlib = (Resolve-Path -LiteralPath $TestLib).Path
if (-not $WorkDir) { $WorkDir = Join-Path ([IO.Path]::GetTempPath()) "PhotoViewerV3-smoke-$Tag" }
$lib = Join-Path $WorkDir 'lib'
$appdata = Join-Path $WorkDir 'appdata'
$shots = Join-Path $WorkDir 'shots'

if (Get-Process PhotoViewerV3 -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exePath }) {
    throw "PhotoViewerV3 aus $exePath läuft bereits"
}

# Frische Testbibliothek (Verschieben/Löschen verändert die Kopie). Nur die eigenen Unterordner leeren, nie den
# ganzen WorkDir — der Parameter könnte auf einen Ordner mit fremden Dateien zeigen.
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
foreach ($dir in @($lib, $appdata, $shots)) {
    if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
}
Copy-Item -LiteralPath $testlib -Destination $lib -Recurse
$expected = @(Get-ChildItem -LiteralPath $testlib -Recurse -File).Count
$count = @(Get-ChildItem -LiteralPath $lib -Recurse -File -ErrorAction SilentlyContinue).Count
if ($count -lt $expected) {
    # Copy-Item/robocopy kopierten im App-Container gelegentlich 0 Dateien → Datei für Datei über .NET nachkopieren
    foreach ($src in Get-ChildItem -LiteralPath $testlib -Recurse -File) {
        $dst = Join-Path $lib ([IO.Path]::GetRelativePath($testlib, $src.FullName))
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($dst)) | Out-Null
        [IO.File]::Copy($src.FullName, $dst, $true)
    }
    $count = @(Get-ChildItem -LiteralPath $lib -Recurse -File).Count
}
if ($count -lt 6) { throw "Testbibliothek-Kopie unvollständig: $count von $expected Dateien in $lib" }
Write-Output "EXE:            $exePath"
Write-Output "Testbibliothek: $lib ($count Dateien)"

New-Item -ItemType Directory -Force -Path $appdata | Out-Null
if (-not $NoShots) { New-Item -ItemType Directory -Force -Path $shots | Out-Null }

$env:PHOTOVIEWERV3_APPDATA = $appdata
$env:PHOTOVIEWERV3_SMOKE_ROOT = $lib
$env:PHOTOVIEWERV3_SMOKE_SHOTS = if ($NoShots) { '' } else { $shots }
$env:PHOTOVIEWERV3_SMOKE_EXIT = '1'
$env:PHOTOVIEWERV3_SMOKE_CLEANUP = '0'

$stdout = Join-Path $appdata 'stdout.txt'
$stderr = Join-Path $appdata 'stderr.txt'
$sw = [Diagnostics.Stopwatch]::StartNew()
$proc = Start-Process -FilePath $exePath -PassThru -WindowStyle Normal -RedirectStandardOutput $stdout -RedirectStandardError $stderr
Write-Output "Gestartet: PID $($proc.Id) — warte bis zu $TimeoutSec s …"
if (-not $proc.WaitForExit($TimeoutSec * 1000)) {
    Write-Output "TIMEOUT nach $TimeoutSec s — Prozess wird beendet"
    Stop-Process -Id $proc.Id -Force
    $proc.WaitForExit(10000) | Out-Null
    $exit = 'TIMEOUT'
} else {
    $exit = $proc.ExitCode
}
Write-Output "Beendet: ExitCode=$exit nach $([int]$sw.Elapsed.TotalSeconds) s"

$result = Join-Path $appdata 'smoke-result.txt'
Write-Output "`n== smoke-result.txt =="
if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result } else { Write-Output '(keine Ergebnisdatei)' }

$log = Get-ChildItem (Join-Path $appdata 'logs') -Filter 'photoviewer-*.log' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime | Select-Object -Last 1
if ($log) {
    $lines = Get-Content -LiteralPath $log.FullName
    Write-Output "`n== Log: $($log.FullName) ($($lines.Count) Zeilen) =="
    Write-Output '-- ERR/FTL/WRN --'
    $lines | Where-Object { $_ -match '\b(ERR|FTL|WRN)\]' } | Select-Object -First 40
    Write-Output '-- Rauchtest ✘ / FAIL --'
    $lines | Where-Object { $_ -match 'Rauchtest ✘|FAIL ' } | Select-Object -First 40
}
if (-not $NoShots) {
    Write-Output "`n== Screenshots =="
    Get-ChildItem -LiteralPath $shots -Filter *.png -ErrorAction SilentlyContinue | ForEach-Object { '{0,8} {1}' -f $_.Length, $_.Name }
}
Write-Output "`n== Prozesse =="
$left = Get-Process PhotoViewerV3 -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exePath }
if ($left) { Write-Output "HÄNGEND: $($left.Id)" } else { Write-Output 'kein hängender Prozess' }

exit $(if ($exit -eq 0) { 0 } else { 1 })
