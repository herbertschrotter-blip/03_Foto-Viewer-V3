using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Services.Thumbnails;

namespace PhotoViewerV3.Gallery.ViewModels;

/// <summary>
/// Pixelgröße, auf die ein Vorschaubild für die Kachel dekodiert wird (ADR-0043): Kachel in DIP × Bildschirm-Skalierung.
/// Das Bild wird so dekodiert, dass es diese Fläche ganz bedeckt (wie „UniformToFill“), also nie hochskaliert wird.
/// </summary>
public readonly record struct ThumbnailDecodeBox(int Width, int Height)
{
    /// <summary>Ab diesem Faktor gilt eine neue Fläche als „deutlich größer“ und lohnt ein erneutes Dekodieren.</summary>
    public const double RedecodeThreshold = 1.15;

    /// <summary>
    /// Rand der Kachel um den Bildbereich in DIP (GalleryView: Container-Rand 6 + Rahmen 2 je Seite). Höhe zusätzlich die
    /// Namenszeile (Polster 4 + zwei Textzeilen 11/10 pt ≈ 36) — bewusst knapp (32) geschätzt: lieber etwas größer
    /// dekodieren als hochskalieren.
    /// </summary>
    public const double TileChromeWidth = 16;

    /// <inheritdoc cref="TileChromeWidth"/>
    public const double TileChromeHeight = 16 + 32;

    /// <summary>
    /// Bildbereich einer Kachel <paramref name="tileWidth"/> × <paramref name="tileHeight"/> DIP (ohne Rand und
    /// Namenszeile) bei <paramref name="dpiScale"/> (1,0 = 96 dpi) in Pixeln.
    /// </summary>
    public static ThumbnailDecodeBox FromTile(double tileWidth, double tileHeight, double dpiScale)
    {
        var scale = dpiScale > 0 && double.IsFinite(dpiScale) ? dpiScale : 1.0;
        return new ThumbnailDecodeBox(
            Math.Max(1, (int)Math.Ceiling(Math.Max(1, tileWidth - TileChromeWidth) * scale)),
            Math.Max(1, (int)Math.Ceiling(Math.Max(1, tileHeight - TileChromeHeight) * scale)));
    }

    /// <summary>true, wenn ein für diese Fläche dekodiertes Bild für <paramref name="wanted"/> sichtbar zu klein wäre.</summary>
    public bool IsClearlySmallerThan(ThumbnailDecodeBox wanted)
        => wanted.Width > Width * RedecodeThreshold || wanted.Height > Height * RedecodeThreshold;

    /// <summary>
    /// Dekodierbreite für ein Bild von <paramref name="imageWidth"/> × <paramref name="imageHeight"/> px: so groß, dass
    /// es die Fläche bedeckt (Maßstab = max(Breite, Höhe)), nie größer als das Bild selbst. 0 = in voller Größe dekodieren.
    /// </summary>
    public int DecodeWidthFor(int imageWidth, int imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            return 0;
        }

        var scale = Math.Max((double)Width / imageWidth, (double)Height / imageHeight);
        return scale >= 1.0 ? 0 : Math.Max(1, (int)Math.Ceiling(imageWidth * scale));
    }

    internal long Pack() => ((long)Width << 32) | (uint)Height;

    internal static ThumbnailDecodeBox Unpack(long packed) => new((int)(packed >> 32), (int)(packed & 0xFFFFFFFF));
}

/// <summary>
/// Vorschaubilder der Kacheln scharf laden (ADR-0043): bisher fest 180 px Breite (ein Querformat blieb 180 × 120 px und
/// wurde in der größten Kachel bei 150 % auf bis zu 480 px hochgezogen). Jetzt bestimmt die Kachelgröße (Regler) ×
/// Bildschirm-Skalierung des Hauptfensters (<see cref="VisualTreeHelper.GetDpi"/>), wie groß dekodiert wird. Wird der
/// Regler größer gezogen oder wechselt das Fenster auf einen Bildschirm mit höherer Skalierung, werden nur die Kacheln
/// neu dekodiert, deren Bild für die neue Fläche deutlich zu klein ist — im Hintergrund, entprellt, sichtbare zuerst und
/// gebündelt mit Background-Priorität an den UI-Thread wie neu erzeugte. Kleiner ziehen dekodiert nichts neu.
/// </summary>
public sealed partial class GalleryViewModel
{
    private const int RedecodeDebounceMs = 250;

