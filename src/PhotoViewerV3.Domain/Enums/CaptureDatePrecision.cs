namespace PhotoViewerV3.Domain.Enums;

/// <summary>
/// Genauigkeit des Aufnahmedatums (<c>Files.CapturedAtPrecision</c>, ADR-0046). Ungefähre Angaben werden für die
/// Sortierung in die Mitte des Zeitraums gelegt: Tag → 12:00 Uhr, Monat → 15. um 12:00, Jahr → 1. Juli um 12:00.
/// </summary>
public enum CaptureDatePrecision
{
    /// <summary>Datum und Uhrzeit (Sekunde).</summary>
    Exact = 0,

    /// <summary>Nur der Tag bekannt (z. B. WhatsApp-Dateiname IMG-20240712-WA0001).</summary>
    Day = 1,

    /// <summary>Monat und Jahr („ca. Juli 1982“).</summary>
    Month = 2,

    /// <summary>Nur das Jahr („ca. 1982“).</summary>
    Year = 3,
}
