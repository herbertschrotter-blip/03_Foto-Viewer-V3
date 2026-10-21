namespace Lumora.Services.Infrastructure;

/// <summary>
/// Zentrale Verwaltung aller App-Datenpfade (DB, Thumbnails, Logs) gemäß ADR-0003, ADR-0009 und ADR-0026.
/// </summary>
public interface IAppDataPaths
{
    /// <summary>Wurzelverzeichnis aller App-Daten unter %LocalAppData%\Lumora (Logs, Speicherort-Datei).</summary>
    string AppDataRoot { get; }

    /// <summary>Pfad zur zentralen Library-Datenbank (SQLite) — im Datenordner (Standard: <see cref="AppDataRoot"/>).</summary>
    string LibraryDatabasePath { get; }

    /// <summary>Wurzelverzeichnis aller Thumbnail-Caches neben der DB, weitere Aufteilung pro LibraryId.</summary>
    string ThumbnailsRoot { get; }

    /// <summary>Wurzelverzeichnis für rollierende Log-Dateien (bleibt immer unter <see cref="AppDataRoot"/>).</summary>
    string LogsRoot { get; }

    /// <summary>Gibt den Thumbnail-Cache-Ordner für eine konkrete Library zurück.</summary>
    string GetLibraryThumbnailsPath(long libraryId);

    /// <summary>Stellt sicher, dass alle Basisverzeichnisse existieren.</summary>
    void EnsureDirectoriesExist();
}

/// <summary>
/// Pfade aus App-Wurzel und Datenordner: Logs und <see cref="DatabaseLocationStore.FileName"/> liegen immer in der
/// App-Wurzel, <c>library.db</c> und <c>thumbs\</c> im Datenordner — Standard die App-Wurzel selbst, sonst der in den
/// Einstellungen gewählte Ordner (ADR-0026).
/// </summary>
public sealed class AppDataPaths : IAppDataPaths
{
    public const string AppFolderName = "Lumora";
    public const string DatabaseFileName = "library.db";
    public const string ThumbnailsFolderName = "thumbs";

    public AppDataPaths()
        : this(DefaultAppDataRoot)
    {
    }

    /// <param name="appDataRoot">App-Wurzel (Logs, Speicherort-Datei).</param>
    /// <param name="dataFolder">Ordner für DB und Thumbnails; null = App-Wurzel.</param>
    public AppDataPaths(string appDataRoot, string? dataFolder = null)
    {
        AppDataRoot = appDataRoot;
        DataFolder = string.IsNullOrWhiteSpace(dataFolder) ? appDataRoot : dataFolder;
        LibraryDatabasePath = Path.Combine(DataFolder, DatabaseFileName);
        ThumbnailsRoot = Path.Combine(DataFolder, ThumbnailsFolderName);
        LogsRoot = Path.Combine(AppDataRoot, "logs");
    }

    /// <summary>%LocalAppData%\Lumora.</summary>
    public static string DefaultAppDataRoot
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    public string AppDataRoot { get; }

    /// <summary>Ordner mit <c>library.db</c> und <c>thumbs\</c>.</summary>
    public string DataFolder { get; }

    /// <summary>true, wenn DB und Thumbnails nicht in der App-Wurzel liegen (in den Einstellungen umgezogen).</summary>
    public bool UsesCustomDataFolder => !PathsEqual(DataFolder, AppDataRoot);

    public string LibraryDatabasePath { get; }
    public string ThumbnailsRoot { get; }
    public string LogsRoot { get; }

    /// <summary>
    /// Liest den gewählten Datenordner aus <see cref="DatabaseLocationStore"/> in der App-Wurzel; ohne Datei gilt die
    /// App-Wurzel. Eine beschädigte Datei wirft (<see cref="InvalidDataException"/>) — still auf den Standardort
    /// zurückzufallen, legte dort einen leeren Katalog an.
    /// </summary>
    public static AppDataPaths Resolve(string appDataRoot)
    {
        var location = DatabaseLocationStore.Load(appDataRoot);
        return new AppDataPaths(appDataRoot, location.DatabaseFolder);
    }

    public string GetLibraryThumbnailsPath(long libraryId)
        => Path.Combine(ThumbnailsRoot, libraryId.ToString());

    /// <summary>
    /// Legt App-Wurzel und Logs an; Thumbnail-Ordner nur, wenn der Datenordner existiert — ein gewählter Ordner auf
    /// einem nicht verbundenen Laufwerk wird nicht neu angelegt (der Start meldet die fehlende DB).
    /// </summary>
    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(AppDataRoot);
        Directory.CreateDirectory(LogsRoot);
        if (!UsesCustomDataFolder || Directory.Exists(DataFolder))
        {
            Directory.CreateDirectory(ThumbnailsRoot);
        }
    }

    /// <summary>Vergleicht zwei Ordnerpfade (voll aufgelöst, ohne abschließenden Trenner, Groß/Klein egal).</summary>
    public static bool PathsEqual(string a, string b)
        => string.Equals(NormalizeFolder(a), NormalizeFolder(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>true, wenn <paramref name="path"/> gleich <paramref name="folder"/> ist oder darunter liegt.</summary>
    public static bool IsSameOrInside(string path, string folder)
    {
        var p = NormalizeFolder(path);
        var f = NormalizeFolder(folder);
        var prefix = f.EndsWith(Path.DirectorySeparatorChar) ? f : f + Path.DirectorySeparatorChar; // Laufwerkswurzel „D:\“
        return string.Equals(p, f, StringComparison.OrdinalIgnoreCase)
               || p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeFolder(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        return full.Length > (root?.Length ?? 0) ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }
}