    /// <summary>Aktuelle Dekodierfläche (gepackt, damit Worker-Threads sie ohne Sperre lesen).</summary>
    private long _thumbDecodeBox = ThumbnailDecodeBox.FromTile(DefaultTileSize, TileHeightAt180, 1.0).Pack();

    /// <summary>Je Kachel: Datei und Fläche, für die ihr Vorschaubild dekodiert wurde. Nur auf dem UI-Thread.</summary>
    private readonly Dictionary<int, (string Path, ThumbnailDecodeBox Box)> _thumbnailSources = new();

    private CancellationTokenSource? _redecodeCts;
    private Window? _dpiWindow;

    /// <summary>Ein fertig dekodiertes Vorschaubild auf dem Weg zum UI-Thread.</summary>
    private readonly record struct ReadyThumbnail(int FileId, ImageSource Image, string Path, ThumbnailDecodeBox Box);

    /// <summary>Fläche, auf die neue Vorschaubilder gerade dekodiert werden (thread-sicher).</summary>
    public ThumbnailDecodeBox ThumbnailDecodeBox => ThumbnailDecodeBox.Unpack(Interlocked.Read(ref _thumbDecodeBox));

    /// <summary>
    /// Berechnet die Dekodierfläche aus Kachelgröße und Skalierung des Hauptfensters neu (UI-Thread) und plant bei einer
    /// deutlich größeren Fläche das erneute Dekodieren. Hängt sich einmal an <see cref="Window.DpiChanged"/>.
    /// </summary>
    internal void UpdateThumbnailDecodeBox()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(UpdateThumbnailDecodeBox);
            return;
        }

        var box = ThumbnailDecodeBox.FromTile(TileWidth, TileHeight, ReadDpiScale());
        var old = ThumbnailDecodeBox.Unpack(Interlocked.Exchange(ref _thumbDecodeBox, box.Pack()));
        if (_thumbnailSources.Count > 0 && (box.Width > old.Width || box.Height > old.Height))
        {
            ScheduleRedecode(); // nur beim Vergrößern; kleiner ziehen behält die schärferen Bilder
        }
    }

    private double ReadDpiScale()
    {
        try
        {
            var window = Application.Current?.MainWindow;
            if (window is null)
            {
                return 1.0;
            }

            if (!ReferenceEquals(window, _dpiWindow))
            {
                if (_dpiWindow is not null)
                {
                    _dpiWindow.DpiChanged -= OnWindowDpiChanged;
                }

                window.DpiChanged += OnWindowDpiChanged;
                _dpiWindow = window;
            }

            return VisualTreeHelper.GetDpi(window).DpiScaleX;
        }
        catch (InvalidOperationException)
        {
            return 1.0; // anderer Thread oder Fenster noch ohne Quelle
        }
    }

    private void OnWindowDpiChanged(object? sender, DpiChangedEventArgs e)
    {
        _logger.LogInformation("Bildschirm-Skalierung der Galerie: {Old:P0} → {New:P0}", e.OldDpi.DpiScaleX, e.NewDpi.DpiScaleX);
        UpdateThumbnailDecodeBox();
    }

    /// <summary>Entprellt (Regler liefert viele Zwischenwerte), dann die zu kleinen Kacheln neu dekodieren — sichtbare zuerst.</summary>
    private void ScheduleRedecode()
    {
        _redecodeCts?.Cancel();
        var cts = new CancellationTokenSource();
        _redecodeCts = cts;
        FireAndForget(() => RedecodeAfterDelayAsync(cts.Token));
    }

    private async Task RedecodeAfterDelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(RedecodeDebounceMs, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var wanted = ThumbnailDecodeBox;
        var stale = new List<GalleryItem>();
        foreach (var item in CurrentPage)
        {
            if (_thumbnailSources.TryGetValue(item.Id, out var source) && source.Box.IsClearlySmallerThan(wanted))
            {
                stale.Add(item);
            }
        }

        if (stale.Count == 0)
        {
            return;
        }

        var work = OrderByVisibility(stale, CurrentPage, _visibleFirstIndex, _visibleLastIndex)
            .Select(it => (it.Id, _thumbnailSources[it.Id].Path))
            .ToList();
        _logger.LogDebug("Vorschaubilder neu dekodieren: {Count} Kacheln auf {Width}×{Height} px", work.Count, wanted.Width, wanted.Height);
        await Task.Run(
            () =>
            {
                foreach (var (fileId, path) in work)
                {
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    if (_currentIdsSnapshot.Contains(fileId) && TryLoadThumbnail(path, wanted) is { } image)
                    {
                        EnqueueReadyThumbnail(new ReadyThumbnail(fileId, image, path, wanted));
                    }
                }
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Gebündelt und mit Background-Priorität an den UI-Thread (beim Erstaufbau kommen hunderte in Sekunden).</summary>
    private void EnqueueReadyThumbnail(ReadyThumbnail ready)
    {
        _readyThumbnails.Enqueue(ready);
        if (Interlocked.CompareExchange(ref _thumbnailFlushScheduled, 1, 0) != 0)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            Interlocked.Exchange(ref _thumbnailFlushScheduled, 0);
            return;
        }

        _ = dispatcher.InvokeAsync(FlushReadyThumbnails, DispatcherPriority.Background);
    }

    /// <summary>
    /// Ein Vorschaubild älterer Größe kommt zu spät (es wurde noch dekodiert, als das neue schon fertig war): nicht über
    /// das schon gezeigte neue legen.
    /// </summary>
    private bool IsOutdatedThumbnail(ReadyThumbnail entry)
        => IsLegacyThumbnailPath(entry.Path)
           && _thumbnailSources.TryGetValue(entry.FileId, out var shown)
           && !IsLegacyThumbnailPath(shown.Path);

    /// <summary>Datei einer älteren Vorschaubild-Größe (Name <c>{fileId}_{size}_still.jpg</c> mit size ≠ aktueller Größe).</summary>
    internal static bool IsLegacyThumbnailPath(string path)
        => !Path.GetFileName(path).Contains($"_{ThumbnailService.Size}_", StringComparison.Ordinal);

    /// <summary>Merkt Datei und Fläche des angezeigten Vorschaubilds; räumt Einträge verschwundener Kacheln gelegentlich ab.</summary>
    private void RememberThumbnailSource(ReadyThumbnail ready)
    {
        _thumbnailSources[ready.FileId] = (ready.Path, ready.Box);
        if (_thumbnailSources.Count > 2 * _currentItems.Count + 256)
        {
            foreach (var id in _thumbnailSources.Keys.Where(id => !_currentItems.ContainsKey(id)).ToList())
            {
                _thumbnailSources.Remove(id);
            }
        }
    }

    /// <summary>
    /// Lädt ein Vorschaubild für eine Kachel der Fläche <paramref name="box"/>: erst nur der Dateikopf (Maße), dann
    /// dekodiert auf die kleinste Breite, bei der es die Fläche bedeckt (nie größer als die Datei). Eingefroren, damit
    /// es vom Worker-Thread an den UI-Thread darf; null bei Fehlern (Platzhalter bleibt).
    /// </summary>
    internal static ImageSource? TryLoadThumbnail(string path, ThumbnailDecodeBox box)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var frame = BitmapDecoder.Create(
                fs,
                BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None).Frames[0];
            var decodeWidth = box.DecodeWidthFor(frame.PixelWidth, frame.PixelHeight);
            fs.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodeWidth > 0)
            {
                bmp.DecodePixelWidth = decodeWidth;
            }

            bmp.StreamSource = fs;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
