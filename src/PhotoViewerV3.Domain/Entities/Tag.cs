namespace PhotoViewerV3.Domain.Entities;

/// <summary>
/// Frei konfigurierbares Schlagwort (ADR-0021). Tags sind global (nicht je Library) und werden über
/// <see cref="FileTag"/> Dateien zugewiesen. <see cref="Name"/> ist eindeutig (ohne Groß-/Kleinschreibung),
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
}
