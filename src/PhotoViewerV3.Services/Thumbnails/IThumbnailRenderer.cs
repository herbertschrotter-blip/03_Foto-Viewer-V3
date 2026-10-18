using System.Threading;
using System.Threading.Tasks;

namespace PhotoViewerV3.Services.Thumbnails;

/// <summary>
/// Ergebnis einer Thumbnail-Erzeugung. Im Erfolgsfall mit finalen Pixelmaßen und
/// Dateigröße (für <c>Thumbnails.ByteSize</c>), im Fehlerfall mit stabilem ErrorCode
/// + Klartext (für <c>Thumbnails.ErrorCode</c>/<c>ErrorMessage</c>).
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
}

/// <summary>
/// Erzeugt ein Standbild-Thumbnail aus einer Bilddatei (JPG/PNG). WIC-basiert
/// (System.Windows.Media.Imaging), daher liegt die Implementierung im Wpf-Projekt.
/// Schreibt atomar (<c>.tmp</c> + <see cref="System.IO.File.Move(string,string,bool)"/>)
/// gemäß PLAN Kap. 13 / Spike #4. Läuft thread-safe auf Worker-Threads (Freeze + OnLoad).
/// </summary>
public interface IImageThumbnailRenderer
{
    ThumbnailRenderResult Render(string sourcePath, string targetThumbPath, int targetWidth);
}

/// <summary>
/// Erzeugt ein Standbild-Thumbnail aus einem Video (Frame-Extraktion via FFmpeg).
/// Timeout/Cancel-Mechanik gemäß Spike #5 und ADR-0007. Atomic <c>.tmp</c> + Move.
/// </summary>
public interface IVideoThumbnailRenderer
{
    /// <summary>True, wenn eine ffmpeg.exe gefunden wurde (PATH oder winget-Links).</summary>
    bool IsAvailable { get; }

    Task<ThumbnailRenderResult> RenderAsync(
        string sourcePath,
        string targetThumbPath,
        int targetWidth,
        CancellationToken cancellationToken);
}
