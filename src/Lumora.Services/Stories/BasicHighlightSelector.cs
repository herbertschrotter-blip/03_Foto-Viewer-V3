using System.Globalization;
using Lumora.Services.Database;
using Lumora.Services.Quality;

namespace Lumora.Services.Stories;

/// <summary>
/// Einfache Ersatz-Auswahl der Highlights (ADR-0058), bis die Foto-Güte (ADR-0057) die echte Implementierung von
/// <see cref="IHighlightSelector"/> liefert und diese per <c>Replace</c> ersetzt: Punkte aus Favorit, Bewertung und
/// Bildgröße, gleichmäßig über die Zeit verteilt (die Liste wird in gleich große Abschnitte geteilt, je Abschnitt das beste
/// Foto). Keine Serien- oder Doppelten-Erkennung (<see cref="HighlightPick.SimilarCount"/> = 0). Titelbild: das beste Foto
/// (kein Video, wenn es ein Foto gibt).
/// </summary>
public sealed class BasicHighlightSelector(ILibraryContextFactory factory) : IHighlightSelector
{
    private const int ChunkSize = 500;

    public async Task<IReadOnlyList<HighlightPick>> SelectAsync(IReadOnlyList<int> fileIds, HighlightOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        ArgumentNullException.ThrowIfNull(options);
        if (fileIds.Count == 0)
        {
            return [];
        }

        var info = await ReadAsync(fileIds, ct).ConfigureAwait(false);
        var wanted = (int)Math.Round(fileIds.Count * Math.Clamp(options.Share, 0, 1));
        wanted = Math.Min(fileIds.Count, Math.Clamp(wanted, Math.Max(0, options.Min), Math.Max(options.Min, options.Max)));
        if (wanted <= 0)
        {
            return [];
        }

        var picks = new List<HighlightPick>(wanted);
        for (var segment = 0; segment < wanted; segment++)
        {
            var from = (int)((long)segment * fileIds.Count / wanted);
            var to = (int)((long)(segment + 1) * fileIds.Count / wanted);
            var best = -1;
            var bestScore = double.MinValue;
            for (var i = from; i < to; i++)
            {
                var score = Score(info.GetValueOrDefault(fileIds[i]));
                if (score > bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }

            if (best >= 0)
            {
                var row = info.GetValueOrDefault(fileIds[best]);
                picks.Add(new HighlightPick(fileIds[best], bestScore, Reason(row), 0));
            }
        }

        return picks;
    }

    public async Task<int?> PickCoverAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        if (fileIds.Count == 0)
        {
            return null;
        }

        return Best(fileIds, await ReadAsync(fileIds, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Titelbilder vieler Storys über EINE Verbindung (erste Erkennung einer großen Bibliothek: Tausende Storys — die
    /// Katalog-Verbindungen sind nicht gepoolt). Nur diese Ersatz-Implementierung bietet das; Ergebnis wie <see cref="PickCoverAsync"/>.
    /// </summary>
    internal Dictionary<int, int> PickCovers(IReadOnlyList<(int StoryId, List<int> Files)> stories, CancellationToken ct)
    {
        var result = new Dictionary<int, int>(stories.Count);
        using var connection = factory.OpenSqliteConnection();
        foreach (var (storyId, files) in stories)
        {
            ct.ThrowIfCancellationRequested();
            if (files.Count > 0 && Best(files, Read(connection, files, ct)) is { } cover)
            {
                result[storyId] = cover;
            }
        }

        return result;
    }

    private static int? Best(IReadOnlyList<int> fileIds, Dictionary<int, FileRow> info)
    {
        if (fileIds.Count == 0)
        {
            return null;
        }

        var hasPhoto = info.Values.Any(r => !r.IsVideo);
        int? best = null;
        var bestScore = double.MinValue;
        var middle = fileIds.Count / 2.0;
        for (var i = 0; i < fileIds.Count; i++)
        {
            if (!info.TryGetValue(fileIds[i], out var row) || (hasPhoto && row.IsVideo))
            {
                continue;
            }

            // Gleichstand: näher an der Mitte der Story.
            var score = Score(row) - Math.Abs(i - middle) / (fileIds.Count * 100.0);
            if (score > bestScore)
            {
                bestScore = score;
                best = fileIds[i];
            }
        }

        return best ?? fileIds[fileIds.Count / 2];
    }

    private readonly record struct FileRow(bool IsFavorite, int Rating, long Pixels, bool IsVideo);

    private static double Score(FileRow row)
    {
        var score = 0.0;
        if (row.IsFavorite)
        {
            score += 3;
        }

        score += row.Rating * 0.6;
        if (row.Pixels > 0)
        {
            score += Math.Clamp(Math.Log2(row.Pixels / 1_000_000.0 + 1), 0, 5) * 0.3;
        }

        return row.IsVideo ? score - 0.2 : score;
    }

    private static string Reason(FileRow row)
    {
        var parts = new List<string>(3);
        if (row.IsFavorite)
        {
            parts.Add("Favorit");
        }

        if (row.Rating > 0)
        {
            parts.Add(row.Rating.ToString(CultureInfo.InvariantCulture) + (row.Rating == 1 ? " Stern" : " Sterne"));
        }

        if (row.Pixels >= 1_000_000)
        {
            parts.Add((row.Pixels / 1_000_000).ToString(CultureInfo.InvariantCulture) + " MP");
        }

        if (row.IsVideo)
        {
            parts.Add("Video");
        }

        return parts.Count == 0 ? "gleichmäßig über die Zeit verteilt" : string.Join(" · ", parts);
    }

    private Task<Dictionary<int, FileRow>> ReadAsync(IReadOnlyList<int> fileIds, CancellationToken ct)
        => Task.Run(() =>
        {
            using var connection = factory.OpenSqliteConnection();
            return Read(connection, fileIds, ct);
        }, ct);

    private static Dictionary<int, FileRow> Read(Microsoft.Data.Sqlite.SqliteConnection connection, IReadOnlyList<int> fileIds, CancellationToken ct)
    {
        var map = new Dictionary<int, FileRow>(fileIds.Count);
        foreach (var chunk in fileIds.Distinct().Chunk(ChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT Id, IsFavorite, COALESCE(Rating, 0), COALESCE(Width, 0) * COALESCE(Height, 0), MediaType FROM Files WHERE Id IN ({StorySql.IdList(chunk)});";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                map[reader.GetInt32(0)] = new FileRow(reader.GetBoolean(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt32(4) == 1);
            }
        }

        return map;
    }
}
