using PhotoViewerV3.Domain.Enums;

namespace PhotoViewerV3.Domain.Entities;

/// <summary>
/// Wurzelordner einer Bibliothek (ADR-0020): eine Bibliothek hat genau eine Archiv-Wurzel und beliebig viele
/// Eingangs-Wurzeln. <c>Libraries.RootPath</c> bleibt als Anzeige-/Kompatibilitätsfeld und entspricht der
/// Archiv-Wurzel. Ordner (<see cref="Folder.RootId"/>) hängen an genau einer Wurzel.
/// </summary>
public sealed class LibraryRoot
{
    public int Id { get; set; }
    public int LibraryId { get; set; }
    public required string Path { get; set; }
    public LibraryRootRole Role { get; set; }
    public required string Name { get; set; }
    public int SortOrder { get; set; }
}
