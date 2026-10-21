using PhotoViewerV3.Domain.Enums;

namespace PhotoViewerV3.Services.Metadata;

/// <summary>Aufnahmedatum einer Datei, wie der Katalog es kennt; <see cref="Source"/> null = noch nicht gelesen.</summary>
public sealed record CaptureDateInfo(int FileId, CaptureDateValue? Value, CaptureDateSource? Source)
{
    /// <summary>Anzeigetext („12.07.2024 15:30 (EXIF)“, „unbekannt (sortiert nach Änderungsdatum)“).</summary>
    public string Text => CaptureDateText.Describe(Value, Source);

    /// <summary>Aus den Katalogspalten (Unix-Sekunden UTC, Herkunft, Genauigkeit, Abstand in Minuten).</summary>
    public static CaptureDateInfo FromColumns(int fileId, long? capturedAtUnix, int? source, int precision, int? offset)
    {
        var src = source is { } s ? (CaptureDateSource?)s : null;
        if (capturedAtUnix is not { } unix || src is null)
        {
            return new CaptureDateInfo(fileId, null, src);
        }

        var utc = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
        var offsetMinutes = offset ?? (int)Math.Round(TimeZoneInfo.Local.GetUtcOffset(utc).TotalMinutes);
        return new CaptureDateInfo(fileId, new CaptureDateValue(utc, offsetMinutes, src.Value, (CaptureDatePrecision)precision), src);
    }
}

/// <summary>Änderung durch „Datum setzen…“ (ADR-0046); schreibt nur den Katalog, nie die Datei.</summary>
public abstract record CaptureDateEdit
{
    private CaptureDateEdit()
    {
    }

    /// <summary>Genaues Datum mit Uhrzeit (Wanduhrzeit, Zeitzone dieses PCs).</summary>
    public sealed record Exact(DateTime LocalWallClock) : CaptureDateEdit;

    /// <summary>Ungefähr: nur Jahr (<paramref name="Month"/> null) oder Monat und Jahr.</summary>
    public sealed record Approximate(int Year, int? Month) : CaptureDateEdit;

    /// <summary>Alle um <paramref name="Delta"/> verschieben (falsch gestellte Kamera); Basis ist das bisherige Datum bzw. das Änderungsdatum.</summary>
    public sealed record Shift(TimeSpan Delta) : CaptureDateEdit;

    /// <summary>Gesetztes Datum verwerfen — das Nachtragen liest es wieder aus der Datei.</summary>
    public sealed record Reset : CaptureDateEdit;

    /// <summary>Kurzbeschreibung für Protokoll und Statuszeile.</summary>
    public string Describe() => this switch
    {
        Exact e => $"Aufnahmedatum {e.LocalWallClock:dd.MM.yyyy HH:mm}",
        Approximate { Month: null } a => $"Aufnahmedatum ca. {a.Year}",
        Approximate a => $"Aufnahmedatum ca. {a.Month:00}/{a.Year}",
        Shift s => $"Aufnahmedatum verschoben um {FormatDelta(s.Delta)}",
        _ => "Aufnahmedatum zurückgesetzt (aus der Datei lesen)",
    };

    public static string FormatDelta(TimeSpan delta)
    {
        var sign = delta < TimeSpan.Zero ? "−" : "+";
        var d = delta.Duration();
        var parts = new List<string>(3);
        if (d.Days > 0)
        {
            parts.Add(d.Days == 1 ? "1 Tag" : $"{d.Days} Tage");
        }

        if (d.Hours > 0)
        {
            parts.Add(d.Hours == 1 ? "1 Stunde" : $"{d.Hours} Stunden");
        }

        if (d.Minutes > 0)
        {
            parts.Add(d.Minutes == 1 ? "1 Minute" : $"{d.Minutes} Minuten");
        }

        return sign + (parts.Count == 0 ? "0" : string.Join(" ", parts));
    }
}

/// <summary>Ergebnis von „Datum setzen…“ bzw. „Rückgängig“; <see cref="BatchId"/> 0 = nichts geändert (kein Protokolleintrag).</summary>
public sealed record CaptureDateChangeResult(int BatchId, int Changed, int Unchanged, string Description);

/// <summary>Zustand des Nachtragens im Hintergrund (Statusleiste).</summary>
public sealed record CaptureDateBackfillStatus(bool IsRunning, int Done, int Total, int Skipped = 0, string? Message = null)
{
    public static CaptureDateBackfillStatus Idle { get; } = new(false, 0, 0);
}

/// <summary>Ergebnis eines Durchgangs des Nachtragens.</summary>
/// <param name="Read">Dateien gelesen (mit oder ohne Datum).</param>
/// <param name="WithDate">davon mit Datum (EXIF, Video, Dateiname).</param>
/// <param name="Skipped">übersprungen: fehlt oder nur online (OneDrive-Platzhalter, kein Download).</param>
public sealed record CaptureDateBackfillReport(int Read, int WithDate, int Skipped);
