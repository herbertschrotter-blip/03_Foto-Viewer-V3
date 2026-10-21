using System.Threading;
using System.Threading.Tasks;

namespace PhotoViewerV3.Services.Thumbnails;

/// <summary>
/// Ergebnis einer Thumbnail-Erzeugung. Im Erfolgsfall mit finalen Pixelmaßen und
/// Dateigröße (für <c>Thumbnails.ByteSize</c>), im Fehlerfall mit stabilem ErrorCode
/// + Klartext (für <c>Thumbnails.ErrorCode</c>/<c>ErrorMessage</c>). <see cref="SourceWidth"/>/<see cref="SourceHeight"/>
/// sind die Pixelmaße des ORIGINALS so, wie es angezeigt wird (nach EXIF-Drehung) — 0, wenn der Renderer sie nicht
/// kennt; der ThumbnailService schreibt sie nach <c>Files.Width/Height</c> (ADR-0043).
/// </summary>
public sealed record ThumbnailRenderResult(
    bool Success,
    int Width,
    int Height,
    long ByteSize,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static ThumbnailRenderResult Ok(int width, int height, long byteSize)
        => new(true, width, height, byteSize, null, null);

    public static ThumbnailRenderResult Fail(string errorCode, string errorMessage)
        => new(false, 0, 0, 0, errorCode, errorMessage);

    /// <summary>Breite des Originals nach EXIF-Drehung (0 = unbekannt).</summary>
    public int SourceWidth { get; init; }

    /// <summary>Höhe des Originals nach EXIF-Drehung (0 = unbekannt).</summary>
    public int SourceHeight { get; init; }
}

/// <summary>
/// Erzeugt ein Standbild-Thumbnail aus einer Bilddatei (JPG/PNG). WIC-basiert
/// (System.Windows.Media.Imaging), daher liegt die Implementierung im Wpf-Projekt.
/// Schreibt atomar (<c>.tmp</c> + <see cref="System.IO.File.Move(string,string,bool)"/>)
/// gemäß PLAN Kap. 13 / Spike #4. Läuft thread-safe auf Worker-Threads (Freeze + OnLoad).
/// </summary>
public interface IImageThumbnailRenderer
{
    /// <summary>
    /// Erzeugt das Vorschaubild mit kurzer Kante <paramref name="targetShortEdge"/> (lange Kante höchstens
    /// <see cref="ThumbnailSizing.MaxLongEdge"/>, nie hochskaliert, ADR-0043) und meldet die Maße des Originals.
    /// </summary>
    ThumbnailRenderResult Render(string sourcePath, string targetThumbPath, int targetShortEdge);

    /// <summary>
    /// Pixelmaße des Originals nach EXIF-Drehung, nur aus dem Dateikopf (ohne die Pixel zu dekodieren) — für das
    /// Nachtragen von <c>Files.Width/Height</c>. null = nicht lesbar oder nicht unterstützt (Standard).
    /// </summary>
    (int Width, int Height)? ReadDimensions(string sourcePath) => null;
}

/// <summary>
/// Erzeugt ein Standbild-Thumbnail aus einem Video (Frame-Extraktion via FFmpeg).
/// Timeout/Cancel-Mechanik gemäß Spike #5 und ADR-0007. Atomic <c>.tmp</c> + Move.
/// </summary>
public interface IVideoThumbnailRenderer
{
    /// <summary>True, wenn eine ffmpeg.exe gefunden wurde (PATH oder winget-Links).</summary>
    bool IsAvailable { get; }

    /// <summary>Standbild mit kurzer Kante <paramref name="targetShortEdge"/> (wie Bilder, ADR-0043).</summary>
    Task<ThumbnailRenderResult> RenderAsync(
        string sourcePath,
        string targetThumbPath,
        int targetShortEdge,
        CancellationToken cancellationToken);
}
