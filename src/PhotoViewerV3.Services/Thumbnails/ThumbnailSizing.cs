using System;

namespace PhotoViewerV3.Services.Thumbnails;

/// <summary>
/// Größe der Vorschaubilder (ADR-0043): die KURZE Kante wird auf <see cref="ShortEdge"/> px gebracht, damit eine
/// Kachel mit „UniformToFill“ bei jeder Ausrichtung genug Pixel hat (bisher lange Kante 320 px: ein Querformat hatte
/// nur 180–240 px Höhe). Gewählt nach Spike #11: kurze Kante 480 bei Qualität 85 kostete 8,3 GB je 100 000 Fotos,
/// 400 bei Qualität 82 kostet 5,5 GB (Grenze ≈ 6 GB) und deckt die größte Kachel bis 125 % Bildschirm-Skalierung ab.
/// Die lange Kante ist auf das <see cref="MaxAspect"/>-fache begrenzt, damit Panoramen nicht riesig werden; kleine
/// Originale werden nie hochskaliert. Reine Rechnung, WPF-frei — der WIC-Renderer (Wpf) und der FFmpeg-Renderer
/// (Services) nutzen dieselbe Regel.
/// </summary>
public static class ThumbnailSizing
{
    /// <summary>Ziel der kurzen Kante in px (= <c>Thumbnails.Size</c> und Teil des Dateinamens <c>{fileId}_400_still.jpg</c>).</summary>
    public const int ShortEdge = 400;

    /// <summary>Die lange Kante ist höchstens so viel mal länger als die kurze Zielkante (400 → 1200 px).</summary>
    public const int MaxAspect = 3;

    /// <summary>JPEG-Qualität der Vorschaubilder (Spike #11: Q85 wäre ≈ 10 % größer; unter 80 wird der WPF-Encoder größer, nicht kleiner).</summary>
    public const int JpegQuality = 82;

    /// <summary>Vorschaubild-Größen vor 0.2.45 (lange Kante); werden im Hintergrund ersetzt.</summary>
    public static readonly int[] LegacySizes = [320];

    /// <summary>Höchstlänge der langen Kante zu einer kurzen Zielkante.</summary>
    public static int MaxLongEdge(int shortEdge) => shortEdge * MaxAspect;

    /// <summary>
    /// Zielmaße für ein Bild von <paramref name="width"/> × <paramref name="height"/> Pixeln: kurze Kante
    /// <paramref name="shortEdge"/>, lange Kante höchstens <paramref name="maxLongEdge"/>, nie größer als das Original,
    /// Seitenverhältnis bleibt (gerundet, mindestens 1 px). Unbekannte Maße (≤ 0) → (0, 0).
    /// </summary>
    public static (int Width, int Height) Fit(int width, int height, int shortEdge, int maxLongEdge)
    {
        if (width <= 0 || height <= 0 || shortEdge <= 0)
        {
            return (0, 0);
        }

        var shortSide = Math.Min(width, height);
        var longSide = Math.Max(width, height);
        var scale = Math.Min(1.0, (double)shortEdge / shortSide);
        if (maxLongEdge > 0)
        {
            scale = Math.Min(scale, (double)maxLongEdge / longSide);
        }

        if (scale >= 1.0)
        {
            return (width, height);
        }

        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary><see cref="Fit(int, int, int, int)"/> mit <see cref="MaxLongEdge"/> zur kurzen Kante.</summary>
    public static (int Width, int Height) Fit(int width, int height, int shortEdge)
        => Fit(width, height, shortEdge, MaxLongEdge(shortEdge));

    /// <summary>
    /// Maße so, wie das Bild angezeigt wird: EXIF-Orientierung 5–8 (90°/270° gedreht) vertauscht Breite und Höhe der
    /// gespeicherten Pixel.
    /// </summary>
    public static (int Width, int Height) Oriented(int storedWidth, int storedHeight, int orientation)
        => orientation is >= 5 and <= 8 ? (storedHeight, storedWidth) : (storedWidth, storedHeight);
}
