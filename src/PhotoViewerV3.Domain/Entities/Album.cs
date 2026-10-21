using PhotoViewerV3.Domain.Enums;

namespace PhotoViewerV3.Domain.Entities;

/// <summary>
/// Album (ADR-0054, Tabelle "Albums"): eine Verknüpfungs-Schicht über den Ordnern — ein Foto liegt physisch nur einmal
/// (FileId stabil) und kann in beliebig vielen Alben erscheinen. Alben gehören wie Tags einer Bibliotheks-Gruppe
/// (<see cref="GroupId"/>, ADR-0048); <see cref="Name"/> ist je Gruppe eindeutig (ohne Groß-/Kleinschreibung).
/// Eigene Alben (<see cref="AlbumKind.Manual"/>) halten ihre Fotos in <see cref="AlbumItem"/>, intelligente
/// (<see cref="AlbumKind.Smart"/>) einen gespeicherten Galerie-Filter in <see cref="FilterJson"/> (Begriffe über ihren
/// Namen, damit Verknüpfen/Lösen und „Neu aufbauen“ sie nicht ungültig machen).
/// </summary>
public sealed class Album
{
    public int Id { get; set; }

    /// <summary>Bibliotheks-Gruppe (ADR-0048); 0 = ohne Bibliothek angelegt, die nächste Bibliothek übernimmt es.</summary>
    public int GroupId { get; set; }

    public required string Name { get; set; }

    public AlbumKind Kind { get; set; }

    /// <summary>Gespeicherter Filter eines intelligenten Albums (JSON, <c>SmartAlbumFilter</c>); null bei eigenen Alben.</summary>
    public string? FilterJson { get; set; }

    /// <summary>Gewähltes Titelbild (Fremdschlüssel auf Files, ON DELETE SET NULL); null = neuestes Foto des Albums.</summary>
    public int? CoverFileId { get; set; }

    public int SortOrder { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
