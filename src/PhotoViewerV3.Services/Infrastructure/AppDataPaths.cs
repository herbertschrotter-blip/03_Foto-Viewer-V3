namespace PhotoViewerV3.Services.Infrastructure;

/// <summary>
/// Zentrale Verwaltung aller App-Datenpfade (DB, Thumbnails, Logs) gemäß ADR-0003 und ADR-0009.
/// </summary>
public interface IAppDataPaths
{
    /// <summary>Wurzelverzeichnis aller App-Daten unter %LocalAppData%\PhotoViewerV3.</summary>
    string AppDataRoot { get; }

    /// <summary>Pfad zur zentralen Library-Datenbank (SQLite).</summary>
    string LibraryDatabasePath { get; }

    /// <summary>Wurzelverzeichnis aller Thumbnail-Caches, weitere Aufteilung pro LibraryId.</summary>
    string ThumbnailsRoot { get; }

    /// <summary>Wurzelverzeichnis für rollierende Log-Dateien.</summary>
    string LogsRoot { get; }

    /// <summary>Gibt den Thumbnail-Cache-Ordner für eine konkrete Library zurück.</summary>
    string GetLibraryThumbnailsPath(long libraryId);

    /// <summary>Stellt sicher, dass alle Basisverzeichnisse existieren.</summary>
    void EnsureDirectoriesExist();
}

public sealed class AppDataPaths : IAppDataPaths
{
    private const string AppFolderName = "PhotoViewerV3";

    public AppDataPaths()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        AppDataRoot = Path.Combine(localAppData, AppFolderName);
        LibraryDatabasePath = Path.Combine(AppDataRoot, "library.db");
        ThumbnailsRoot = Path.Combine(AppDataRoot, "thumbs");
        LogsRoot = Path.Combine(AppDataRoot, "logs");
    }

    public string AppDataRoot { get; }
    public string LibraryDatabasePath { get; }
    public string ThumbnailsRoot { get; }
    public string LogsRoot { get; }

    public string GetLibraryThumbnailsPath(long libraryId)
        => Path.Combine(ThumbnailsRoot, libraryId.ToString());

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(AppDataRoot);
        Directory.CreateDirectory(ThumbnailsRoot);
        Directory.CreateDirectory(LogsRoot);
    }
}
