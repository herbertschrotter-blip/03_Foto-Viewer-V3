#Requires -Version 7
<#
.SYNOPSIS
    Erzeugt src\PhotoViewerV3.Wpf\Shell\Branding\Lumora.ico neu aus der Vektor-Quelle LumoraIcon.xaml (ADR-0047).

.DESCRIPTION
    Rendert das App-Symbol in 16, 20, 24, 32, 40, 48, 64, 96, 128 und 256 px (unter 32 px die vereinfachte Variante)
    mit WPF-Bordmitteln (RenderTargetBitmap) und schreibt den ICO-Container selbst (LumoraIconRenderer). Läuft als
    Test „LumoraIconFileTests“ mit LUMORA_WRITE_ICON=1; ohne die Variable prüft derselbe Test nur, dass die
    eingecheckte Datei den gerenderten Rahmen entspricht. Danach die Datei einchecken und neu bauen
    (ApplicationIcon/EmbeddedResource im csproj).

.EXAMPLE
    pwsh -File build\make-icon.ps1
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$tests = Join-Path $repo 'tests\PhotoViewerV3.Wpf.Tests'

$env:LUMORA_WRITE_ICON = '1'
try {
    & dotnet test $tests --nologo -v q --filter 'FullyQualifiedName~LumoraIconFileTests'
    if ($LASTEXITCODE -ne 0) { throw "Erzeugen fehlgeschlagen (ExitCode $LASTEXITCODE)." }
}
finally {
    Remove-Item Env:\LUMORA_WRITE_ICON -ErrorAction SilentlyContinue
}

$ico = Join-Path $repo 'src\PhotoViewerV3.Wpf\Shell\Branding\Lumora.ico'
Write-Host "Fertig: $ico ($((Get-Item -LiteralPath $ico).Length) Bytes)"
