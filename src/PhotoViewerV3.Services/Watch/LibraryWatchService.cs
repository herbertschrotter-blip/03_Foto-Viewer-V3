using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Services.Infrastructure;
using PhotoViewerV3.Services.Library;
using PhotoViewerV3.Services.Media;
using PhotoViewerV3.Services.Messaging;
using PhotoViewerV3.Services.Scanning;

namespace PhotoViewerV3.Services.Watch;

/// <summary>
/// Ordnerüberwachung der aktiven Bibliothek (ADR-0019 nennt den FileSystemWatcher als Auslöser für „Aktualisieren“):
/// <list type="bullet">
/// <item>Ein <see cref="FileSystemWatcher"/> je Wurzel (rekursiv, 64 KB Puffer). Nicht erreichbare Wurzeln werden alle
/// <see cref="RootRetryInterval"/> erneut versucht; nach einem Watcher-Fehler (Pufferüberlauf) wird der Watcher neu
/// gestartet und ein Scan eingeplant, weil Ereignisse verloren sein können.</item>
/// <item>Relevant sind nur Medienformate (<see cref="WatchPathFilter"/>). Ereignisse werden gezählt, nicht gespeichert; jedes
/// Ereignis startet die Ruhezeit neu. Erst <see cref="QuietPeriod"/> ohne weiteres Ereignis löst einen Incremental-Scan
/// über <see cref="IScanService"/> aus.</item>
/// <item>Nie parallel zu einem laufenden Scan — weder zum eigenen noch zu einem manuellen (erkannt an
/// <see cref="ScanProgressMessage"/>/<see cref="ScanCompletedMessage"/>/<see cref="ScanAbortedMessage"/> der Bibliothek).
/// Während eines Scans gesammelte Ereignisse werden nach dessen Ende erneut geprüft (neue Ruhezeit).</item>
/// <item>Endet der automatische Scan mit <see cref="ScanOutcome.MassMissingBlocked"/> oder <see cref="ScanOutcome.LibraryOffline"/>,
/// wird nicht automatisch wiederholt (<see cref="WatchState.Blocked"/>, Hinweis in der Statusleiste); ein erfolgreicher
/// manueller Scan, ein Bibliothekswechsel oder das Umschalten der Einstellung hebt die Sperre auf.</item>
/// <item>Wechsel der aktiven Bibliothek per <see cref="ActiveLibraryChangedMessage"/>; Start beim App-Start, Stop beim
/// Beenden (Dispose über den DI-Container).</item>
/// </list>
/// Alle Zustandsänderungen gehen als <see cref="WatchStatusChangedMessage"/> an den Messenger (vom Watcher-, Timer- oder
/// Scan-Thread). Der Zustand wird unter einem Lock geführt; Nachrichten werden außerhalb des Locks gesendet.
/// </summary>
public sealed class LibraryWatchService : ILibraryWatchService
{
    /// <summary>Abstand, in dem eine nicht erreichbare Wurzel erneut versucht wird.</summary>
    public static readonly TimeSpan RootRetryInterval = TimeSpan.FromSeconds(30);

    private const int WatcherBufferSize = 64 * 1024;

    private readonly ILibraryService _libraries;
    private readonly IScanService _scans;
    private readonly IWatchSettingsStore _settingsStore;
    private readonly WatchPathFilter _filter;
    private readonly ILogger<LibraryWatchService> _logger;
    private readonly IMessenger _messenger;
    private readonly TimeSpan _rootRetryInterval;

    private readonly object _gate = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Timer _quietTimer;
    private readonly Timer _retryTimer;

    private bool _started;
    private bool _disposed;
    private bool _enabled = WatchSettings.Default.Enabled;
    private TimeSpan _quietPeriod = WatchSettings.Default.QuietPeriod;
    private int? _libraryId;
    private string[] _roots = [];
    private string[] _unavailableRoots = [];
    private int _pendingEvents;
    private bool _ownScanRunning;
    private bool _externalScanRunning;
    private bool _blocked;
    private string? _blockedReason;
    private DateTime? _dueAtUtc;
    private CancellationTokenSource? _scanCts;
    private WatchStatus _status = WatchStatus.Off;
    private int _attachVersion;

