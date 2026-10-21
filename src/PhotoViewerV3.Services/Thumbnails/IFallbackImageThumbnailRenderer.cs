namespace PhotoViewerV3.Services.Thumbnails;

/// <summary>
/// Zweiter Versuch für Bildformate, die WIC nicht decodieren kann (WebP/HEIC/AVIF/Kamera-RAW
/// ohne Windows-Codec-Paket). Der <see cref="ThumbnailService"/> ruft ihn nur nach einem
/// Fehlschlag des Bild-Renderers mit <see cref="ThumbnailErrorCodes.WicDecodeFailed"/>;
/// erst wenn auch der Fallback scheitert, gilt das Thumbnail als Failed.
/// Implementiert vom FFmpeg-Wrapper (<see cref="FfmpegVideoThumbnailer"/>), atomar (.tmp + Move).
/// </summary>
public interface IFallbackImageThumbnailRenderer
{
    /// <summary>False, wenn das Fallback-Werkzeug (FFmpeg) fehlt — dann bleibt es beim WIC-Fehler.</summary>
    bool IsAvailable { get; }

    /// <summary>Standbild mit kurzer Kante <paramref name="targetShortEdge"/> (wie WIC, ADR-0043).</summary>
    Task<ThumbnailRenderResult> RenderAsync(
        string sourcePath,
        string targetThumbPath,
        int targetShortEdge,
        CancellationToken cancellationToken);
}
