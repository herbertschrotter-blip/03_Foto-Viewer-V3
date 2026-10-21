using System;
using Lumora.Services.Scanning;

namespace Lumora.Shell;

/// <summary>Gemeinsame Anzeigeformate (Bytes, Dauer, Scan-Phase) für Statusleiste und Seiten.</summary>
public static class Formatting
{
    public static string Bytes(long bytes)
    {
        const double kb = 1024;
        const double mb = kb * 1024;
        const double gb = mb * 1024;
        const double tb = gb * 1024;

        return bytes switch
        {
            >= (long)tb => $"{bytes / tb:N2} TB",
            >= (long)gb => $"{bytes / gb:N2} GB",
            >= (long)mb => $"{bytes / mb:N1} MB",
            >= (long)kb => $"{bytes / kb:N0} KB",
            _ => $"{bytes:N0} B",
        };
    }

    public static string Duration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00} h";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{duration.Minutes}:{duration.Seconds:00} min";
        }

        return $"{duration.TotalSeconds:N1} s";
    }

    public static string ScanPhase(ScanPhase phase) => phase switch
    {
        Services.Scanning.ScanPhase.Clearing => "alte Katalogdaten werden entfernt…",
        Services.Scanning.ScanPhase.Persisting => "Katalog wird geschrieben…",
        Services.Scanning.ScanPhase.Indexing => "Suchindex wird erstellt…",
        _ => "Ordner werden durchsucht…",
    };

    /// <summary>Hinweis, solange ein Scan oder eine Datei-Operation auf die Sperre der Bibliothek wartet (0.2.22).</summary>
    public static string GateWait(ScanGateUse holder) => holder == ScanGateUse.Scan
        ? "Wartet auf laufenden Scan…"
        : "Wartet auf laufende Datei-Operation…";
}
