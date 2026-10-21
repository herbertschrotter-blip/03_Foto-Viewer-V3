using PhotoViewerV3.Domain.Enums;

namespace PhotoViewerV3.Services.Albums;

/// <summary>Titelbild eines Albums mit allem, was die Vorschaubild-Pipeline braucht (ADR-0054).</summary>
public sealed record AlbumCover(int FileId, int LibraryId, string Path, MediaType MediaType, DateTime ModifiedAtUtc);

/// <summary>
/// Anzeige-DTO eines Albums (ADR-0054). <paramref name="FileCount"/> zählt nur Dateien des aktuellen Bereichs
/// (ADR-0048); bei intelligenten Alben ohne Auswerter (<see cref="ISmartAlbumEvaluator"/>) null.
/// </summary>
public sealed record AlbumInfo(
    int Id,
    string Name,
    AlbumKind Kind,
    int? FileCount,
    AlbumCover? Cover,
    SmartAlbumFilter? Filter,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc)
{
    public bool IsSmart => Kind == AlbumKind.Smart;

    /// <summary>Zweite Zeile der Kachel: „12 Fotos“ bzw. „Intelligent · 12 Fotos“.</summary>
    public string CountText
    {
        get
        {
            var count = FileCount is { } n ? (n == 1 ? "1 Datei" : $"{n:N0} Dateien") : "…";
            return IsSmart ? $"Intelligent · {count}" : count;
        }
    }
}

/// <summary>Art einer Änderung an den Alben (nicht an Dateien).</summary>
public enum AlbumsChangeKind
{
    Created = 0,
    Renamed = 1,
    Deleted = 2,
    CoverChanged = 3,

    /// <summary>Fotos hinzugefügt oder entfernt.</summary>
    ItemsChanged = 4,

    /// <summary>Filter eines intelligenten Albums neu gespeichert.</summary>
    FilterChanged = 5,
}

/// <summary>
/// Alben haben sich geändert (Liste, Name, Titelbild, Inhalt). Empfänger: Seite „Alben“, Galerie (Filter „Album“).
/// Transport: WeakReferenceMessenger, ggf. von Worker-Threads.
/// </summary>
public sealed record AlbumsChangedMessage(AlbumsChangeKind Kind, int? AlbumId);

/// <summary>
/// Ordner-Verknüpfungen haben sich geändert (ADR-0054): Galerie lädt in der Ordneransicht neu, Ordnerbaum zählt neu.
/// </summary>
public sealed record FolderLinksChangedMessage(int LibraryId, int FolderId, IReadOnlyList<int> FileIds);

/// <summary>Ergebnis von „Hier verknüpfen“.</summary>
/// <param name="Linked">Neu verknüpft.</param>
/// <param name="AlreadyThere">Lagen schon in dem Ordner bzw. waren schon verknüpft.</param>
/// <param name="OtherLibrary">Gehören zu einer anderen Bibliothek als der Ordner (nicht erlaubt).</param>
/// <param name="Unknown">Nicht (mehr) im Katalog.</param>
public sealed record FolderLinkResult(int Linked, int AlreadyThere, int OtherLibrary, int Unknown)
{
    public int Total => Linked + AlreadyThere + OtherLibrary + Unknown;
}

/// <summary>Fortschritt von „Album exportieren…“.</summary>
public sealed record AlbumExportProgress(int Done, int Total, string? CurrentName);

/// <summary>Ergebnis von „Album exportieren…“ (echte Kopien im Zielordner).</summary>
/// <param name="Copied">Kopiert.</param>
/// <param name="Renamed">Davon mit neuem Namen kopiert (gleicher Name im Ziel → „Name (2).jpg“).</param>
/// <param name="Missing">Quelle fehlt (Laufwerk offline, gelöscht).</param>
/// <param name="Failed">Fehler beim Kopieren (Text in <paramref name="Errors"/>).</param>
/// <param name="Canceled">Abgebrochen (bereits kopierte Dateien bleiben).</param>
public sealed record AlbumExportResult(int Copied, int Renamed, int Missing, int Failed, bool Canceled, string TargetFolder, IReadOnlyList<string> Errors);

/// <summary>Das Album gehört nicht zum aktuellen Bereich oder existiert nicht (mehr).</summary>
public sealed class AlbumNotFoundException(int albumId) : InvalidOperationException($"Album {albumId} existiert nicht (mehr).");
