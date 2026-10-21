namespace PhotoViewerV3.Gallery.Data;

// Der frühere Einzel-Filter (enum GalleryFilter) ist seit 0.2.43 in unabhängige Teile zerlegt: GalleryFilterState (ADR-0041).

/// <summary>Sortierung der Galerie; die Zweitsortierung nach Id ist immer stabil (siehe GalleryPageSource).</summary>
public enum GallerySort
{
    ModifiedDesc,
    ModifiedAsc,
    NameAsc,
    NameDesc,
    SizeDesc,
    SizeAsc,

    /// <summary>Aufnahmedatum neu → alt; ohne Aufnahmedatum zählt das Änderungsdatum (ADR-0046).</summary>
    CapturedDesc,
    CapturedAsc,
}

/// <summary>Verknüpfung der gewählten Tags im Tag-Filter (ADR-0021).</summary>
public enum GalleryTagMode
{
    /// <summary>ODER: mindestens eines der gewählten Tags.</summary>
    Any,

    /// <summary>UND: alle gewählten Tags.</summary>
    All,

    /// <summary>Nur Dateien ohne Tag (gewählte Tags werden ignoriert).</summary>
    None,
}

/// <summary>
/// Flaches Zeilen-DTO einer Grid-Page (ADR-0010: kleine DTOs, keine Entities im UI).
/// <paramref name="RootRole"/> = Rolle der Wurzel des Ordners (0 Eingang, 1 Archiv, null unbekannt — ADR-0020).
/// <paramref name="CaptureDate"/> = Aufnahmedatum aus dem Katalog (ADR-0046; null nur bei DTOs ohne Katalogzeile).
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
    int? RootRole = null,
    PhotoViewerV3.Services.Metadata.CaptureDateInfo? CaptureDate = null)
{
    public bool IsVideo => MediaType == 1;
    public bool IsArchive => MediaType == 2;
    public string SizeDisplay => FormatSize(Size);

    /// <summary>true, wenn die Datei unter einer Eingangs-Wurzel liegt (Badge „Eingang“).</summary>
    public bool IsInbox => RootRole == 0;

    /// <summary>Breite des Originals nach EXIF-Drehung (<c>Files.Width</c>, seit 0.2.45); null/0 = unbekannt. Collage (ADR-0044).</summary>
    public int? Width { get; init; }

    /// <summary>Höhe des Originals nach EXIF-Drehung (<c>Files.Height</c>); null/0 = unbekannt.</summary>
    public int? Height { get; init; }

    /// <summary>Bibliothek der Datei (ADR-0048; 0 = unbekannt → die aktive). In „Alle verknüpften“ je Datei verschieden.</summary>
    public int LibraryId { get; init; }

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
