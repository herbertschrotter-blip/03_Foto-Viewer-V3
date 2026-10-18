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
/// Phase eines laufenden Scans (für Statusleiste und Scan-Panel).
/// </summary>
public enum ScanPhase
{
    /// <summary>Filesystem wird durchsucht (CurrentPath = aktueller Ordner).</summary>
    Enumerating = 0,

    /// <summary>ClearAndRescan: alte Katalogdaten werden entfernt.</summary>
    Clearing = 1,

    /// <summary>Folders/Files werden in die DB geschrieben (FilesDiscovered = bereits geschriebene Zeilen).</summary>
    Persisting = 2,

    /// <summary>FTS5-Suchindex wird befüllt.</summary>
    Indexing = 3,
}

/// <summary>
/// Progress-Event für UI-Feedback während eines Scans.
/// </summary>
public sealed record ScanProgress(
    int FoldersDiscovered,
    int FilesDiscovered,
    long TotalBytesDiscovered,
    string? CurrentPath)
{
    /// <summary>Aktuelle Phase; Default Enumerating, damit bestehende Aufrufer unverändert bleiben.</summary>
    public ScanPhase Phase { get; init; } = ScanPhase.Enumerating;
}

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
    /// <summary>Erst-Scan: erwartet leere Folders/Files in der DB für diese Library, sonst Fehler.</summary>
    Fresh = 0,

    /// <summary>
    /// Bestehende Scan-Daten der Library (Files, Folders, Thumbnails, FTS) löschen und danach wie Fresh scannen.
    /// FileIds werden dabei neu vergeben.
    /// </summary>
    ClearAndRescan = 1,

    /// <summary>
    /// Diff-Scan: neue Dateien anlegen, nicht mehr vorhandene als Missing markieren, geänderte
    /// (Size/ModifiedAt) aktualisieren. FileIds bleiben stabil. Mit Offline-vs-Missing-Guard (PLAN Kap. 12).
    /// </summary>
    Incremental = 2,
}