    /// <param name="messenger">Optional (Tests); produktiv WeakReferenceMessenger.Default.</param>
    /// <param name="rootRetryInterval">Optional (Tests); produktiv <see cref="RootRetryInterval"/>.</param>
    public LibraryWatchService(
        ILibraryService libraries,
        IScanService scans,
        IWatchSettingsStore settingsStore,
        IMediaFormatRegistry formats,
        IAppDataPaths paths,
        ILogger<LibraryWatchService> logger,
        IMessenger? messenger = null,
        TimeSpan? rootRetryInterval = null)
    {
        _libraries = libraries;
        _scans = scans;
        _settingsStore = settingsStore;
        _filter = new WatchPathFilter(formats, paths.ThumbnailsRoot);
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _rootRetryInterval = rootRetryInterval ?? RootRetryInterval;
        _quietTimer = new Timer(_ => OnQuietElapsed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _retryTimer = new Timer(_ => OnRetryTick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public bool IsEnabled
    {
        get
        {
            lock (_gate)
            {
                return _enabled;
            }
        }
    }

    public TimeSpan QuietPeriod
    {
        get
        {
            lock (_gate)
            {
                return _quietPeriod;
            }
        }
    }

    public WatchStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>
    /// Zu überwachende Wurzeln einer Bibliothek. Bewusst an einer Stelle gekapselt: sobald Bibliotheken mehrere Wurzeln
    /// (Eingang/Archiv) haben, wird hier die Liste geliefert — der Rest des Dienstes arbeitet bereits mit n Wurzeln.
    /// </summary>
    internal static IReadOnlyList<string> GetRootPaths(LibraryInfo library)
        => string.IsNullOrWhiteSpace(library.RootPath) ? [] : [library.RootPath];

    // ---- Lebenszyklus ------------------------------------------------------------------------------

    public async Task StartAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _started = true;
        }

        var settings = WatchSettings.Default;
        try
        {
            settings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ordnerüberwachung: Einstellungen konnten nicht gelesen werden — Standard (an, {Quiet} s)", WatchSettings.DefaultQuietSeconds);
        }

        lock (_gate)
        {
            _enabled = settings.Enabled;
            _quietPeriod = WatchSettings.ClampQuietPeriod(settings.QuietPeriod);
        }

        _messenger.Register<LibraryWatchService, ActiveLibraryChangedMessage>(this, static (r, m) => r.OnActiveLibraryChanged(m));
        _messenger.Register<LibraryWatchService, ScanProgressMessage>(this, static (r, m) => r.OnExternalScanProgress(m));
        _messenger.Register<LibraryWatchService, ScanCompletedMessage>(this, static (r, m) => r.OnExternalScanFinished(m.LibraryId, m.Result.Outcome == ScanOutcome.Completed));
        _messenger.Register<LibraryWatchService, ScanAbortedMessage>(this, static (r, m) => r.OnExternalScanFinished(m.LibraryId, completedOk: false));

        _logger.LogInformation("Ordnerüberwachung: {State}, Ruhezeit {Quiet} s", settings.Enabled ? "an" : "aus", (int)_quietPeriod.TotalSeconds);
        await AttachToActiveLibraryAsync(ct).ConfigureAwait(false);
    }

    public Task StopAsync()
    {
        WatchStatus status;
        lock (_gate)
        {
            if (!_started)
            {
                return Task.CompletedTask;
            }

            _started = false;
            CancelOwnScanLocked();
            DisposeWatchersLocked();
            _quietTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _retryTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _libraryId = null;
            _roots = [];
            _unavailableRoots = [];
            _pendingEvents = 0;
            _dueAtUtc = null;
            _blocked = false;
            _blockedReason = null;
            _externalScanRunning = false;
            status = SetStatusLocked();
        }

        _messenger.UnregisterAll(this);
        _logger.LogInformation("Ordnerüberwachung beendet");
        Send(status);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        StopAsync().GetAwaiter().GetResult();
        lock (_gate)
        {
            _disposed = true;
        }

        _quietTimer.Dispose();
        _retryTimer.Dispose();
    }

    // ---- Einstellungen -----------------------------------------------------------------------------

    public async Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        WatchStatus status;
        TimeSpan quiet;
        lock (_gate)
        {
            _enabled = enabled;
            quiet = _quietPeriod;
            _blocked = false;
            _blockedReason = null;
            if (!enabled)
            {
                CancelOwnScanLocked();
                _pendingEvents = 0;
                _dueAtUtc = null;
                _quietTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }

            if (_started)
            {
                RebuildWatchersLocked();
            }

            status = SetStatusLocked();
        }

        _logger.LogInformation("Ordnerüberwachung {State}", enabled ? "eingeschaltet" : "ausgeschaltet");
        Send(status);
        await _settingsStore.SaveAsync(new WatchSettings(enabled, quiet), ct).ConfigureAwait(false);
    }

