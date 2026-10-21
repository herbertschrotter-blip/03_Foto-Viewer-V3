namespace PhotoViewerV3.Domain.Entities;

/// <summary>
/// Frei konfigurierbare Kategorie (ADR-0027), z. B. „Projekt“ mit den Werten „Nord“ und „Süd“. Kategorien gehören seit
/// 0.2.51 einer Bibliotheks-Gruppe (<see cref="GroupId"/>, ADR-0048, Werte über ihre Kategorie); jede Datei hat je Kategorie höchstens einen <see cref="CategoryValue"/> (<see cref="FileCategoryValue"/>).
/// <see cref="Name"/> ist eindeutig (ohne Groß-/Kleinschreibung), <see cref="SortOrder"/> die Reihenfolge in Filter,
/// Spalten und Einstellungen. Mapped auf Tabelle "Categories".
/// </summary>
public sealed class Category
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Bibliotheks-Gruppe (ADR-0048); Name ist je Gruppe eindeutig.</summary>
    public int GroupId { get; set; }
}
