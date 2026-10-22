using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Lightbox.Imaging;
using Lumora.Services.Assistant;

namespace Lumora.Stories.Assistant;

/// <summary>
/// Collage-Bild über WIC/WPF (ADR-0063): jedes Original wird so groß dekodiert, dass es seinen Platz gerade füllt
/// (EXIF-Ausrichtung wie in Lightbox/Diashow), mittig beschnitten gezeichnet, darüber optional die sachliche Überschrift und
/// das Datum je Foto. Rendert auf einem eigenen STA-Thread (der UI-Thread bleibt frei), schreibt erst eine Temp-Datei im
/// Zielordner und benennt sie dann um — nichts landet außerhalb des Zielordners (versteckte Bibliothek: Container).
/// Druckgröße mit 300 dpi in den Metadaten. Ein unlesbares Foto bleibt eine graue Fläche (Protokoll ohne Pfad).
/// </summary>
public sealed class WpfCollageRenderer : ICollageRenderer
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private readonly ILogger<WpfCollageRenderer> _logger;

    public WpfCollageRenderer(ILogger<WpfCollageRenderer>? logger = null)
    {
        _logger = logger ?? NullLogger<WpfCollageRenderer>.Instance;
    }

    public Task RenderAsync(CollageRenderJob job, IProgress<int>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Render(job, progress, ct);
                tcs.SetResult();
            }
            catch (OperationCanceledException)
            {
                tcs.SetCanceled(ct);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); // Dispatcher des Render-Threads freigeben
            }
        })
        {
            IsBackground = true,
            Name = "Lumora Collage",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private void Render(CollageRenderJob job, IProgress<int>? progress, CancellationToken ct)
    {
        var layout = job.Layout;
        var visual = new DrawingVisual();
        var failed = 0;
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, layout.Width, layout.Height));
            if (layout.Title is { } titleRect && !string.IsNullOrWhiteSpace(job.Title))
            {
                DrawTitle(dc, titleRect, job.Title!, job.TitleNote);
            }

            var done = 0;
            foreach (var cell in layout.Cells)
            {
                ct.ThrowIfCancellationRequested();
                if (cell.Index >= job.Photos.Count)
                {
                    continue;
                }

                var photo = job.Photos[cell.Index];
                var rect = new Rect(cell.Rect.X, cell.Rect.Y, cell.Rect.Width, cell.Rect.Height);
                var image = TryDecode(photo.Path, cell.Rect.Width, cell.Rect.Height, ct);
                if (image is null)
                {
                    failed++;
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xEA)), null, rect);
                }
                else
                {
                    dc.PushClip(new RectangleGeometry(rect));
                    dc.DrawImage(image, Fill(rect, image.PixelWidth, image.PixelHeight));
                    dc.Pop();
                }

                if (job.ShowDates && photo.LocalTime is { } time)
                {
                    DrawDate(dc, rect, time);
                }

                progress?.Report(++done);
            }
        }

        var bitmap = new RenderTargetBitmap(layout.Width, layout.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        BitmapSource output = bitmap;
        if (layout.Dpi != 96)
        {
            // gleiche Pixel, andere Auflösungs-Angabe (Druck 300 dpi)
            var stride = layout.Width * 4;
            var pixels = new byte[stride * layout.Height];
            bitmap.CopyPixels(pixels, stride, 0);
            output = BitmapSource.Create(layout.Width, layout.Height, layout.Dpi, layout.Dpi, PixelFormats.Pbgra32, null, pixels, stride);
        }

        BitmapEncoder encoder = job.Format == CollageImageFormat.Png
            ? new PngBitmapEncoder()
            : new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(job.Format == CollageImageFormat.Png ? output : new FormatConvertedBitmap(output, PixelFormats.Bgr24, null, 0)));
        var folder = Path.GetDirectoryName(job.OutputPath) ?? ".";
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, "." + Path.GetFileNameWithoutExtension(job.OutputPath) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                encoder.Save(stream);
            }

            ct.ThrowIfCancellationRequested();
            File.Move(temp, job.OutputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        if (failed > 0)
        {
            _logger.LogWarning("Collage: {Count} Fotos nicht lesbar (graue Fläche)", failed);
        }
    }

    /// <summary>Original so dekodiert, dass es den Platz füllt (nie größer als die Quelle); null bei unlesbarer Datei.</summary>
    private BitmapSource? TryDecode(string path, int width, int height, CancellationToken ct)
    {
        try
        {
            var (sourceWidth, sourceHeight, exif) = LightboxImageDecoder.ReadHeader(path);
            var rotated = exif.Orientation is 5 or 6 or 7 or 8;
            var displayWidth = rotated ? sourceHeight : sourceWidth;
            var displayHeight = rotated ? sourceWidth : sourceHeight;
            var scale = displayWidth > 0 && displayHeight > 0 ? Math.Min(1.0, Math.Max((double)width / displayWidth, (double)height / displayHeight)) : 1.0;
            var decodeWidth = Math.Max(1, (int)Math.Ceiling(sourceWidth * scale));
            return LightboxImageDecoder.Decode(path, decodeWidth, ct).Bitmap;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _logger.LogDebug("Collage: Foto nicht lesbar ({Error})", ex.GetType().Name); // ohne Pfad (Datenschutz)
            return null;
        }
    }

    /// <summary>Bild mittig so groß, dass es <paramref name="target"/> ganz bedeckt (wird beschnitten).</summary>
    internal static Rect Fill(Rect target, double imageWidth, double imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            return target;
        }

        var scale = Math.Max(target.Width / imageWidth, target.Height / imageHeight);
        var w = imageWidth * scale;
        var h = imageHeight * scale;
        return new Rect(target.X + ((target.Width - w) / 2), target.Y + ((target.Height - h) / 2), w, h);
    }

    private static void DrawTitle(DrawingContext dc, Lumora.Services.Assistant.CollageRect rect, string title, string? note)
    {
        var size = rect.Height * 0.62;
        var face = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var text = new FormattedText(title, German, FlowDirection.LeftToRight, face, size, new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)), 1.0)
        {
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            MaxTextWidth = Math.Max(10, rect.Width * 0.8),
        };
        dc.DrawText(text, new Point(rect.X, rect.Y + ((rect.Height - text.Height) / 2)));
        if (!string.IsNullOrWhiteSpace(note))
        {
            var small = new FormattedText(note, German, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size * 0.5, new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)), 1.0);
            dc.DrawText(small, new Point(rect.Right - small.Width, rect.Y + ((rect.Height - small.Height) / 2)));
        }
    }

    private static void DrawDate(DrawingContext dc, Rect rect, DateTime time)
    {
        var size = Math.Clamp(rect.Height * 0.07, 9, 64);
        var text = new FormattedText(time.ToString("d. MMMM yyyy", German), German, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, Brushes.White, 1.0)
        {
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            MaxTextWidth = Math.Max(10, rect.Width - (size * 1.2)),
        };
        var band = new Rect(rect.X, rect.Bottom - (text.Height + (size * 0.6)), rect.Width, text.Height + (size * 0.6));
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)), null, band);
        dc.DrawText(text, new Point(rect.X + (size * 0.6), band.Y + (size * 0.3)));
    }
}
