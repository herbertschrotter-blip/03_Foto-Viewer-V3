using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Spike03.Services;

namespace PhotoViewerV3.Spike03.ViewModels;

public sealed partial class SpikeLightboxViewModel : ObservableObject, IDisposable
{
    private readonly SpikeMediaDownloader _downloader;
    private readonly ILogger<SpikeLightboxViewModel> _logger;
    private static int _coreInitialized;

    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private Media? _media;
    private CancellationTokenSource? _downloadCts;
    private Stopwatch? _openWatch;

    public SpikeLightboxViewModel(SpikeMediaDownloader downloader, ILogger<SpikeLightboxViewModel> logger)
    {
        _downloader = downloader;
        _logger = logger;
        SamplePath = _downloader.ExpectedLocalPath;
        IsLocallyAvailable = _downloader.IsLocallyAvailable;
    }

    public MediaPlayer? Player => _player;

    [ObservableProperty] private string _status = "Bereit. Klicke 'Sample laden', dann 'Öffnen + Play'.";
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadFraction;
    [ObservableProperty] private string _samplePath = string.Empty;
    [ObservableProperty] private bool _isLocallyAvailable;
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _timeToFirstFrameMs;
    [ObservableProperty] private long _durationMs;
    [ObservableProperty] private long _positionMs;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private int _disposeLoopCount;

    [RelayCommand]
    private async Task DownloadSampleAsync()
    {
        if (IsDownloading)
        {
            return;
        }

        _downloadCts = new CancellationTokenSource();
        IsDownloading = true;
        ErrorMessage = string.Empty;
        Status = $"Verbinde zu {_downloader.SourceUri.Host} …";
        _logger.LogInformation("Sample-Download starts: target={Target} source={Source}",
            _downloader.ExpectedLocalPath, _downloader.SourceUri);
        long lastLoggedMb = 0;
        var progress = new Progress<DownloadProgress>(p =>
        {
            DownloadFraction = p.Fraction;
            Status = $"Lade Sample … {p.Bytes / 1024.0 / 1024.0:N1} MB / {p.TotalBytes / 1024.0 / 1024.0:N1} MB ({p.Fraction * 100:N1} %)";
            var currentMb = p.Bytes / 1024 / 1024;
            if (currentMb - lastLoggedMb >= 10)
            {
                _logger.LogInformation("Sample-Download progress: {Bytes:N0} / {Total:N0} bytes ({Pct:N1} %)",
                    p.Bytes, p.TotalBytes, p.Fraction * 100);
                lastLoggedMb = currentMb;
            }
        });

        try
        {
            var sw = Stopwatch.StartNew();
            var info = await _downloader.EnsureSampleAsync(progress, _downloadCts.Token).ConfigureAwait(true);
            sw.Stop();
            SamplePath = info.LocalPath;
            IsLocallyAvailable = true;
            Status = info.WasDownloaded
                ? $"Sample geladen: {info.SizeBytes / 1024.0 / 1024.0:N1} MB in {sw.Elapsed.TotalSeconds:N1} s"
                : $"Sample bereits lokal ({info.SizeBytes / 1024.0 / 1024.0:N1} MB).";
            _logger.LogInformation("Sample bereit: {Path} (was-downloaded: {Down}, {Bytes:N0} bytes, {Sec:N1} s)",
                info.LocalPath, info.WasDownloaded, info.SizeBytes, sw.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            Status = "Download abgebrochen.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.LogError(ex, "Sample-Download fehlgeschlagen");
        }
        finally
        {
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private void OpenAndPlay()
    {
        try
        {
            EnsureCoreInitialized();
            DisposeMediaPlayerOnly();

            _libVlc ??= new LibVLC(enableDebugLogs: false);
            _player = new MediaPlayer(_libVlc);
            _player.Playing += OnPlayerPlaying;
            _player.Paused += OnPlayerPaused;
            _player.Stopped += OnPlayerStopped;
            _player.LengthChanged += OnPlayerLengthChanged;
            _player.TimeChanged += OnPlayerTimeChanged;

            _media = new Media(_libVlc, new Uri(SamplePath));
            _openWatch = Stopwatch.StartNew();
            _player.Play(_media);
            IsReady = true;
            Status = "Player gestartet …";
            _logger.LogInformation("MediaPlayer Play: {Path}", SamplePath);
            OnPropertyChanged(nameof(Player));
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.LogError(ex, "OpenAndPlay fehlgeschlagen");
        }
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (_player is null)
        {
            return;
        }

        if (_player.IsPlaying)
        {
            _player.Pause();
        }
        else
        {
            _player.Play();
        }
    }

    [RelayCommand]
    private void StopPlayback()
    {
        _player?.Stop();
    }

    [RelayCommand]
    private void DisposeAndReopen()
    {
        // Lifecycle-Test: 10× Open + Close → Handle-Count beobachten
        DisposeMediaPlayerOnly();
        DisposeLoopCount++;
        Status = $"Disposed (Loop {DisposeLoopCount}). Klicke 'Öffnen + Play' für neuen Lifecycle.";
    }

    public void NotifyOpenedInWindow()
    {
        Status = "Wiedergabe in eigenem Window — schließe Window mit Esc oder X.";
    }

    private void OnPlayerPlaying(object? sender, EventArgs e)
    {
        if (_openWatch is not null && _openWatch.IsRunning)
        {
            _openWatch.Stop();
            TimeToFirstFrameMs = _openWatch.Elapsed.TotalMilliseconds;
            _logger.LogInformation("Time-to-First-Frame: {Ms:N0} ms", TimeToFirstFrameMs);
        }

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            IsPlaying = true;
            Status = $"Spielt … First-Frame: {TimeToFirstFrameMs:N0} ms";
        });
    }

    private void OnPlayerPaused(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.InvokeAsync(() => IsPlaying = false);
    }

    private void OnPlayerStopped(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            IsPlaying = false;
            PositionMs = 0;
            Status = "Wiedergabe gestoppt.";
        });
    }

    private void OnPlayerLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        Application.Current?.Dispatcher.InvokeAsync(() => DurationMs = e.Length);
    }

    private void OnPlayerTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        Application.Current?.Dispatcher.InvokeAsync(() => PositionMs = e.Time);
    }

    private void EnsureCoreInitialized()
    {
        if (Interlocked.CompareExchange(ref _coreInitialized, 1, 0) == 0)
        {
            Core.Initialize();
            _logger.LogInformation("LibVLC Core initialized");
        }
    }

    private void DisposeMediaPlayerOnly()
    {
        if (_player is not null)
        {
            _player.Playing -= OnPlayerPlaying;
            _player.Paused -= OnPlayerPaused;
            _player.Stopped -= OnPlayerStopped;
            _player.LengthChanged -= OnPlayerLengthChanged;
            _player.TimeChanged -= OnPlayerTimeChanged;
            _player.Stop();
            _player.Dispose();
            _player = null;
            OnPropertyChanged(nameof(Player));
        }

        _media?.Dispose();
        _media = null;
        IsReady = false;
        IsPlaying = false;
    }

    public void Dispose()
    {
        _downloadCts?.Cancel();
        DisposeMediaPlayerOnly();
        _libVlc?.Dispose();
        _libVlc = null;
    }
}
