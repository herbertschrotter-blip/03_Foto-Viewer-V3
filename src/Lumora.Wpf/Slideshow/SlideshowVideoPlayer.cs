using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;
using Lumora.Lightbox;

namespace Lumora.Slideshow;

/// <summary>Video-Wiedergabe der Diashow (Fenster-unabhängiger Vertrag, damit das ViewModel ohne LibVLC testbar bleibt).</summary>
public interface ISlideshowVideoPlayer : IDisposable
{
    /// <summary>Video zu Ende oder Wiedergabefehler (beliebiger Thread).</summary>
    event EventHandler? Ended;

    /// <summary>Startet die Wiedergabe; false, wenn die Datei fehlt oder LibVLC sie ablehnt.</summary>
    Task<bool> PlayAsync(string path, CancellationToken ct);

    void Pause();

    void Resume();

    /// <summary>Stoppt und gibt das aktuelle Medium frei (der Player bleibt für die Diashow).</summary>
    void Stop();
}

/// <summary>
/// LibVLC-Wiedergabe über den gemeinsamen <see cref="LibVlcHost"/> der Lightbox (ein MediaPlayer je Diashow-Fenster,
/// Hardware-Dekodierung, eigene Tasten/Maus von VLC aus). Mit Ton in der zuletzt gewählten Lautstärke der Lightbox. Das
/// Fenster bindet <see cref="Player"/> an sein VideoView, sobald <see cref="PlayerCreated"/> kommt; vor dem Start wartet
/// <see cref="PlayAsync"/> kurz auf dessen Fenster-Handle (sonst öffnete VLC ein eigenes Fenster).
/// </summary>
public sealed class SlideshowVideoPlayer : ISlideshowVideoPlayer
{
    private readonly LibVlcHost _host;
    private readonly ILogger _logger;
    private MediaPlayer? _player;
    private Media? _media;
    private volatile bool _pauseRequested;
    private volatile bool _disposed;

    public SlideshowVideoPlayer(LibVlcHost host, ILogger logger)
    {
        _host = host;
        _logger = logger;
    }

    public event EventHandler? Ended;

    /// <summary>Der MediaPlayer wurde erzeugt (UI-Thread) — das Fenster setzt ihn ins VideoView.</summary>
    public event EventHandler? PlayerCreated;

    public MediaPlayer? Player => _player;

    /// <summary>True, solange ein Video läuft (Rauchtest).</summary>
    public bool IsPlaying => _player?.IsPlaying == true;

    public async Task<bool> PlayAsync(string path, CancellationToken ct)
    {
        if (_disposed || !File.Exists(path))
        {
            return false;
        }

        try
        {
            if (_player is null)
            {
                var player = _host.CreatePlayer();
                player.EndReached += OnEnded;
                player.EncounteredError += OnEnded;
                player.Playing += OnPlaying;
                _player = player;
                PlayerCreated?.Invoke(this, EventArgs.Empty);
            }

            Stop();
            _pauseRequested = false; // die Diashow pausiert ein neues Video bei Bedarf selbst (Pause nach dem Start)
            var media = new Media(_host.GetLibVlc(), path, FromType.FromPath);
            _media = media;
            for (var i = 0; i < 40 && _player.Hwnd == IntPtr.Zero; i++)
            {
                await Task.Delay(25, ct).ConfigureAwait(true);
            }

            if (ct.IsCancellationRequested || !ReferenceEquals(_media, media))
            {
                return false;
            }

            _player.Volume = Math.Clamp(_host.LastVolume, 0, 100);
            _player.Mute = _host.LastMuted;
            return _player.Play(media);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Diashow: Video konnte nicht gestartet werden");
            return false;
        }
    }

    public void Pause()
    {
        // Play() startet asynchron: kommt die Pause vor „Playing“, holt OnPlaying sie nach.
        _pauseRequested = true;
        try
        {
            if (_player?.IsPlaying == true)
            {
                _player.SetPause(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Diashow: Pause fehlgeschlagen");
        }
    }

    public void Resume()
    {
        _pauseRequested = false;
        try
        {
            _player?.SetPause(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Diashow: Fortsetzen fehlgeschlagen");
        }
    }

    public void Stop()
    {
        var media = _media;
        _media = null;
        try
        {
            _player?.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Diashow: Stop fehlgeschlagen");
        }

        media?.Dispose();
    }

    private void OnEnded(object? sender, EventArgs e) => Ended?.Invoke(this, EventArgs.Empty);

    private void OnPlaying(object? sender, EventArgs e)
    {
        if (!_pauseRequested || _player is not { } player)
        {
            return;
        }

        // Nie LibVLC aus dem eigenen Ereignis-Thread aufrufen (Verklemmung) — auf den Thread-Pool verlagern.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                if (_pauseRequested && !_disposed)
                {
                    player.SetPause(true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Diashow: nachgeholte Pause fehlgeschlagen");
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        var player = _player;
        _player = null;
        if (player is not null)
        {
            player.EndReached -= OnEnded;
            player.EncounteredError -= OnEnded;
            player.Playing -= OnPlaying;
            player.Dispose();
        }
    }
}
