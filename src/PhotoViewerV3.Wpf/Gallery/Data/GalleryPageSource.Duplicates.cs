using System.Collections.Generic;
using PhotoViewerV3.Services.Duplicates;

namespace PhotoViewerV3.Gallery.Data;

/// <summary>
/// Galerie-Filter „Doppelte“ (ADR-0053, Chip in der Zeile „Inhalt“): nur Dateien, zu denen es im Bereich eine byte-gleiche Datei gibt
/// (gleiche gültige SHA-256-Prüfsumme). <paramref name="LibraryIds"/> = Bibliotheken, in denen das Gegenstück liegen darf
/// — der Bereich der Galerie (eine Bibliothek bzw. „Alle verknüpften“), beim Bauen der Abfrage frisch gesetzt.
/// </summary>
public sealed record GalleryDuplicateFilter(IReadOnlyList<int> LibraryIds);

/// <summary>SQL des Filters „Doppelte“ — UND-Bedingung des Datei-Teils, über <c>IX_FileFingerprints_ContentHash</c>.</summary>
public sealed partial class GalleryPageSource
{
    internal static IEnumerable<string> BuildDuplicateClauses(GalleryFilterState filter)
    {
        if (filter.Duplicates is { } duplicates)
        {
            yield return "AND " + DuplicateSql.HasExactDuplicate("f", duplicates.LibraryIds);
        }
    }
}
