using System;
using System.IO;
using PhotoViewerV3.Services.Infrastructure;

namespace PhotoViewerV3.Shell;

/// <summary>
/// Optionaler AppData-Root per Umgebungsvariable PHOTOVIEWERV3_APPDATA (z. B. für Rauchtests mit frischer DB
/// ohne die Katalog-DB des Benutzers anzufassen). Ohne Variable gilt die produktive <see cref="AppDataPaths"/>.
/// </summary>
public static class AppDataRootOverride
{
    public const string EnvironmentVariable = "PHOTOVIEWERV3_APPDATA";

    public static IAppDataPaths Resolve()
    {
        var root = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return string.IsNullOrWhiteSpace(root)
            ? new AppDataPaths()
            : new RelocatedAppDataPaths(Path.GetFullPath(root));
    }

    private sealed class RelocatedAppDataPaths : IAppDataPaths
    {
        public RelocatedAppDataPaths(string root)
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
}
