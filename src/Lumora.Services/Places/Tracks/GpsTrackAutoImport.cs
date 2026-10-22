using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Services.Messaging;

namespace Lumora.Services.Places.Tracks;

/// <summary>Neue Aktivitäten laufend ergänzen (ADR-0062): beobachteter Ordner und Garmin-Uhr als USB-Laufwerk.</summary>
public interface IGpsTrackAutoImport
{
    /// <summary>Einstellungen lesen, Ordner beobachten, USB-Laufwerke prüfen (nach dem Start der Sitzung).</summary>
    void Start();

    /// <summary>Nach geänderten Einstellungen: Ordner bzw. USB-Erkennung neu einrichten.</summary>
    Task ReconfigureAsync(CancellationToken ct = default);

    /// <summary>FIT-Dateien der Uhr, die neuer als das letzte Einlesen sind.</summary>
    IReadOnlyList<string> FindNewOnDevice(string activityFolder, long? sinceUnix);

    /// <summary>Neue Aktivitäten der Uhr einlesen (nach der Rückfrage) und den Merker setzen.</summary>
    Task<GpsTrackImportReport> ImportDeviceAsync(string activityFolder, CancellationToken ct = default);

    /// <summary>Beobachteten Ordner jetzt prüfen (neue Dateien seit dem letzten Mal).</summary>
    Task<GpsTrackImportReport?> ScanWatchFolderAsync(CancellationToken ct = default);
}

