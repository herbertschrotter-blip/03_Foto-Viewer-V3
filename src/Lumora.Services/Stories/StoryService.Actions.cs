using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;
using Lumora.Services.Albums;
using Lumora.Services.Library;
using Lumora.Services.Messaging;

namespace Lumora.Services.Stories;

public sealed partial class StoryService
{
    /// <summary>Höchstlänge eines eigenen Titels.</summary>
    public const int MaxTitleLength = 200;

    public async Task SetPinnedAsync(int storyId, bool pinned, CancellationToken ct = default)
    {
        var scope = await RequireAsync(storyId, ct).ConfigureAwait(false);
        await WriteAsync((c, tx) => StorySql.Execute(c, tx,
            "UPDATE Stories SET IsPinned = $p, PinnedAtUtc = $t, UpdatedAtUtc = $now WHERE Id = $id;",
            ("$p", pinned), ("$t", pinned ? Now() : null), ("$now", Now()), ("$id", storyId)), ct).ConfigureAwait(false);
        Publish(scope.GroupId, StoriesChangeKind.Updated, storyId);
    }

    public async Task SetHiddenAsync(int storyId, bool hidden, CancellationToken ct = default)
    {
        var scope = await RequireAsync(storyId, ct).ConfigureAwait(false);
        await WriteAsync((c, tx) => StorySql.Execute(c, tx,
            "UPDATE Stories SET IsHidden = $h, UpdatedAtUtc = $now WHERE Id = $id;", ("$h", hidden), ("$now", Now()), ("$id", storyId)), ct).ConfigureAwait(false);
        Publish(scope.GroupId, StoriesChangeKind.Updated, storyId);
    }

    public async Task MarkSeenAsync(IReadOnlyCollection<int> storyIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(storyIds);
        if (storyIds.Count == 0)
        {
            return;
        }

        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        var changed = await WriteAsync((c, tx) => StorySql.Execute(c, tx,
            $"UPDATE Stories SET IsSeen = 1, SeenAtUtc = $now WHERE GroupId = $g AND IsSeen = 0 AND Id IN ({StorySql.IdList(storyIds)});",
            ("$now", Now()), ("$g", scope.GroupId)), ct).ConfigureAwait(false);
        if (changed > 0)
        {
            Publish(scope.GroupId, StoriesChangeKind.Updated, storyIds.ToArray());
        }
    }

    public async Task MarkAllSeenAsync(CancellationToken ct = default)
    {
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        var changed = await WriteAsync((c, tx) => StorySql.Execute(c, tx,
            "UPDATE Stories SET IsSeen = 1, SeenAtUtc = $now WHERE GroupId = $g AND IsSeen = 0;", ("$now", Now()), ("$g", scope.GroupId)), ct).ConfigureAwait(false);
        if (changed > 0)
        {
            Publish(scope.GroupId, StoriesChangeKind.Updated);
        }
    }

    public async Task SetCustomTitleAsync(int storyId, string? title, CancellationToken ct = default)
    {
        var scope = await RequireAsync(storyId, ct).ConfigureAwait(false);
        var text = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        if (text is { Length: > MaxTitleLength })
        {
            text = text[..MaxTitleLength].TrimEnd();
        }

        await WriteAsync((c, tx) => StorySql.Execute(c, tx,
            "UPDATE Stories SET CustomTitle = $t, UpdatedAtUtc = $now WHERE Id = $id;", ("$t", text), ("$now", Now()), ("$id", storyId)), ct).ConfigureAwait(false);
        Publish(scope.GroupId, StoriesChangeKind.Updated, storyId);
    }

    public async Task SetCoverAsync(int storyId, int? fileId, CancellationToken ct = default)
    {
        var scope = await RequireAsync(storyId, ct).ConfigureAwait(false);
        if (fileId is { } f)
        {
            var set = await WriteAsync((c, tx) => StorySql.Execute(c, tx, """
                UPDATE Stories SET CoverFileId = $f, CoverIsManual = 1, UpdatedAtUtc = $now
                WHERE Id = $id AND EXISTS (SELECT 1 FROM StoryItems si WHERE si.StoryId = $id AND si.FileId = $f AND si.Origin >= 0);
                """, ("$f", f), ("$now", Now()), ("$id", storyId)), ct).ConfigureAwait(false);
            if (set == 0)
            {
                throw new InvalidOperationException("Das Titelbild muss ein Foto der Story sein.");
            }
        }
        else
        {
            await WriteAsync((c, tx) => StorySql.Execute(c, tx,
                "UPDATE Stories SET CoverFileId = NULL, CoverIsManual = 0, UpdatedAtUtc = $now WHERE Id = $id;", ("$now", Now()), ("$id", storyId)), ct).ConfigureAwait(false);
            await EnsureCoverAsync(storyId, ct).ConfigureAwait(false);
        }

        Publish(scope.GroupId, StoriesChangeKind.Updated, storyId);
    }