    public async Task SetQuietPeriodAsync(TimeSpan quietPeriod, CancellationToken ct = default)
    {
        var quiet = WatchSettings.ClampQuietPeriod(quietPeriod);
        WatchStatus status;
        bool enabled;
        lock (_gate)
        {
            _quietPeriod = quiet;
            enabled = _enabled;
            if (_pendingEvents > 0 && !_ownScanRunning && !_externalScanRunning && !_blocked)
            {
                ArmQuietTimerLocked();
            }

            status = SetStatusLocked();
        }

        _logger.LogInformation("Ordnerüberwachung: Ruhezeit {Quiet} s", (int)quiet.TotalSeconds);
        Send(status);
        await _settingsStore.SaveAsync(new WatchSettings(enabled, quiet), ct).ConfigureAwait(false);
    }

    // ---- Bibliothek --------------------------------------------------------------------------------

    private void OnActiveLibraryChanged(ActiveLibraryChangedMessage message)
    {
        _logger.LogDebug("Ordnerüberwachung: ActiveLibraryChangedMessage LibraryId={LibraryId}", message.LibraryId);
        _ = AttachToActiveLibraryAsync(CancellationToken.None);
    }

    private async Task AttachToActiveLibraryAsync(CancellationToken ct)
    {
        int version;
        lock (_gate)
        {
            if (!_started)
            {
                return;
            }

            version = ++_attachVersion;
        }

        LibraryInfo? library = null;
        try
        {
            library = await _libraries.GetActiveAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ordnerüberwachung: aktive Bibliothek konnte nicht ermittelt werden");
        }

        var roots = library is null ? [] : GetRootPaths(library).ToArray();
        WatchStatus status;
        lock (_gate)
        {
            if (!_started || version != _attachVersion)
            {
                return; // ein jüngerer Wechsel ist bereits unterwegs
            }

            var unchanged = _libraryId == library?.Id && _roots.SequenceEqual(roots, StringComparer.OrdinalIgnoreCase);
            if (!unchanged)
            {
                CancelOwnScanLocked();
                _libraryId = library?.Id;
                _roots = roots;
                _pendingEvents = 0;
                _dueAtUtc = null;
                _blocked = false;
                _blockedReason = null;
                _externalScanRunning = false;
                _quietTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _logger.LogInformation("Ordnerüberwachung: Bibliothek {LibraryId} ({Name}) mit {Count} Wurzel(n)", library?.Id, library?.Name ?? "-", roots.Length);
            }

            // Dieselbe Bibliothek erneut gemeldet und die Watcher laufen: nicht neu aufbauen (in der Lücke gingen Ereignisse verloren).
            if (!unchanged || (_watchers.Count == 0 && _unavailableRoots.Length == 0))
            {
                RebuildWatchersLocked();
            }

            status = SetStatusLocked();
        }

        Send(status);
    }

