using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Lumora.Settings.Info;

/// <summary>
/// Angaben zur App für Einstellungen › Info (ADR-0042): Version (= oberster Eintrag „## [x.y.z]“ in CHANGELOG.md, siehe
/// Lumora.Wpf.csproj), Build-Datum, Debug/Release, Versionen der Bausteine aus den DLLs neben der App.
/// Reine Hilfsfunktionen ohne UI und ohne Netzzugriff.
/// </summary>
public static partial class AppInfo
{
    /// <summary>Sichtbarer Produktname (ADR-0047; bis 0.2.48 „PhotoViewer V3“).</summary>
    public const string AppName = Lumora.Shell.Branding.LumoraBrand.ProductName;

    /// <summary>Name der Änderungsliste neben der EXE (per csproj ins Ausgabe- und Paketverzeichnis kopiert).</summary>
    public const string ChangelogFileName = "CHANGELOG.md";

    public const string Description = "Fotos und Videos ansehen, bewerten, sortieren und in die Bibliothek verschieben.";

    /// <summary>Build-Art dieser Assembly.</summary>
#if DEBUG
    public const string BuildConfiguration = "Debug";
#else
    public const string BuildConfiguration = "Release";
#endif

    private static Assembly AppAssembly => typeof(AppInfo).Assembly;

    /// <summary>Version „x.y.z“ aus der InformationalVersion der App (ohne „+Commit“-Anhang).</summary>
    public static string Version => CleanVersion(
        AppAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? AppAssembly.GetName().Version?.ToString(3))
        ?? "?";

    /// <summary>Zeitpunkt des Builds = Änderungszeit der App-DLL (bzw. der EXE), null wenn nicht ermittelbar.</summary>
    public static DateTime? BuildDate
    {
        get
        {
            var location = AppAssembly.Location;
            if (string.IsNullOrEmpty(location))
            {
                location = Environment.ProcessPath ?? string.Empty;
            }

            return File.Exists(location) ? File.GetLastWriteTime(location) : null;
        }
    }

    /// <summary>Ordner der App (DLLs, CHANGELOG.md).</summary>
    public static string AppDirectory => AppContext.BaseDirectory;

    /// <summary>Oberste Version „## [x.y.z]“ eines CHANGELOG-Texts, null wenn keine.</summary>
    public static string? ReadTopChangelogVersion(string changelogText)
    {
        var match = TopVersionRegex().Match(changelogText);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Pfad der CHANGELOG.md im Ordner, null wenn sie fehlt (dann bleibt „Änderungsprotokoll“ ausgeblendet).</summary>
    public static string? FindChangelog(string directory)
    {
        var path = Path.Combine(directory, ChangelogFileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>„1.24.4+abc123“ → „1.24.4“; leer → null.</summary>
    public static string? CleanVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return (plus >= 0 ? version[..plus] : version).Trim();
    }

    /// <summary>Produktversion einer DLL im Ordner (ohne Commit-Anhang), null wenn die Datei fehlt.</summary>
    public static string? ComponentVersion(string directory, string dllName)
    {
        try
        {
            var path = Path.Combine(directory, dllName);
            if (!File.Exists(path))
            {
                return null;
            }

            var info = FileVersionInfo.GetVersionInfo(path);
            return CleanVersion(info.ProductVersion) ?? CleanVersion(info.FileVersion);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>„Windows 11 (Build 26200)“; ab Build 22000 Windows 11.</summary>
    public static string WindowsText(Version osVersion)
    {
        var name = osVersion.Major >= 10 && osVersion.Build >= 22000 ? "Windows 11"
            : osVersion.Major >= 10 ? "Windows 10"
            : $"Windows {osVersion.Major}.{osVersion.Minor}";
        return $"{name} (Build {osVersion.Build})";
    }

    /// <summary>Kopfzeile der Info-Karte: „Version 0.2.44 · erstellt am 21.10.2026 · Debug-Build“.</summary>
    public static string HeaderLine(string version, DateTime? buildDate, string configuration)
    {
        var date = buildDate is { } d ? $" · erstellt am {d:dd.MM.yyyy}" : string.Empty;
        return $"Version {version}{date} · {configuration}-Build";
    }

    [GeneratedRegex(@"(?m)^## \[(\d+\.\d+\.\d+)\]")]
    private static partial Regex TopVersionRegex();
}
