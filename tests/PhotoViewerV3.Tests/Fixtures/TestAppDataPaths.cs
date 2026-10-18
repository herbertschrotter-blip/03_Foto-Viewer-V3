using PhotoViewerV3.Services.Infrastructure;

namespace PhotoViewerV3.Tests.Fixtures;

/// <summary>
/// <see cref="IAppDataPaths"/> auf ein temporäres Verzeichnis (DB, Thumbnails, Logs) —
/// Tests fassen %LocalAppData% nie an.
/// </summary>
public sealed class TestAppDataPaths : IAppDataPaths
{
    public TestAppDataPaths(string root)
    {
        AppDataRoot = root;
        LibraryDatabasePath = Path.Combine(root, "library.db");
        ThumbnailsRoot = Path.Combine(root, "thumbs");
        LogsRoot = Path.Combine(root, "logs");
    }

    public string AppDataRoot { get; }
    public string LibraryDatabasePath { get; }
    public string ThumbnailsRoot { get; }
    public string LogsRoot { get; }

    public string GetLibraryThumbnailsPath(long libraryId) => Path.Combine(ThumbnailsRoot, libraryId.ToString());

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(AppDataRoot);
        Directory.CreateDirectory(ThumbnailsRoot);
        Directory.CreateDirectory(LogsRoot);
    }
}
