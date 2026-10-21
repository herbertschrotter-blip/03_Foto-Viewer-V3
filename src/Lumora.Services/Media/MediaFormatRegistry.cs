using Lumora.Domain.Enums;

namespace Lumora.Services.Media;

/// <summary>
/// Standard-Implementierung mit den gängigen Foto- und Videoformaten.
/// Bild-Decoding läuft über WIC: JPG/PNG/GIF/BMP/TIFF nativ, WebP/HEIC/HEIF/AVIF und Kamera-RAW
/// nur mit den jeweiligen Windows-Codec-Paketen aus dem Store (sonst FFmpeg-Fallback oder Platzhalter).
/// Video-Thumbnails kommen aus FFmpeg, Wiedergabe aus LibVLC — beide decodieren praktisch alles.
/// Archive (ZIP/RAR/7z/TAR/GZ/TGZ/BZ2/XZ) sind Katalog-Einträge ohne Thumbnail (ADR-0022).
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

    public static readonly string[] DefaultArchiveExtensions =
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz",
    };

    private readonly HashSet<string> _images;
    private readonly HashSet<string> _videos;
    private readonly HashSet<string> _archives;

    public MediaFormatRegistry()
        : this(DefaultImageExtensions, DefaultVideoExtensions, DefaultArchiveExtensions)
    {
    }

    /// <summary>Eigene Bild-/Videolisten; Archiv-Endungen bleiben die Standardliste.</summary>
    public MediaFormatRegistry(IEnumerable<string> imageExtensions, IEnumerable<string> videoExtensions)
        : this(imageExtensions, videoExtensions, DefaultArchiveExtensions)
    {
    }

    public MediaFormatRegistry(IEnumerable<string> imageExtensions, IEnumerable<string> videoExtensions, IEnumerable<string> archiveExtensions)
    {
        _images = Normalize(imageExtensions);
        _videos = Normalize(videoExtensions);
        _archives = Normalize(archiveExtensions);
    }

    public IReadOnlySet<string> ImageExtensions => _images;
    public IReadOnlySet<string> VideoExtensions => _videos;
    public IReadOnlySet<string> ArchiveExtensions => _archives;

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

            if (_archives.Contains(e))
            {
                mediaType = MediaType.Archive;
                return true;
            }
        }

        mediaType = default;
        return false;
    }

    public bool IsSupported(string? extension) => TryClassify(extension, out _);

    public bool IsMedia(string? extension) => TryClassify(extension, out var mediaType) && mediaType is MediaType.Image or MediaType.Video;

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
