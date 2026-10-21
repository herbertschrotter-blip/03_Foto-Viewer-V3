using Lumora.Domain.Enums;

namespace Lumora.Domain.Entities;

/// <summary>
/// Story (ADR-0058, Tabelle "Stories"): automatisch erkannte bzw. eigene Zusammenstellung von Fotos einer
/// Bibliotheks-Gruppe (<see cref="GroupId"/> ohne Fremdschlüssel wie Alben, ADR-0048/0054). <see cref="Signature"/> ist der
/// stabile Schlüssel der Erkennung (Art + Zeitraum/Person/Ort, je Gruppe eindeutig): eine Neuberechnung findet darüber —
/// und bei verschobenen Rändern über die gemeinsamen Dateien — dieselbe Story wieder und lässt den Benutzer-Zustand
/// (angeheftet, ausgeblendet, eigener Titel, Titelbild, manuelle Zu-/Abgänge) unberührt. Überschriften werden nicht
/// gespeichert, sondern beim Lesen sachlich aus Datum, Orten und Personen gebaut; nur <see cref="CustomTitle"/> ist Text
/// des Benutzers.
/// </summary>
public sealed class Story
{
    public int Id { get; set; }

    /// <summary>Bibliotheks-Gruppe (ADR-0048); 0 = ohne Bibliothek angelegt, die nächste Bibliothek übernimmt sie.</summary>
    public int GroupId { get; set; }

    public StoryKind Kind { get; set; }

    /// <summary>Stabiler Schlüssel der Erkennung, z. B. „E:1720771200“, „T:20281-20288“, „P:12:2019-2019“, „O:3175172“.</summary>
    public required string Signature { get; set; }

    /// <summary>Aufnahme der ersten Datei (UTC, Unix-Sekunden wie <c>Files.CapturedAt</c>).</summary>
    public DateTime StartAt { get; set; }

    /// <summary>Aufnahme der letzten Datei (UTC).</summary>
    public DateTime EndAt { get; set; }

    /// <summary>Erster Kalendertag (Ortszeit der Aufnahme) als Tageszahl seit 1970-01-01 — Grundlage der Überschrift.</summary>
    public int FirstDay { get; set; }

    /// <summary>Letzter Kalendertag (Ortszeit) als Tageszahl seit 1970-01-01.</summary>
    public int LastDay { get; set; }

    /// <summary>Genauigkeit der Daten: Monat/Jahr = Story aus ungefähren Aufnahmedaten (Überschrift nur „Juli 2025“ bzw. „2025“).</summary>
    public CaptureDatePrecision DatePrecision { get; set; }

    /// <summary>Ort einer Orte-Story (GeoNames-Id, ADR-0050), sonst null.</summary>
    public int? PlaceId { get; set; }

    /// <summary>Person einer Personen-Story (Fremdschlüssel, ON DELETE CASCADE), sonst null.</summary>
    public int? PersonId { get; set; }

    /// <summary>Orte der Story in Reihenfolge des ersten Auftretens als „id,id,…“ (GeoNames-Ids), null ohne Orte.</summary>
    public string? PlaceIds { get; set; }

    /// <summary>Zahl der Besuche (Orte-Story) bzw. null.</summary>
    public int? VisitCount { get; set; }

    /// <summary>Ereignis bzw. Reise weg von zu Hause (ein Tag = „Ausflug“).</summary>
    public bool IsAway { get; set; }

    /// <summary>Eigener Titel des Benutzers; null = sachliche Überschrift.</summary>
    public string? CustomTitle { get; set; }

    /// <summary>Titelbild (Fremdschlüssel auf Files, ON DELETE SET NULL); null = noch keins gewählt.</summary>
    public int? CoverFileId { get; set; }

    /// <summary>Titelbild vom Benutzer gesetzt (die Erkennung ersetzt es nie).</summary>
    public bool CoverIsManual { get; set; }

    public bool IsPinned { get; set; }

    public DateTime? PinnedAtUtc { get; set; }

    public bool IsHidden { get; set; }

    public bool IsSeen { get; set; }

    public DateTime? SeenAtUtc { get; set; }

    /// <summary>Fotos und Videos der Story (automatisch + hinzugefügt, ohne Ausschlüsse) über die ganze Gruppe.</summary>
    public int FileCount { get; set; }

    public int VideoCount { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
