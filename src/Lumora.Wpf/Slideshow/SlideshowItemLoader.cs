using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;
using Lumora.Services.Database;
using Lumora.Services.Metadata;
using Lumora.Services.Places;

namespace Lumora.Slideshow;

/// <summary>Gesichtsrahmen relativ (0..1) zum Bild nach EXIF-Orientierung (wie <c>Faces.Box*</c>, ADR-0035).</summary>
public readonly record struct SlideshowFaceBox(double X, double Y, double Width, double Height);

/// <summary>Was die Diashow über eine Datei wissen muss: Pfad, Art, Aufnahmedatum, Ortsname, Gesichtsrahmen.</summary>
/// <param name="CaptureDate">null = Datum unbekannt bzw. noch nicht gelesen (dann keine Datumszeile).</param>
/// <param name="PlaceName">„Wien, Österreich“ oder null ohne Ort.</param>
/// <param name="Faces">Bestätigte, vorgeschlagene und erkannte Gesichter (ignorierte nicht) — Ziel des langsamen Zooms.</param>
/// <param name="DurationMs">Videolänge laut Katalog oder null.</param>
public sealed record SlideshowItemInfo(
    int FileId,
    string Path,
    MediaType MediaType,
    bool Missing,
    CaptureDateValue? CaptureDate,
    string? PlaceName,
    IReadOnlyList<SlideshowFaceBox> Faces,
    int? DurationMs = null);

/// <summary>Lädt die <see cref="SlideshowItemInfo"/> stapelweise (eine Abfrage je Block statt je Bild).</summary>
public interface ISlideshowItemLoader
{
    /// <summary>Infos zu den Ids; unbekannte Ids (inzwischen gelöscht) fehlen im Ergebnis.</summary>
    Task<IReadOnlyDictionary<int, SlideshowItemInfo>> LoadAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct);
}

/// <summary>
/// Katalog-Lesepfad der Diashow: Dateien samt Ordnerpfad, Aufnahmedatum (Präzision/Abstand, ADR-0046), Ort über
/// <see cref="IPlaceLookup"/> (ADR-0050) und Gesichtsrahmen (ADR-0035/0038) in Blöcken zu höchstens 500 Ids. Liest nur;
/// in einer versteckten Bibliothek ist der Katalog der der Sitzung (ADR-0049).
/// </summary>
public sealed class SlideshowItemLoader : ISlideshowItemLoader
{
    private const int ChunkSize = 500;

    private readonly ILibraryContextFactory _factory;
    private readonly IPlaceLookup? _places;
    private readonly ILogger<SlideshowItemLoader> _logger;

    public SlideshowItemLoader(ILibraryContextFactory factory, ILogger<SlideshowItemLoader> logger, IPlaceLookup? places = null)
    {
        _factory = factory;
        _logger = logger;
        _places = places;
    }

    public async Task<IReadOnlyDictionary<int, SlideshowItemInfo>> LoadAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct)
    {
        var result = new Dictionary<int, SlideshowItemInfo>(fileIds.Count);
        var ids = fileIds.Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0)
        {
            return result;
        }

        await Task.Run(
            async () =>
            {
                await using var ctx = _factory.Create();
                foreach (var chunk in ids.Chunk(ChunkSize))
                {
                    ct.ThrowIfCancellationRequested();
                    var part = chunk.ToList();
                    var rows = await (
                            from f in ctx.Files.AsNoTracking()
                            join d in ctx.Folders.AsNoTracking() on f.FolderId equals d.Id
                            where part.Contains(f.Id)
                            select new
                            {
                                f.Id,
                                f.Name,
                                FolderPath = d.Path,
                                f.MediaType,
                                f.Missing,
                                f.CapturedAt,
                                f.CapturedAtSource,
                                f.CapturedAtPrecision,
                                f.CapturedAtOffset,
                                f.PlaceId,
                                f.DurationMs,
                            })
                        .ToListAsync(ct)
                        .ConfigureAwait(false);
                    var faces = await ctx.Faces.AsNoTracking()
                        .Where(x => part.Contains(x.FileId) && x.State != FaceState.Ignored)
                        .Select(x => new { x.FileId, x.BoxX, x.BoxY, x.BoxW, x.BoxH })
                        .ToListAsync(ct)
                        .ConfigureAwait(false);
                    var facesByFile = faces
                        .GroupBy(x => x.FileId)
                        .ToDictionary(g => g.Key, g => (IReadOnlyList<SlideshowFaceBox>)g.Select(x => new SlideshowFaceBox(x.BoxX, x.BoxY, x.BoxW, x.BoxH)).ToList());

                    foreach (var row in rows)
                    {
                        CaptureDateValue? date = null;
                        if (row.CapturedAt is { } at && row.CapturedAtSource is { } source)
                        {
                            var unix = new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)).ToUnixTimeSeconds();
                            date = CaptureDateInfo.FromColumns(row.Id, unix, (int)source, (int)row.CapturedAtPrecision, row.CapturedAtOffset).Value;
                        }

                        result[row.Id] = new SlideshowItemInfo(
                            row.Id,
                            System.IO.Path.Combine(row.FolderPath, row.Name),
                            row.MediaType,
                            row.Missing,
                            date,
                            PlaceName(row.PlaceId),
                            facesByFile.TryGetValue(row.Id, out var boxes) ? boxes : [],
                            row.DurationMs);
                    }
                }
            },
            ct).ConfigureAwait(false);

        _logger.LogDebug("Diashow: {Found} von {Asked} Datei-Infos geladen", result.Count, ids.Count);
        return result;
    }

    private string? PlaceName(int? placeId)
    {
        if (placeId is not { } id || _places is null)
        {
            return null;
        }

        try
        {
            return PlaceText.Short(_places.GetPlace(id));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException)
        {
            _logger.LogDebug(ex, "Ortsname für die Diashow nicht lesbar");
            return null;
        }
    }
}
