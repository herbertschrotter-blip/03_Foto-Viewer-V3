using PhotoViewerV3.Domain.Enums;

namespace PhotoViewerV3.Services.Media;

/// <summary>
/// Standard-Implementierung mit den gängigen Foto- und Videoformaten.
/// Bild-Decoding läuft über WIC: JPG/PNG/GIF/BMP/TIFF nativ, WebP/HEIC/HEIF/AVIF und Kamera-RAW
/// nur mit den jeweiligen Windows-Codec-Paketen aus dem Store (sonst FFmpeg-Fallback oder Platzhalter).
/// Video-Thumbnails kommen aus FFmpeg, Wiedergabe aus LibVLC — beide decodieren praktisch alles.
/// </summary>
public sealed class MediaFormatRegistry : IMediaFormatRegistry
{
    public static readonly string[] DefaultImageExtensions =
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".tif", ".tiff",
        ".webp", ".heic", ".heif", ".avif",
        ".dng", ".cr2", ".cr3", ".nef", ".nrw", ".arw", ".orf", ".rw2", ".raf", ".pef", ".srw",
    };

    public static readonly string[] DefaultVideoExtensions =
    {
        ".mp4", ".m4v", ".mov", ".avi", ".mkv", ".webm", ".wmv", ".asf", ".flv", ".f4v",
        ".mpg", ".mpeg", ".mpe", ".m2v", ".ts", ".mts", ".m2ts", ".vob", ".3gp", ".3g2", ".ogv", ".divx",
    };

    private readonly HashSet<string> _images;
    private readonly HashSet<string> _videos;

    public MediaFormatRegistry()
        : this(DefaultImageExtensions, DefaultVideoExtensions)
    {
    }

    public MediaFormatRegistry(IEnumerable<string> imageExtensions, IEnumerable<string> videoExtensions)
    {
        _images = Normalize(imageExtensions);
        _videos = Normalize(videoExtensions);
    }

    public IReadOnlySet<string> ImageExtensions => _images;
    public IReadOnlySet<string> VideoExtensions => _videos;

    public bool TryClassify(string? extension, out MediaType mediaType)
    {
        var e = NormalizeOne(extension);
        if (e is not null)
        {
            if (_images.Contains(e))
            {
                mediaType = MediaType.Image;
                return true;
            }

            if (_videos.Contains(e))
            {
                mediaType = MediaType.Video;
                return true;
            }
        }

        mediaType = default;
        return false;
    }

    public bool IsSupported(string? extension) => TryClassify(extension, out _);

    private static HashSet<string> Normalize(IEnumerable<string> extensions)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in extensions)
        {
            var e = NormalizeOne(raw);
            if (e is not null)
            {
                set.Add(e);
            }
        }

        return set;
    }

    private static string? NormalizeOne(string? raw)
    {
        var e = raw?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(e))
        {
            return null;
        }

        return e.StartsWith('.') ? e : "." + e;
    }
}