    public async Task<int> AddFilesAsync(int storyId, IReadOnlyList<int> fileIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        var scope = await RequireAsync(storyId, ct).ConfigureAwait(false);
        if (fileIds.Count == 0)
        {
            return 0;
        }

        var zone = _clock.Zone;
        var added = await WriteAsync((c, tx) =>
        {
            var items = ReadFiles(c, tx, fileIds, scope.GroupLibraryIds, zone);

            // Schon automatisch drin: als „hinzugefügt“ markieren (zählt schon, bleibt aber auch, wenn die Erkennung sie später nicht mehr zählt).
            foreach (var chunk in items.Select(i => i.FileId).Chunk(ChunkSize))
            {
                StorySql.Execute(c, tx, $"UPDATE StoryItems SET Origin = 1 WHERE StoryId = $s AND Origin = 0 AND FileId IN ({StorySql.IdList(chunk)});", ("$s", storyId));
            }

            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO StoryItems (StoryId, FileId, Day, Origin, AddedAtUtc) VALUES ($s, $f, $d, 1, $now)
                ON CONFLICT(StoryId, FileId) DO UPDATE SET Origin = 1, AddedAtUtc = excluded.AddedAtUtc WHERE Origin = -1;
                """;
            cmd.Parameters.AddWithValue("$s", storyId);
            cmd.Parameters.AddWithValue("$now", Now());
            var f = cmd.Parameters.Add("$f", SqliteType.Integer);
            var d = cmd.Parameters.Add("$d", SqliteType.Integer);
            var count = 0;
            foreach (var item in items)
            {
                f.Value = item.FileId;
                d.Value = item.Day;
                count += cmd.ExecuteNonQuery();
            }

            StorySql.RefreshAggregates(c, tx, [storyId], Now());
            return count;
        }, ct).ConfigureAwait(false);

        await EnsureCoverAsync(storyId, ct).ConfigureAwait(false);
        _logger.LogInformation("Story {Id}: {Count} Dateien hinzugefügt", storyId, added);
        Publish(scope.GroupId, StoriesChangeKind.Updated, storyId);
        return added;
    }

    public async Task<int> RemoveFilesAsync(int storyId, IReadOnlyList<int> fileIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        var scope = await RequireAsync(storyId, ct).ConfigureAwait(false);
        if (fileIds.Count == 0)
        {
            return 0;
        }

        var (removed, deleted) = await WriteAsync((c, tx) =>
        {
            var kind = (StoryKind)StorySql.Scalar(c, tx, "SELECT Kind FROM Stories WHERE Id = $id;", ("$id", storyId));
            var count = 0;
            foreach (var chunk in fileIds.Distinct().Chunk(ChunkSize))
            {
                // Eigene Storys: Eintrag weg. Automatische: Ausschluss, den keine Neuberechnung zurückholt.
                count += StorySql.Execute(c, tx, kind == StoryKind.Custom
                    ? $"DELETE FROM StoryItems WHERE StoryId = $id AND FileId IN ({StorySql.IdList(chunk)});"
                    : $"UPDATE StoryItems SET Origin = -1, AddedAtUtc = $now WHERE StoryId = $id AND Origin >= 0 AND FileId IN ({StorySql.IdList(chunk)});",
                    ("$id", storyId), ("$now", Now()));
            }

            StorySql.RefreshAggregates(c, tx, [storyId], Now());
            var empty = kind == StoryKind.Custom && StorySql.Scalar(c, tx, "SELECT FileCount FROM Stories WHERE Id = $id;", ("$id", storyId)) == 0;
            if (empty)
            {
                StoryPlanWriter.DeleteStory(c, tx, storyId);
            }

            return (count, empty);
        }, ct).ConfigureAwait(false);

        if (!deleted)
        {
            await EnsureCoverAsync(storyId, ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Story {Id}: {Count} Dateien entfernt", storyId, removed);
        Publish(scope.GroupId, deleted ? StoriesChangeKind.Deleted : StoriesChangeKind.Updated, storyId);
        return removed;
    }

    public async Task<int> CreateFromSelectionAsync(IReadOnlyList<int> fileIds, string? title = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        if (!scope.HasLibrary)
        {
            throw new InvalidOperationException("Ohne Bibliothek gibt es keine Storys.");
        }

        var text = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        if (text is { Length: > MaxTitleLength })
        {
            text = text[..MaxTitleLength].TrimEnd();
        }

        var zone = _clock.Zone;
        var id = await WriteAsync((c, tx) =>
        {
            var items = ReadFiles(c, tx, fileIds, scope.LibraryIds, zone);
            if (items.Count == 0)
            {
                throw new InvalidOperationException("Keine Fotos oder Videos dieser Bibliothek ausgewählt.");
            }

            var now = Now();
            var story = new DetectedStory
            {
                Kind = StoryKind.Custom,
                Signature = "C:" + Guid.NewGuid().ToString("N"),
                Items = items,
                PlaceIds = StoryDetector.SignificantPlaces(items, new StoryDetectionOptions()),
            };
            var newId = StoryPlanWriter.Insert(c, tx, scope.GroupId, story, now);
            StorySql.Execute(c, tx, "UPDATE Stories SET CustomTitle = $t, IsSeen = 1, SeenAtUtc = $now WHERE Id = $id;", ("$t", text), ("$now", now), ("$id", newId));
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO StoryItems (StoryId, FileId, Day, Origin, AddedAtUtc) VALUES ($s, $f, $d, 1, $now);";
            cmd.Parameters.AddWithValue("$s", newId);
            cmd.Parameters.AddWithValue("$now", now);
            var f = cmd.Parameters.Add("$f", SqliteType.Integer);
            var d = cmd.Parameters.Add("$d", SqliteType.Integer);
            foreach (var item in items)
            {
                f.Value = item.FileId;
                d.Value = item.Day;
                cmd.ExecuteNonQuery();
            }

            StorySql.RefreshAggregates(c, tx, [newId], now);
            return newId;
        }, ct).ConfigureAwait(false);

        await EnsureCoverAsync(id, ct).ConfigureAwait(false);
        _logger.LogInformation("Story aus Auswahl angelegt: Id {Id} ({Count} Dateien)", id, fileIds.Count);
        Publish(scope.GroupId, StoriesChangeKind.Created, id);
        return id;
    }

    public async Task DeleteAsync(int storyId, CancellationToken ct = default)
    {
        var scope = await RequireAsync(storyId, ct).ConfigureAwait(false);
        await WriteAsync((c, tx) =>
        {
            var kind = (StoryKind)StorySql.Scalar(c, tx, "SELECT Kind FROM Stories WHERE Id = $id;", ("$id", storyId));
            if (kind is not (StoryKind.Custom or StoryKind.Memory))
            {
                throw new InvalidOperationException("Automatisch erkannte Storys lassen sich nur ausblenden — sie entstünden sonst beim nächsten Neuberechnen wieder.");
            }

            StoryPlanWriter.DeleteStory(c, tx, storyId);
            return 0;
        }, ct).ConfigureAwait(false);
        Publish(scope.GroupId, StoriesChangeKind.Deleted, storyId);
    }

    public async Task<AlbumInfo> SaveAsAlbumAsync(int storyId, string? albumName = null, bool highlightsOnly = false, CancellationToken ct = default)
    {
        if (_albums is null)
        {
            throw new InvalidOperationException("Alben sind nicht verfügbar.");
        }

        var card = await GetAsync(storyId, ct).ConfigureAwait(false) ?? throw new StoryNotFoundException(storyId);
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        IReadOnlyList<int> files = highlightsOnly
            ? (await GetHighlightsAsync(storyId, ct).ConfigureAwait(false)).Select(h => h.FileId).ToList()
            : await ReadAsync(c => StorySql.ReadStoryFileIds(c, storyId, scope.LibraryIds), ct).ConfigureAwait(false);

        var name = (string.IsNullOrWhiteSpace(albumName) ? card.Title : albumName).Trim();
        if (name.Length > AlbumService.MaxNameLength - 6)
        {
            name = name[..(AlbumService.MaxNameLength - 6)].TrimEnd();
        }

        var candidate = name;
        for (var n = 2; await _albums.FindByNameAsync(candidate, ct).ConfigureAwait(false) is not null; n++)
        {
            candidate = $"{name} ({n})";
        }

        var album = await _albums.CreateAsync(candidate, ct).ConfigureAwait(false);
        await _albums.AddFilesAsync(album.Id, files, ct).ConfigureAwait(false);
        _logger.LogInformation("Story {Id} als Album {AlbumId} gespeichert ({Count} Dateien, Highlights: {Highlights})", storyId, album.Id, files.Count, highlightsOnly);
        return await _albums.GetAsync(album.Id, ct).ConfigureAwait(false) ?? album;
    }

    public async Task RecomputeAsync(int? storyId = null, CancellationToken ct = default)
    {
        var scope = storyId is { } id ? await RequireAsync(id, ct).ConfigureAwait(false) : await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        if (_index is null || scope.GroupId == 0)
        {
            return;
        }

        await _index.RunFullAsync(scope.GroupId, ct).ConfigureAwait(false);
    }

    // ---- Hilfen ----------------------------------------------------------------------------------------------------

    /// <summary>Bereich, in dessen Gruppe die Story liegt — sonst <see cref="StoryNotFoundException"/>.</summary>
    private async Task<LibraryScopeInfo> RequireAsync(int storyId, CancellationToken ct)
    {
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        var exists = scope.HasLibrary && await ReadAsync(c => InGroup(c, storyId, scope.GroupId), ct).ConfigureAwait(false);
        return exists ? scope : throw new StoryNotFoundException(storyId);
    }

    /// <summary>Automatisches Titelbild, falls die Story keins (mehr) hat.</summary>
    private async Task EnsureCoverAsync(int storyId, CancellationToken ct)
    {
        var files = await ReadAsync(c =>
            StorySql.Scalar(c, null, "SELECT COUNT(*) FROM Stories WHERE Id = $id AND CoverFileId IS NULL;", ("$id", storyId)) > 0
                ? StorySql.ReadStoryFileIds(c, storyId)
                : [], ct).ConfigureAwait(false);
        if (files.Count == 0 || await _selector.PickCoverAsync(files, ct).ConfigureAwait(false) is not { } cover)
        {
            return;
        }

        await WriteAsync((c, tx) => StorySql.Execute(c, tx,
            "UPDATE Stories SET CoverFileId = $f, CoverIsManual = 0 WHERE Id = $id AND CoverFileId IS NULL;", ("$f", cover), ("$id", storyId)), ct).ConfigureAwait(false);
    }

    /// <summary>Fotos/Videos aus den Bibliotheken als Zeitachsen-Einträge (für Tagesabschnitte und Orte).</summary>
    private static List<StoryTimelineItem> ReadFiles(SqliteConnection connection, SqliteTransaction? tx, IReadOnlyList<int> fileIds, IReadOnlyCollection<int> libraryIds, TimeZoneInfo zone)
    {
        var list = new List<StoryTimelineItem>(fileIds.Count);
        foreach (var chunk in fileIds.Distinct().Chunk(ChunkSize))
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                SELECT f.Id, {StorySql.TimeKey}, f.CapturedAtOffset, f.CapturedAtPrecision, f.Latitude, f.Longitude, f.PlaceId, f.MediaType
                FROM Files f WHERE f.Id IN ({StorySql.IdList(chunk)}) AND f.LibraryId IN ({StorySql.IdList(libraryIds)}) AND f.MediaType IN (0, 1);
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var time = reader.GetInt64(1);
                int? offset = reader.IsDBNull(2) ? null : reader.GetInt32(2);
                list.Add(new StoryTimelineItem(
                    reader.GetInt32(0), time, StorySql.LocalDay(time, offset, zone), (CaptureDatePrecision)reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetDouble(4), reader.IsDBNull(5) ? null : reader.GetDouble(5),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6), reader.GetInt32(7) == 1));
            }
        }

        list.Sort(static (x, y) => x.Time != y.Time ? x.Time.CompareTo(y.Time) : x.FileId.CompareTo(y.FileId));
        return list;
    }
}
