using System;
using System.Threading;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;

namespace PhotoViewerV3.Lightbox;

/// <summary>
/// Prozessweiter LibVLC-Anker (Spike #03, Folge-Erkenntnisse 1 + 2): <c>Core.Initialize()</c> genau
/// einmal pro Prozess, eine <see cref="LibVLC"/>-Instanz pro App, ein <see cref="MediaPlayer"/> pro
/// Lightbox-Fenster (<see cref="CreatePlayer"/>). Singleton; der Host disposed ihn beim Beenden,
/// nachdem die Fenster ihre Player freigegeben haben.
/// </summary>
public sealed class LibVlcHost : IDisposable
{
    private static int s_coreInitialized;

    private readonly ILogger<LibVlcHost> _logger;
    private readonly object _gate = new();
    private LibVLC? _libVlc;
    private bool _disposed;

    public LibVlcHost(ILogger<LibVlcHost> logger)
    {
        _logger = logger;
    }

    /// <summary>Zuletzt gewählte Lautstärke (0–100); gilt sitzungsweit für alle Lightbox-Fenster.</summary>
    public int LastVolume { get; set; } = 80;

    /// <summary>Zuletzt gewählter Stumm-Zustand; gilt sitzungsweit.</summary>
    public bool LastMuted { get; set; }

    /// <summary>Liefert die LibVLC-Instanz; initialisiert Core + LibVLC beim ersten Aufruf.</summary>
    public LibVLC GetLibVlc()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_libVlc is not null)
            {
                return _libVlc;
            }

            if (Interlocked.CompareExchange(ref s_coreInitialized, 1, 0) == 0)
            {
                try
                {
                    Core.Initialize();
                    _logger.LogInformation("LibVLC Core initialisiert");
                }
                catch
                {
                    // Nächster Versuch darf Core.Initialize erneut aufrufen.
                    Interlocked.Exchange(ref s_coreInitialized, 0);
                    throw;
                }
            }

            // --no-video-title-show: kein Dateiname-Overlay von VLC beim Start eines Videos.
            _libVlc = new LibVLC(enableDebugLogs: false, "--no-video-title-show");
            _logger.LogInformation("LibVLC-Instanz erstellt (Version {Version})", _libVlc.Version);
            return _libVlc;
        }
    }

    /// <summary>
    /// Erzeugt einen MediaPlayer für eine Lightbox-Session. Eigene Tastatur-/Maus-Verarbeitung von
    /// VLC ist abgeschaltet (die Lightbox steuert selbst), Hardware-Decoding an (Spike #03, Erkenntnis 5).
    /// </summary>
    public MediaPlayer CreatePlayer()
    {
        var player = new MediaPlayer(GetLibVlc())
        {
            EnableHardwareDecoding = true,
            EnableKeyInput = false,
            EnableMouseInput = false,
        };
        return player;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _libVlc?.Dispose();
            _libVlc = null;
            _logger.LogInformation("LibVLC-Instanz freigegeben");
        }
    }
}
