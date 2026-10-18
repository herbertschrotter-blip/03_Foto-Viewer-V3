using PhotoViewerV3.Domain.Enums;

namespace PhotoViewerV3.Services.Media;

/// <summary>
/// Zentrale Zuordnung Dateiendung → Medientyp. Ersetzt die festen V0.1-Listen
/// (nur jpg/png/mp4). Endungen werden klein und mit führendem Punkt geführt (".jpg").
/// Scanner, Thumbnail-Renderer und Settings-Seite arbeiten ausschließlich über dieses Register.
/// </summary>
public interface IMediaFormatRegistry
{
    IReadOnlySet<string> ImageExtensions { get; }
    IReadOnlySet<string> VideoExtensions { get; }

    /// <summary>Ordnet eine Endung (mit oder ohne Punkt, beliebige Schreibweise) einem Medientyp zu.</summary>
    bool TryClassify(string? extension, out MediaType mediaType);

    bool IsSupported(string? extension);
}