/// <summary>
/// Beobachteter Ordner (FileSystemWatcher, entprellt) und Garmin-Uhr per USB (ADR-0062): nur Uhren, die sich als
/// Laufwerk mit <c>GARMIN\Activity</c> melden (USB-Massenspeicher). Neuere Uhren (z. B. Fenix 6/7) melden sich per MTP
/// und erscheinen nicht als Laufwerk — die werden bewusst nicht unterstützt (Oberfläche erklärt den Weg über den
/// Datenexport bzw. Garmin Express). Vor dem Einlesen von der Uhr wird gefragt (<see cref="GarminDeviceDetectedMessage"/>).
/// Gelesen wird nur; geschrieben wird nichts auf Uhr oder Ordner.
/// </summary>
public sealed class GpsTrackAutoImport : IGpsTrackAutoImport, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(5);

    private readonly IGpsTrackImportService _import;
    private readonly IGeoTrackSettingsStore _settings;
    private readonly ILogger<GpsTrackAutoImport> _logger;
    private readonly IMessenger _messenger;
    private readonly Func<IEnumerable<string>> _driveRoots;
    private readonly HashSet<string> _seenRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private FileSystemWatcher? _watcher;
    private Timer? _pollTimer;
    private Timer? _debounceTimer;
    private string? _watchFolder;
    private bool _usb;
    private int _disposed;

    /// <param name="driveRoots">Optional (Tests); produktiv die Wurzeln der Wechsellaufwerke.</param>
    public GpsTrackAutoImport(
        IGpsTrackImportService import,
        IGeoTrackSettingsStore settings,
        ILogger<GpsTrackAutoImport> logger,
        IMessenger? messenger = null,
        Func<IEnumerable<string>>? driveRoots = null)
    {
        _import = import;
        _settings = settings;
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _driveRoots = driveRoots ?? RemovableDriveRoots;
    }

    public void Start() => _ = Task.Run(async () =>
    {
        try
        {
            await ReconfigureAsync().ConfigureAwait(false);
            await ScanWatchFolderAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GPS-Spuren: automatisches Einlesen nicht gestartet");
        }
    });

    public async Task ReconfigureAsync(CancellationToken ct = default)
    {
        var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);
        lock (_sync)
        {
            if (_disposed != 0)
            {
                return;
            }

            _watcher?.Dispose();
            _watcher = null;
            _watchFolder = settings.WatchFolder;
            if (_watchFolder is { } folder && Directory.Exists(folder))
            {
                try
                {
                    var watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
                    watcher.Created += (_, e) => OnFolderEvent(e.FullPath);
                    watcher.Changed += (_, e) => OnFolderEvent(e.FullPath);
                    watcher.Renamed += (_, e) => OnFolderEvent(e.FullPath);
                    watcher.EnableRaisingEvents = true;
                    _watcher = watcher;
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
                {
                    _logger.LogWarning("GPS-Spuren: Ordner kann nicht beobachtet werden ({Type})", ex.GetType().Name);
                }
            }

            _usb = settings.UsbDetection;
            if (_usb)
            {
                _pollTimer ??= new Timer(_ => Poll(), null, TimeSpan.FromSeconds(3), PollInterval);
            }
            else
            {
                _pollTimer?.Dispose();
                _pollTimer = null;
                _seenRoots.Clear();
            }
        }
    }

    private void OnFolderEvent(string path)
    {
        if (!GpsTrackImportService.IsSupported(path))
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed != 0)
            {
                return;
            }

            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => _ = ScanWatchFolderSafeAsync(), null, Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task ScanWatchFolderSafeAsync()
    {
        try
        {
            await ScanWatchFolderAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GPS-Spuren: beobachteter Ordner nicht eingelesen");
        }
    }

    public async Task<GpsTrackImportReport?> ScanWatchFolderAsync(CancellationToken ct = default)
    {
        var folder = _watchFolder;
        if (folder is null || !Directory.Exists(folder))
        {
            return null;
        }

        var since = await _settings.GetMarkerAsync(GeoTrackSettings.WatchLastScanKey, ct).ConfigureAwait(false);
        var (files, newest) = NewerFiles(folder, since, recursive: true, fitOnly: false);
        if (files.Count == 0)
        {
            return null;
        }

        var report = await _import.ImportAsync(files, ct).ConfigureAwait(false);
        if (report.Error is null && !report.Cancelled)
        {
            await _settings.SetMarkerAsync(GeoTrackSettings.WatchLastScanKey, newest, ct).ConfigureAwait(false);
        }

        return report;
    }

    public IReadOnlyList<string> FindNewOnDevice(string activityFolder, long? sinceUnix)
        => NewerFiles(activityFolder, sinceUnix, recursive: false, fitOnly: true).Files;

    public async Task<GpsTrackImportReport> ImportDeviceAsync(string activityFolder, CancellationToken ct = default)
    {
        var since = await _settings.GetMarkerAsync(GeoTrackSettings.UsbLastImportKey, ct).ConfigureAwait(false);
        var (files, newest) = NewerFiles(activityFolder, since, recursive: false, fitOnly: true);
        if (files.Count == 0)
        {
            return new GpsTrackImportReport(0, 0, 0, 0, 0, false);
        }

        var report = await _import.ImportAsync(files, ct).ConfigureAwait(false);
        if (report.Error is null && !report.Cancelled)
        {
            await _settings.SetMarkerAsync(GeoTrackSettings.UsbLastImportKey, newest, ct).ConfigureAwait(false);
        }

        return report;
    }

    /// <summary>
    /// Dateien, die seit dem Merker geändert ODER angelegt wurden (eine kopierte ältere Aktivität behält ihr Änderungsdatum,
    /// bekommt aber ein neues Erstelldatum); liefert als neuen Merker den Zeitpunkt vor dem Durchsuchen.
    /// </summary>
    private static (IReadOnlyList<string> Files, long Newest) NewerFiles(string folder, long? sinceUnix, bool recursive, bool fitOnly)
    {
        var list = new List<string>();
        var newest = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true };
            foreach (var file in Directory.EnumerateFiles(folder, "*", options))
            {
                var ext = Path.GetExtension(file);
                if (fitOnly ? !ext.Equals(".fit", StringComparison.OrdinalIgnoreCase) : !GpsTrackImportService.IsSupported(file) || ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var info = new FileInfo(file);
                var stamp = new DateTimeOffset(info.LastWriteTimeUtc > info.CreationTimeUtc ? info.LastWriteTimeUtc : info.CreationTimeUtc).ToUnixTimeSeconds();
                if (sinceUnix is { } since && stamp <= since)
                {
                    continue;
                }

                list.Add(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Laufwerk abgezogen bzw. Ordner weg: beim nächsten Mal
        }

        return (list, newest);
    }

    /// <summary>Ordner <c>GARMIN\Activity</c> auf einem Laufwerk oder null.</summary>
    public static string? ActivityFolderOn(string driveRoot)
    {
        var folder = Path.Combine(driveRoot, "GARMIN", "Activity");
        return Directory.Exists(folder) ? folder : null;
    }

    private void Poll()
    {
        if (!_usb || _disposed != 0)
        {
            return;
        }

        try
        {
            var roots = _driveRoots().ToList();
            lock (_sync)
            {
                _seenRoots.IntersectWith(roots); // abgezogene vergessen → beim nächsten Anstecken wieder fragen
            }

            foreach (var root in roots)
            {
                lock (_sync)
                {
                    if (!_seenRoots.Add(root))
                    {
                        continue;
                    }
                }

                if (ActivityFolderOn(root) is not { } folder)
                {
                    continue;
                }

                var since = _settings.GetMarkerAsync(GeoTrackSettings.UsbLastImportKey).GetAwaiter().GetResult();
                var count = FindNewOnDevice(folder, since).Count;
                _logger.LogInformation("GPS-Spuren: Garmin-Uhr als Laufwerk erkannt, {Count} neue Aktivitäten", count);
                if (count > 0)
                {
                    _messenger.Send(new GarminDeviceDetectedMessage(folder, count));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GPS-Spuren: Laufwerke nicht geprüft");
        }
    }

    private static IEnumerable<string> RemovableDriveRoots()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            bool ready;
            try
            {
                ready = drive.DriveType == DriveType.Removable && drive.IsReady;
            }
            catch (IOException)
            {
                ready = false;
            }

            if (ready)
            {
                yield return drive.RootDirectory.FullName;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_sync)
        {
            _watcher?.Dispose();
            _pollTimer?.Dispose();
            _debounceTimer?.Dispose();
        }
    }
}
