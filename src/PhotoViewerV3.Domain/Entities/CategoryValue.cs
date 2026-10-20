namespace PhotoViewerV3.Domain.Entities;

/// <summary>
/// Ein wählbarer Wert einer <see cref="Category"/> (Tabelle "CategoryValues"). Der Name ist je Kategorie eindeutig
/// (ohne Groß-/Kleinschreibung); löscht man die Kategorie, verschwinden ihre Werte (ON DELETE CASCADE).
/// </summary>
public sealed class CategoryValue
{
    public int Id { get; set; }
    public int CategoryId { get; set; }
    public required string Name { get; set; }
    public int SortOrder { get; set; }
}
