using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Services.Library;
using PhotoViewerV3.Services.Messaging;
using PhotoViewerV3.Services.Scanning;
using PhotoViewerV3.Services.Thumbnails;

namespace PhotoViewerV3.Shell;

/// <summary>
/// Statusleiste gemäß ADR-0013: Scan-Fortschritt (ScanProgress-/ScanCompleted-/ScanAbortedMessage),
/// Thumbnail-Warteschlange (IThumbnailService.PendingCount, alle 500 ms), Hintergrund-Aktivität und Hinweise
/// (aktive Bibliothek offline). Alle Messenger-Nachrichten werden auf den UI-Thread dispatcht.
/// </summary>
public sealed partial class StatusBarViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan ThumbnailPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ResultDisplayDuration = TimeSpan.FromSeconds(12);

    private readonly IThumbnailService _thumbnails;
    private readonly ILibraryService _libraries;
    private readonly ILogger<StatusBarViewModel> _logger;
    private readonly IMessenger _messenger = WeakReferenceMessenger.Default;
    private readonly DispatcherTimer _thumbnailTimer;
    private readonly DispatcherTimer _resultTimer;

    [ObservableProperty] private string? _activeLibraryName;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string? _scanText;
    [ObservableProperty] private string? _scanResultText;
    [ObservableProperty] private bool _scanResultIsError;
    [ObservableProperty] private int _thumbnailPending;
    [ObservableProperty] private bool _hasThumbnailWork;
    [ObservableProperty] private string? _hintText;
    [ObservableProperty] private string? _backgroundActivityText;
    [ObservableProperty] private bool _isBackgroundActive;

    public StatusBarViewModel(IThumbnailService thumbnails, ILibraryService libraries, ILogger<StatusBarViewModel> logger)
    {
        _thumbnails = thumbnails;
        _libraries = libraries;
        _logger = logger;

        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _thumbnailTimer = new DispatcherTimer(ThumbnailPollInterval, DispatcherPriority.Background, OnThumbnailTick, dispatcher);
        _resultTimer = new DispatcherTimer(ResultDisplayDuration, DispatcherPriority.Background, OnResultTick, dispatcher);
        _resultTimer.Stop();

        _messenger.Register<StatusBarViewModel, ScanProgressMessage>(this, static (r, m) => UiThread.Post(() => r.OnScanProgress(m)));
        _messenger.Register<StatusBarViewModel, ScanCompletedMessage>(this, static (r, m) => UiThread.Post(() => r.OnScanCompleted(m)));
        _messenger.Register<StatusBarViewModel, ScanAbortedMessage>(this, static (r, m) => UiThread.Post(() => r.OnScanAborted(m)));
        _messenger.Register<StatusBarViewModel, ActiveLibraryChangedMessage>(this, static (r, m) => UiThread.Post(() => { _ = r.RefreshLibraryAsync(); }));
    }

    /// <summary>Einmal nach dem Laden des Hauptfensters: aktive Bibliothek + Offline-Hinweis ermitteln.</summary>
    public Task InitializeAsync() => RefreshLibraryAsync();

    private async Task RefreshLibraryAsync()
    {
        try
        {
            var active = await _libraries.GetActiveAsync().ConfigureAwait(true);
            ActiveLibraryName = active?.Name;
            HintText = active is { IsRootAvailable: false }
                ? $"Bibliothek offline: {active.RootPath} ist nicht erreichbar"
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Statusleiste: aktive Bibliothek konnte nicht ermittelt werden");
        }
    }

    private void OnScanProgress(ScanProgressMessage message)
    {
        var p = message.Progress;
        IsScanning = true;
        ScanResultText = null;
        _resultTimer.Stop();
        ScanText = $"Scan: {p.FilesDiscovered:N0} Dateien · {p.FoldersDiscovered:N0} Ordner · {Formatting.Bytes(p.TotalBytesDiscovered)} — {Formatting.ScanPhase(p.Phase)}";
        UpdateActivity();
    }

    private void OnScanCompleted(ScanCompletedMessage message)
    {
        var r = message.Result;
        IsScanning = false;
        ScanText = null;
        ScanResultIsError = false;
        ScanResultText = $"Scan abgeschlossen: {r.FilesInserted:N0} Dateien in {Formatting.Duration(r.TotalDuration)}";
        _resultTimer.Start();
        UpdateActivity();
        _ = RefreshLibraryAsync();
    }

    private void OnScanAborted(ScanAbortedMessage message)
    {
        IsScanning = false;
        ScanText = null;
        ScanResultIsError = !message.Canceled;
        ScanResultText = message.Canceled ? "Scan abgebrochen" : $"Scan fehlgeschlagen: {message.Reason}";
        _resultTimer.Start();
        UpdateActivity();
    }

    private void OnResultTick(object? sender, EventArgs e)
    {
        _resultTimer.Stop();
        ScanResultText = null;
    }

    private void OnThumbnailTick(object? sender, EventArgs e)
    {
        var pending = _thumbnails.PendingCount;
        if (pending == ThumbnailPending)
        {
            return;
        }

        ThumbnailPending = pending;
        HasThumbnailWork = pending > 0;
        UpdateActivity();
    }

    private void UpdateActivity()
    {
        var parts = new List<string>(2);
        if (IsScanning)
        {
            parts.Add("Scan");
        }

        if (HasThumbnailWork)
        {
            parts.Add("Vorschaubilder");
        }

        IsBackgroundActive = parts.Count > 0;
        BackgroundActivityText = IsBackgroundActive ? "Hintergrund: " + string.Join(" · ", parts) : null;
    }

    public void Dispose()
    {
        _thumbnailTimer.Stop();
        _resultTimer.Stop();
        _messenger.UnregisterAll(this);
    }
}
