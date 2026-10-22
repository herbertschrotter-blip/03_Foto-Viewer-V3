using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;
using Lumora.Services.Database;
using Lumora.Services.Library;

namespace Lumora.Stories;

/// <summary>Was die Story-Seite über eine Datei wissen muss: Vorschaubild-Quelle, Maße, Video, Favorit, Uhrzeit, Ort.</summary>
/// <param name="LocalTime">Aufnahmezeit in Ortszeit (mit eigenem Abstand, sonst Zeitzone des Rechners); null ohne Aufnahmedatum (die Änderungszeit taugt nicht für Tageszeit-Abschnitte).</param>
public sealed record StoryMediaInfo(
    int FileId,
    int LibraryId,
    string Path,
    string Name,
    MediaType MediaType,
    long Size,
    DateTime ModifiedAtUtc,
    int? Width,
    int? Height,
    bool IsFavorite,
    byte? Rating,
    int? DurationMs,
    DateTime? LocalTime,
    int? PlaceId)
{
    public bool IsVideo => MediaType == MediaType.Video;

    /// <summary>Seitenverhältnis nach EXIF-Drehung (<c>Files.Width/Height</c>, ADR-0043); ohne Maße 4:3.</summary>
    public double Aspect => Width is > 0 && Height is > 0 ? (double)Width.Value / Height.Value : StoryLayoutEngine.DefaultAspect;

    /// <summary>„0:42“, „1:12:05“; null ohne Länge.</summary>
    public string? DurationText => DurationMs is > 0 ? FormatDuration(TimeSpan.FromMilliseconds(DurationMs.Value)) : null;

    internal static string FormatDuration(TimeSpan duration)
        => duration.TotalHours >= 1
            ? ((int)duration.TotalHours).ToString(CultureInfo.InvariantCulture) + ":" + duration.ToString(@"mm\:ss", CultureInfo.InvariantCulture)
            : ((int)duration.TotalMinutes).ToString(CultureInfo.InvariantCulture) + ":" + duration.ToString(@"ss", CultureInfo.InvariantCulture);
}

/// <summary>Person auf einer Karte bzw. im Titelbild (bestätigte Gesichter, meiste Fotos zuerst) mit Gesicht für das Rund-Bild.</summary>
public sealed record StoryAvatarInfo(int PersonId, string Name, int FileCount, int? FaceId, int LibraryId);

/// <summary>Liest Datei-Infos und Personen der Storys stapelweise aus dem Katalog (nur lesend, Bereich des Katalogs der Sitzung).</summary>
public interface IStoryMediaLoader
{
    /// <summary>Infos zu den Ids; unbekannte (inzwischen gelöschte) fehlen.</summary>
    Task<IReadOnlyDictionary<int, StoryMediaInfo>> LoadAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct = default);

    /// <summary>Je Story höchstens <paramref name="perStory"/> Personen (bestätigte Gesichter im aktuellen Bereich).</summary>
    Task<IReadOnlyDictionary<int, IReadOnlyList<StoryAvatarInfo>>> LoadPersonsAsync(IReadOnlyCollection<int> storyIds, int perStory = 3, CancellationToken ct = default);
}

/// <summary>
/// Katalog-Lesepfad der Story-Seite (ADR-0060): Dateien samt Ordnerpfad in Blöcken zu höchstens 500 Ids (eine Abfrage je
/// Block), Personen je Story über bestätigte Gesichter (<c>Faces.State = 2</c>) nur im aktuellen Bereich (ADR-0048), mit
/// Titel-Gesicht der Person (<c>Persons.CoverFaceId</c>, sonst ein Gesicht aus der Story). Liest nur; in einer versteckten
/// Bibliothek ist der Katalog der der Sitzung (ADR-0049).
/// </summary>
public sealed class StoryMediaLoader : IStoryMediaLoader
{
    private const int ChunkSize = 500;

    private readonly ILibraryContextFactory _factory;
    private readonly ILibraryScope _scope;
    private readonly ILogger<StoryMediaLoader> _logger;

