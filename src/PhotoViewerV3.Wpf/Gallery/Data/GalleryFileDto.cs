namespace PhotoViewerV3.Gallery.Data;

public enum GalleryFilter
{
    All,
    Images,
    Videos,
    Favorites,

    /// <summary>Nur Archive (ZIP/RAR/7z …, MediaType 2, ADR-0022).</summary>
    Archives,

    /// <summary>Dateien unter einer Eingangs-Wurzel (Folders.RootId → LibraryRoots.Role = Inbox, ADR-0020).</summary>
    NotArchived,

    /// <summary>Dateien unter der Archiv-Wurzel.</summary>
    Archived,
}

/// <summary>Sortierung der Galerie; die Zweitsortierung nach Id ist immer stabil (siehe GalleryPageSource).</summary>
public enum GallerySort
{
    ModifiedDesc,
    ModifiedAsc,
    NameAsc,
    NameDesc,
    SizeDesc,
    SizeAsc,
}

/// <summary>
/// Flaches Zeilen-DTO einer Grid-Page (ADR-0010: kleine DTOs, keine Entities im UI).
/// <paramref name="RootRole"/> = Rolle der Wurzel des Ordners (0 Eingang, 1 Archiv, null unbekannt — ADR-0020).
/// </summary>
public sealed record GalleryFileDto(
    int Id,
    int FolderId,
    string Name,
    string Extension,
    long Size,
    long ModifiedAtUnix,
    int MediaType,
    bool IsFavorite,
    byte? Rating,
    bool Missing,
    string FolderPath,
    int? RootRole = null)
{
    public bool IsVideo => MediaType == 1;
    public bool IsArchive => MediaType == 2;
    public string SizeDisplay => FormatSize(Size);

    /// <summary>true, wenn die Datei unter einer Eingangs-Wurzel liegt (Badge „Eingang“).</summary>
    public bool IsInbox => RootRole == 0;

    public static string FormatSize(long size) => size switch
    {
        < 1024 => $"{size} B",
        < 1024 * 1024 => $"{size / 1024.0:N1} KB",
        < 1024L * 1024 * 1024 => $"{size / 1024.0 / 1024.0:N1} MB",
        _ => $"{size / 1024.0 / 1024.0 / 1024.0:N2} GB",
    };
}

/// <summary>Unterordner-Kachel für den Container-State (PLAN Kap. 16); FileCount zählt rekursiv.</summary>
public sealed record GallerySubfolderDto(int Id, string Name, string Path, int FileCount)
{
    public string FileCountDisplay => FileCount == 1 ? "1 Datei" : $"{FileCount:N0} Dateien";
}

/// <summary>Stammdaten eines Ordners (für den Filter-Chip „Ordner: …“).</summary>
public sealed record GalleryFolderInfo(int Id, string Name, string Path, int? ParentId);

/// <summary>Benutzer-Metadaten einer Datei zum In-place-Update nach FilesChangedMessage(Metadata).</summary>
public sealed record GalleryMetadataDto(int Id, bool IsFavorite, byte? Rating, bool Missing);
