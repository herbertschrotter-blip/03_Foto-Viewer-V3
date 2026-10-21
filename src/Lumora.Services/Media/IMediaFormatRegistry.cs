using Lumora.Domain.Enums;

namespace Lumora.Services.Media;

/// <summary>
/// Zentrale Zuordnung Dateiendung → Medientyp. Ersetzt die festen V0.1-Listen
/// (nur jpg/png/mp4). Endungen werden klein und mit führendem Punkt geführt (".jpg").
/// Scanner, Thumbnail-Renderer und Settings-Seite arbeiten ausschließlich über dieses Register.
/// Seit 0.2.8 gehören auch Archiv-Endungen dazu (<see cref="MediaType.Archive"/>, ADR-0022): der Scanner nimmt
/// sie in den Katalog auf; wer nur Fotos und Videos meint, fragt <see cref="IsMedia"/> statt <see cref="IsSupported"/>.
/// </summary>
public interface IMediaFormatRegistry
{
    IReadOnlySet<string> ImageExtensions { get; }
    IReadOnlySet<string> VideoExtensions { get; }

    /// <summary>Archiv-Endungen (.zip, .rar, .7z, .tar, .gz, .tgz, .bz2, .xz).</summary>
    IReadOnlySet<string> ArchiveExtensions { get; }

    /// <summary>Ordnet eine Endung (mit oder ohne Punkt, beliebige Schreibweise) einem Medientyp zu.</summary>
    bool TryClassify(string? extension, out MediaType mediaType);

    /// <summary>True für jede Endung, die der Katalog erfasst (Bild, Video oder Archiv).</summary>
    bool IsSupported(string? extension);

    /// <summary>True nur für Bild- und Videoformate (keine Archive).</summary>
    bool IsMedia(string? extension);
}
