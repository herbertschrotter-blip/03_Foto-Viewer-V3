using System.IO;

namespace Lumora.Services.Scanning;

/// <summary>
/// Erreichbarkeitsprüfung des Bibliotheks-Roots mit Timeout (PLAN Kap. 12, Spike #06): lokal 1 s, UNC 3 s.
/// <c>Directory.Exists</c> auf einem nicht erreichbaren UNC-Pfad respektiert kein CancellationToken und hängt
/// bis zum SMB-Timeout (~30 s) — deshalb läuft die Prüfung auf einem Pool-Thread und <c>WaitAsync</c> begrenzt
/// die Wartezeit; ein Timeout zählt als „nicht erreichbar“. Gemeinsame Mechanik für ScanService (Vorbedingung
/// und Recheck vor jedem Persist-Batch) und LibraryService (<c>LibraryInfo.IsRootAvailable</c>).
/// </summary>
public static class RootAvailabilityProbe
{
    public static readonly TimeSpan LocalTimeout = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan UncTimeout = TimeSpan.FromSeconds(3);

    public static bool IsUncPath(string path) => path.StartsWith(@"\\", StringComparison.Ordinal);

    public static TimeSpan GetTimeout(string rootPath) => IsUncPath(rootPath) ? UncTimeout : LocalTimeout;

    /// <summary>true, wenn der Ordner innerhalb des Timeouts als vorhanden gemeldet wird; Timeout = false.</summary>
    public static async Task<bool> IsAvailableAsync(string rootPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        try
        {
            return await Task.Run(() => Directory.Exists(rootPath), ct)
                .WaitAsync(GetTimeout(rootPath), ct)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
