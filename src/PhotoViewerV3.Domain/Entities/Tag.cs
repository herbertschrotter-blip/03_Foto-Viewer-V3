namespace PhotoViewerV3.Domain.Entities;

/// <summary>
/// Frei konfigurierbares Schlagwort (ADR-0021). Tags gehören seit 0.2.51 einer Bibliotheks-Gruppe (<see cref="GroupId"/>,
/// ADR-0048) und werden über <see cref="FileTag"/> Dateien zugewiesen. <see cref="Name"/> ist je Gruppe eindeutig (ohne Groß-/Kleinschreibung),
/// <see cref="Color"/> ist ein Hex-Wert aus der Palette („#RRGGBB“), <see cref="SortOrder"/> die
/// Reihenfolge in Menüs und Einstellungen. Mapped auf Tabelle "Tags".
/// </summary>
public sealed class Tag
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Color { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Bibliotheks-Gruppe (ADR-0048); 0 = ohne Bibliothek angelegt, die nächste Bibliothek übernimmt es.</summary>
    public int GroupId { get; set; }
}
