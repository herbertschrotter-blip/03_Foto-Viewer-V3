using PhotoViewerV3.Domain.Enums;

namespace PhotoViewerV3.Services.Scanning;

/// <summary>
/// Ergebnis eines Filesystem-Walks (vor DB-Persistenz). Path ist absoluter Pfad.
/// </summary>
public sealed record DiscoveredFile(
    string FullPath,
    string FolderPath,
    string Name,
    string Extension,
    long Size,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    MediaType MediaType);

/// <summary>
/// Aggregierte Folder-Information aus dem Discover-Pass.
/// </summary>
public sealed record DiscoveredFolder(
    string Path,
    string? ParentPath,
    string Name);

/// <summary>
/// Progress-Event für UI-Feedback während eines Scans.
/// </summary>
public sealed record ScanProgress(
    int FoldersDiscovered,
    int FilesDiscovered,
    long TotalBytesDiscovered,
    string? CurrentPath);

/// <summary>
/// Ergebnis eines abgeschlossenen Scans, inkl. Zähler und Timing.
/// </summary>
public sealed record ScanResult(
    int LibraryId,
    string RootPath,
    int FoldersInserted,
    int FilesInserted,
    int FtsRowsInserted,
    long TotalBytes,
    TimeSpan TotalDuration,
    TimeSpan EnumerationDuration,
    TimeSpan PersistenceDuration,
    TimeSpan FtsDuration);

/// <summary>
/// Modus für ScanService.ScanAsync.
/// </summary>
public enum ScanMode
{
    /// <summary>
    /// Erst-Scan: erwartet leere Folders/Files in der DB für diese Library.
    /// Wenn nicht leer, Fehler oder optional Clear.
    /// </summary>
    Fresh = 0,
    // Incremental kommt ab V0.2.
}