    public StoryMediaLoader(ILibraryContextFactory factory, ILibraryScope scope, ILogger<StoryMediaLoader> logger)
    {
        _factory = factory;
        _scope = scope;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<int, StoryMediaInfo>> LoadAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct = default)
    {
        var result = new Dictionary<int, StoryMediaInfo>(fileIds.Count);
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
                                f.LibraryId,
                                f.Name,
                                FolderPath = d.Path,
                                f.MediaType,
                                f.Size,
                                f.ModifiedAtUtc,
                                f.Width,
                                f.Height,
                                f.IsFavorite,
                                f.Rating,
                                f.DurationMs,
                                f.CapturedAt,
                                f.CapturedAtOffset,
                                f.PlaceId,
                            })
                        .ToListAsync(ct)
                        .ConfigureAwait(false);
                    foreach (var row in rows)
                    {
                        result[row.Id] = new StoryMediaInfo(
                            row.Id,
                            row.LibraryId,
                            System.IO.Path.Combine(row.FolderPath, row.Name),
                            row.Name,
                            row.MediaType,
                            row.Size,
                            row.ModifiedAtUtc,
                            row.Width,
                            row.Height,
                            row.IsFavorite,
                            row.Rating,
                            row.DurationMs,
                            row.CapturedAt is { } captured ? LocalTime(captured, row.CapturedAtOffset) : null,
                            row.PlaceId);
                    }
                }
            },
            ct).ConfigureAwait(false);

        _logger.LogDebug("Storys: {Found} von {Asked} Datei-Infos geladen", result.Count, ids.Count);
        return result;
    }

    /// <summary>Wanduhrzeit der Aufnahme: UTC + eigener Abstand, sonst in der Zeitzone des Rechners.</summary>
    internal static DateTime LocalTime(DateTime utc, int? offsetMinutes)
    {
        var value = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return offsetMinutes is { } minutes
            ? DateTime.SpecifyKind(value.AddMinutes(minutes), DateTimeKind.Unspecified)
            : DateTime.SpecifyKind(value.ToLocalTime(), DateTimeKind.Unspecified);
    }

    public async Task<IReadOnlyDictionary<int, IReadOnlyList<StoryAvatarInfo>>> LoadPersonsAsync(IReadOnlyCollection<int> storyIds, int perStory = 3, CancellationToken ct = default)
    {
        var result = new Dictionary<int, IReadOnlyList<StoryAvatarInfo>>();
        var ids = storyIds.Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0)
        {
            return result;
        }

        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        if (!scope.HasLibrary)
        {
            return result;
        }

        var libraries = string.Join(",", scope.LibraryIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));
        await Task.Run(
            () =>
            {
                using var connection = _factory.OpenSqliteConnection();
                var rows = new List<(int StoryId, int PersonId, string Name, int Count, int? CoverFace, int AnyFace)>();
                foreach (var chunk in ids.Chunk(ChunkSize))
                {
                    ct.ThrowIfCancellationRequested();
                    using var cmd = connection.CreateCommand();
                    // Ids sind ganze Zahlen aus dem Katalog (keine Benutzereingabe) — als Literale wie in GalleryPageSource.
                    cmd.CommandText = $"""
                        SELECT si.StoryId, p.Id, p.Name, COUNT(DISTINCT fa.FileId), p.CoverFaceId, MIN(fa.Id)
                        FROM StoryItems si
                        CROSS JOIN Files f ON f.Id = si.FileId
                        CROSS JOIN Faces fa ON fa.FileId = si.FileId AND fa.State = 2
                        CROSS JOIN Persons p ON p.Id = fa.PersonId
                        WHERE si.StoryId IN ({string.Join(",", chunk.Select(id => id.ToString(CultureInfo.InvariantCulture)))})
                          AND si.Origin >= 0 AND f.LibraryId IN ({libraries}) AND p.IsHidden = 0
                        GROUP BY si.StoryId, p.Id;
                        """;
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        rows.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt32(3),
                            reader.IsDBNull(4) ? null : reader.GetInt32(4), reader.GetInt32(5)));
                    }
                }

                // Titel-Gesichter der Personen bzw. ein Gesicht aus der Story: Bibliothek ihrer Datei (Ausschnitt-Cache je Bibliothek)
                var coverFaces = rows.Where(r => r.CoverFace is not null).Select(r => r.CoverFace!.Value).Concat(rows.Select(r => r.AnyFace)).Distinct().ToList();
                var coverLibrary = new Dictionary<int, int>();
                foreach (var chunk in coverFaces.Chunk(ChunkSize))
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = $"""
                        SELECT fa.Id, f.LibraryId FROM Faces fa CROSS JOIN Files f ON f.Id = fa.FileId
                        WHERE fa.Id IN ({string.Join(",", chunk.Select(id => id.ToString(CultureInfo.InvariantCulture)))}) AND f.LibraryId IN ({libraries});
                        """;
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        coverLibrary[reader.GetInt32(0)] = reader.GetInt32(1);
                    }
                }

                foreach (var story in rows.GroupBy(r => r.StoryId))
                {
                    result[story.Key] = story
                        .OrderByDescending(r => r.Count).ThenBy(r => r.Name, StringComparer.CurrentCulture)
                        .Take(Math.Max(1, perStory))
                        .Select(r => r.CoverFace is { } cover && coverLibrary.TryGetValue(cover, out var library)
                            ? new StoryAvatarInfo(r.PersonId, r.Name, r.Count, cover, library)
                            : new StoryAvatarInfo(r.PersonId, r.Name, r.Count, r.AnyFace, coverLibrary.GetValueOrDefault(r.AnyFace)))
                        .ToList();
                }
            },
            ct).ConfigureAwait(false);
        return result;
    }
}
