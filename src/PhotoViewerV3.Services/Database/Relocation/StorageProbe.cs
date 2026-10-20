using PhotoViewerV3.Services.Infrastructure;

namespace PhotoViewerV3.Services.Database.Relocation;

/// <summary>Laufwerks-Eigenschaften eines Zielordners für den DB-Umzug (in Tests ersetzbar).</summary>
public interface IStorageProbe
{
    /// <summary>Freier Platz für den aktuellen Benutzer in Bytes; null = unbekannt.</summary>
    long? GetAvailableFreeSpace(string folder);

    /// <summary>UNC-Pfad oder verbundenes Netzlaufwerk — SQLite im WAL-Modus braucht ein lokales Dateisystem.</summary>
    bool IsNetworkLocation(string folder);

    /// <summary>Liegt in einem OneDrive-Ordner (Synchronisierung kann <c>-wal</c>/<c>-shm</c> stören).</summary>
    bool IsCloudSynced(string folder);
}

public sealed class StorageProbe : IStorageProbe
{
    private static readonly string[] CloudFolderVariables = ["OneDrive", "OneDriveConsumer", "OneDriveCommercial"];

    public long? GetAvailableFreeSpace(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            return string.IsNullOrEmpty(root) || IsUnc(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool IsNetworkLocation(string folder)
    {
        var full = Path.GetFullPath(folder);
        if (IsUnc(full))
        {
            return true;
        }

        try
        {
            var root = Path.GetPathRoot(full);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool IsCloudSynced(string folder)
    {
        foreach (var variable in CloudFolderVariables)
        {
            var cloud = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(cloud) && Path.IsPathFullyQualified(cloud) && AppDataPaths.IsSameOrInside(folder, cloud))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnc(string path) => path.StartsWith(@"\\", StringComparison.Ordinal);
}