    /// <summary>Watcher für die aktuellen Wurzeln (neu) aufbauen; Zähler und Sperre bleiben unberührt.</summary>
    private void RebuildWatchersLocked()
    {
        DisposeWatchersLocked();
        _retryTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _unavailableRoots = [];
        if (!_started || !_enabled || _libraryId is null || _roots.Length == 0)
        {
            return;
        }

        var unavailable = new List<string>();
        foreach (var root in _roots)
        {
            if (!Directory.Exists(root))
            {
                unavailable.Add(root);
                continue;
            }

            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = WatcherBufferSize,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                watcher.Created += (_, e) => OnWatcherEvent(e.ChangeType, e.FullPath, null);
                watcher.Deleted += (_, e) => OnWatcherEvent(e.ChangeType, e.FullPath, null);
                watcher.Changed += (_, e) => OnWatcherEvent(e.ChangeType, e.FullPath, null);
                watcher.Renamed += (_, e) => OnWatcherEvent(e.ChangeType, e.FullPath, e.OldFullPath);
                watcher.Error += (_, e) => OnWatcherError(root, e.GetException());
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
            {
                _logger.LogWarning(ex, "Ordnerüberwachung: Wurzel {Root} kann nicht überwacht werden", root);
                unavailable.Add(root);
            }
        }

        _unavailableRoots = unavailable.ToArray();
        if (unavailable.Count > 0)
        {
            _logger.LogWarning("Ordnerüberwachung: {Count} Wurzel(n) nicht erreichbar, erneuter Versuch in {Seconds} s: {Roots}", unavailable.Count, (int)_rootRetryInterval.TotalSeconds, string.Join(" | ", unavailable));
            _retryTimer.Change(_rootRetryInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private void DisposeWatchersLocked()
    {
        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Ordnerüberwachung: Watcher konnte nicht sauber beendet werden");
            }
        }

        _watchers.Clear();
    }

    private void OnRetryTick()
    {
        WatchStatus status;
        lock (_gate)
        {
            if (!_started || !_enabled || _libraryId is null || _unavailableRoots.Length == 0)
            {
                return;
            }

            var before = _unavailableRoots.Length;
            RebuildWatchersLocked();
            if (_unavailableRoots.Length < before && !_blocked)
            {
                // Wurzel wieder da: währenddessen kann sich alles geändert haben → Scan nach Ruhezeit
                _pendingEvents++;
                ArmQuietTimerLocked();
            }

            status = SetStatusLocked();
        }

        Send(status);
    }

    // ---- Ereignisse --------------------------------------------------------------------------------

    private void OnWatcherEvent(WatcherChangeTypes change, string fullPath, string? oldPath)
    {
        if (!_filter.IsRelevant(change, fullPath, oldPath))
        {
            return;
        }

        WatchStatus? status = null;
        lock (_gate)
        {
            if (!_started || !_enabled || _libraryId is null || _blocked)
            {
                return;
            }

            _pendingEvents++;
            ArmQuietTimerLocked();
            if (!_ownScanRunning && !_externalScanRunning)
            {
                status = SetStatusLocked();
            }
        }

        _logger.LogDebug("Ordnerüberwachung: {Change} {Path}", change, fullPath);
        if (status is not null)
        {
            Send(status);
        }
    }

    private void OnWatcherError(string root, Exception? error)
    {
        _logger.LogWarning(error, "Ordnerüberwachung: Fehler am Watcher für {Root} — Neustart, Scan wird eingeplant", root);
        // Nicht im Watcher-Callback selbst neu aufbauen (Dispose des eigenen Watchers).
        ThreadPool.QueueUserWorkItem(_ =>
        {
            WatchStatus status;
            lock (_gate)
            {
                if (!_started || !_enabled || _libraryId is null)
                {
                    return;
                }

                RebuildWatchersLocked();
                if (!_blocked)
                {
                    _pendingEvents++; // Ereignisse können verloren sein (Pufferüberlauf)
                    ArmQuietTimerLocked();
                }

                status = SetStatusLocked();
            }

            Send(status);
        });
    }

    private void ArmQuietTimerLocked()
    {
        _dueAtUtc = DateTime.UtcNow + _quietPeriod;
        _quietTimer.Change(_quietPeriod, Timeout.InfiniteTimeSpan);
    }

    // ---- Scan --------------------------------------------------------------------------------------

    private void OnQuietElapsed()
    {
        int libraryId;
        CancellationToken token;
        int events;
        WatchStatus status;
        lock (_gate)
        {
            if (!_started || !_enabled || _libraryId is null || _blocked || _pendingEvents == 0)
            {
                return;
            }

            if (_ownScanRunning || _externalScanRunning)
            {
                return; // wird nach dem Ende des laufenden Scans erneut geprüft
            }

            events = _pendingEvents;
            _pendingEvents = 0;
            _dueAtUtc = null;
            _ownScanRunning = true;
            _scanCts = new CancellationTokenSource();
            token = _scanCts.Token;
            libraryId = _libraryId.Value;
            status = SetStatusLocked();
        }

        _logger.LogInformation("Ordnerüberwachung: {Events} Ereignis(se), Ruhezeit abgelaufen → Incremental-Scan der Bibliothek {LibraryId}", events, libraryId);
        Send(status);
        _ = RunAutoScanAsync(libraryId, token);
    }

    private async Task RunAutoScanAsync(int libraryId, CancellationToken token)
    {
        ScanResult? result = null;
        string? blockReason = null;
        try
        {
            result = await _scans.ScanAsync(libraryId, ScanMode.Incremental, ScanOptions.Default, null, token).ConfigureAwait(false);
            switch (result.Outcome)
            {
                case ScanOutcome.MassMissingBlocked:
                    blockReason = $"{result.MissingDetected:N0} fehlende Dateien — Bestätigung auf der Bibliotheksseite nötig („Aktualisieren“)";
                    break;
                case ScanOutcome.LibraryOffline:
                    blockReason = "Bibliothek offline — nach dem Anschließen auf der Bibliotheksseite „Aktualisieren“";
                    break;
                default:
                    _logger.LogInformation(
                        "Ordnerüberwachung: Scan fertig — {Added} neu, {Modified} geändert, {Missing} fehlend, {Reappeared} wieder da ({Duration:N1} s)",
                        result.Added, result.Modified, result.MarkedMissing, result.Reappeared, result.TotalDuration.TotalSeconds);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Ordnerüberwachung: automatischer Scan abgebrochen");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ordnerüberwachung: automatischer Scan fehlgeschlagen");
        }

        WatchStatus status;
        lock (_gate)
        {
            _ownScanRunning = false;
            _scanCts?.Dispose();
            _scanCts = null;
            if (blockReason is not null)
            {
                _blocked = true;
                _blockedReason = blockReason;
                _pendingEvents = 0;
                _dueAtUtc = null;
                _logger.LogWarning("Ordnerüberwachung angehalten: {Reason}", blockReason);
            }
            else if (_pendingEvents > 0 && _started && _enabled && !_externalScanRunning)
            {
                ArmQuietTimerLocked(); // während des Scans kamen weitere Ereignisse
            }

            status = SetStatusLocked(result is { Outcome: ScanOutcome.Completed } ? result : null);
        }

        Send(status);
    }

    private void CancelOwnScanLocked()
    {
        try
        {
            _scanCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Scan ist gerade fertig geworden
        }
    }

    // ---- Manuelle Scans (Bibliotheksseite) ---------------------------------------------------------

    private void OnExternalScanProgress(ScanProgressMessage message)
    {
        WatchStatus? status = null;
        lock (_gate)
        {
            if (!_started || message.LibraryId != _libraryId || _ownScanRunning || _externalScanRunning)
            {
                return;
            }

            _externalScanRunning = true;
            if (_pendingEvents > 0)
            {
                _quietTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _dueAtUtc = null;
                status = SetStatusLocked();
            }
        }

        if (status is not null)
        {
            Send(status);
        }
    }

    private void OnExternalScanFinished(int libraryId, bool completedOk)
    {
        WatchStatus status;
        lock (_gate)
        {
            if (!_started || libraryId != _libraryId || _ownScanRunning)
            {
                return;
            }

            _externalScanRunning = false;
            if (completedOk && _blocked)
            {
                _blocked = false;
                _blockedReason = null;
                _logger.LogInformation("Ordnerüberwachung: Sperre nach erfolgreichem manuellen Scan aufgehoben");
            }

            if (_pendingEvents > 0 && _enabled && !_blocked)
            {
                ArmQuietTimerLocked();
            }

            status = SetStatusLocked();
        }

        Send(status);
    }

    // ---- Zustand -----------------------------------------------------------------------------------

    private WatchStatus SetStatusLocked(ScanResult? lastAutoScan = null)
    {
        WatchState state;
        string? reason = null;
        if (!_started || !_enabled || _libraryId is null)
        {
            state = WatchState.Disabled;
        }
        else if (_blocked)
        {
            state = WatchState.Blocked;
            reason = _blockedReason;
        }
        else if (_ownScanRunning)
        {
            state = WatchState.Scanning;
        }
        else if (_pendingEvents > 0 && _dueAtUtc is not null && !_externalScanRunning)
        {
            state = WatchState.ChangesPending;
        }
        else if (_unavailableRoots.Length > 0)
        {
            state = WatchState.RootUnavailable;
            reason = string.Join(" | ", _unavailableRoots);
        }
        else
        {
            state = WatchState.Idle;
        }

        _status = new WatchStatus(_libraryId, state, state == WatchState.ChangesPending ? _dueAtUtc : null, reason, lastAutoScan);
        return _status;
    }

    private void Send(WatchStatus status)
    {
        try
        {
            _messenger.Send(new WatchStatusChangedMessage(status));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ordnerüberwachung: Statusnachricht konnte nicht zugestellt werden");
        }
    }
}
