using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Thumbnails;

namespace PhotoViewerV3.Services.Duplicates;

/// <summary>
/// Abfragen der Doppelten (ADR-0053) — immer nur im übergebenen Bereich (ADR-0048: aktive Bibliothek bzw. alle der Gruppe
/// in „Alle verknüpften“; eine versteckte Bibliothek hat ihren eigenen Katalog, ADR-0049).
/// </summary>
public interface IDuplicateService
{
    /// <summary>Gruppen samt Mitgliedern in Vorschlags-Reihenfolge und Stand des Hintergrund-Index.</summary>
    Task<DuplicateResult> GetGroupsAsync(DuplicateQuery query, CancellationToken ct = default);

    /// <summary>Dateien im Eingang, die byte-gleich schon im Bibliotheksordner liegen.</summary>
    Task<int> CountInboxAlreadyInLibraryAsync(IReadOnlyList<int> libraryIds, CancellationToken ct = default);

    /// <summary>
    /// „Gibt es diese Prüfsumme schon?“ (für die Übertragung vom Handy, Welle 7): FileIds der Dateien mit genau diesem
    /// Inhalt (Größe + SHA-256) im Bereich. Gleich große Dateien ohne Prüfsumme werden dafür sofort gerechnet (nur lokale).
    /// </summary>
    Task<IReadOnlyList<int>> FindByContentAsync(IReadOnlyList<int> libraryIds, long size, byte[] sha256, CancellationToken ct = default);
}

/// <inheritdoc cref="IDuplicateService"/>
public sealed class DuplicateService : IDuplicateService
{
    private const int ChunkSize = 500;

    private readonly ILibraryContextFactory _factory;
    private readonly ILogger<DuplicateService> _logger;

    public DuplicateService(ILibraryContextFactory factory, ILogger<DuplicateService> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public Task<DuplicateResult> GetGroupsAsync(DuplicateQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Task.Run(() => GetGroups(query, ct), ct);
    }

    public Task<int> CountInboxAlreadyInLibraryAsync(IReadOnlyList<int> libraryIds, CancellationToken ct = default)
        => Task.Run(
            () =>
            {
                using var connection = _factory.OpenSqliteConnection();
                return DuplicateSql.CountInboxAlreadyInLibrary(connection, libraryIds.ToList());
            },
            ct);

    public Task<IReadOnlyList<int>> FindByContentAsync(IReadOnlyList<int> libraryIds, long size, byte[] sha256, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sha256);
        if (sha256.Length != 32)
        {
            throw new ArgumentException("SHA-256 hat 32 Bytes.", nameof(sha256));
        }

        return Task.Run<IReadOnlyList<int>>(
            async () =>
            {
                using var connection = _factory.OpenSqliteConnection();
                await HashMissingOfSizeAsync(connection, libraryIds, size, ct).ConfigureAwait(false);
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"""
                    SELECT f.Id FROM Files f JOIN FileFingerprints h ON h.FileId = f.Id
                    WHERE f.LibraryId IN ({DuplicateSql.IdList(libraryIds)}) AND f.Missing = 0 AND f.Size = $size
                      AND {DuplicateSql.ValidContent("h", "f")} AND h.ContentHash = $hash
                    ORDER BY f.Id;
                    """;
                cmd.Parameters.AddWithValue("$size", size);
                cmd.Parameters.AddWithValue("$hash", sha256);
                return ReadIds(cmd);
            },
            ct);
    }

    /// <summary>
    /// Gleich große Dateien des Bereichs ohne gültige Prüfsumme sofort rechnen (nur lokale, keine Cloud-Platzhalter) und in
    /// EINER Transaktion speichern (mit Wiederholung bei gesperrter DB).
    /// </summary>
    private async Task HashMissingOfSizeAsync(SqliteConnection connection, IReadOnlyList<int> libraryIds, long size, CancellationToken ct)
    {
        var pending = new List<(int Id, string Path, long Modified)>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT f.Id, d.Path, f.Name, f.ModifiedAtUtc FROM Files f
                JOIN Folders d ON d.Id = f.FolderId
                LEFT JOIN FileFingerprints h ON h.FileId = f.Id
                WHERE f.LibraryId IN ({DuplicateSql.IdList(libraryIds)}) AND f.Missing = 0 AND f.Size = $size
                  AND (h.FileId IS NULL OR h.ContentSize IS NOT f.Size OR h.ContentModifiedAt IS NOT f.ModifiedAtUtc);
                """;
            cmd.Parameters.AddWithValue("$size", size);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                pending.Add((reader.GetInt32(0), Path.Combine(reader.GetString(1), reader.GetString(2)), reader.GetInt64(3)));
            }
        }

        var hashed = new List<(int Id, byte[] Hash, long Modified)>();
        foreach (var (id, path, modified) in pending)
        {
            ct.ThrowIfCancellationRequested();
            if (!ThumbnailService.IsLocallyAvailable(path))
            {
                continue;
            }

            byte[] hash;
            try
            {
                hash = ContentHasher.Compute(path, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Doppelte: Prüfsumme für FileId {FileId} nicht lesbar", id);
                continue;
            }

            hashed.Add((id, hash, modified));
        }

        if (hashed.Count == 0)
        {
            return;
        }

        using var tx = await _factory.BusyRetry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
        foreach (var (id, hash, modified) in hashed)
        {
            using var write = connection.CreateCommand();
            write.Transaction = tx;
            write.CommandText = """
                INSERT INTO FileFingerprints(FileId, ContentHash, ContentSize, ContentModifiedAt)
                SELECT $id, $hash, $size, $mod
                WHERE EXISTS (SELECT 1 FROM Files WHERE Id = $id AND Size = $size AND ModifiedAtUtc = $mod)
                ON CONFLICT(FileId) DO UPDATE SET ContentHash = excluded.ContentHash, ContentSize = excluded.ContentSize,
                                                  ContentModifiedAt = excluded.ContentModifiedAt;
                """;
            write.Parameters.AddWithValue("$id", id);
            write.Parameters.AddWithValue("$hash", hash);
            write.Parameters.AddWithValue("$size", size);
            write.Parameters.AddWithValue("$mod", modified);
            write.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private DuplicateResult GetGroups(DuplicateQuery query, CancellationToken ct)
    {
        using var connection = _factory.OpenSqliteConnection();
        var ids = DuplicateSql.IdList(query.LibraryIds);
        var rawGroups = query.Mode == DuplicateMode.Exact
            ? LoadExactGroups(connection, ids)
            : LoadSimilarGroups(connection, ids, Math.Clamp(query.MaxDistance, 0, DuplicateSettings.MaxThreshold), ct);

        var allIds = rawGroups.SelectMany(g => g.Select(e => e.FileId)).Distinct().ToList();
        var members = LoadMembers(connection, allIds, ct);
        var groups = new List<DuplicateGroup>(rawGroups.Count);
        foreach (var raw in rawGroups)
        {
            var entries = raw.Where(e => members.ContainsKey(e.FileId)).ToDictionary(e => e.FileId);
            var list = entries.Keys.Select(id => members[id]).ToList();
            if (list.Count < 2)
            {
                continue;
            }

            if (query.InboxOnly && !(list.Any(m => m.IsInInbox) && list.Any(m => !m.IsInInbox)))
            {
                continue;
            }

            // Abstand und „byte-gleich“ jeweils zum Vorschlag „behalten“
            var keeper = entries[KeeperRule.Order(list)[0].FileId];
            var ordered = KeeperRule.Order(list.Select(m => m with
            {
                Distance = entries[m.FileId].Image is { } h && keeper.Image is { } k ? PerceptualHash.Distance(h, k) : 0,
                IsExactCopy = m.FileId != keeper.FileId && entries[m.FileId].Content is { } a && keeper.Content is { } b && a.AsSpan().SequenceEqual(b),
            }));
            var key = (query.Mode == DuplicateMode.Exact ? "E" : "S") + raw[0].FileId.ToString(CultureInfo.InvariantCulture);
            // Begründung gegenüber der nächsten Datei, die nicht byte-gleich ist (bei „ähnlich“ sagt „gleichwertig“ zur eigenen Kopie wenig)
            var compareWith = ordered.Skip(1).FirstOrDefault(m => !m.IsExactCopy) ?? ordered[1];
            groups.Add(new DuplicateGroup(key, query.Mode, ordered, ordered[0].FileId, KeeperRule.Reason(ordered[0], compareWith)));
        }

        // größte Ersparnis zuerst, dann stabil nach der ältesten Datei
        groups = groups.OrderByDescending(g => g.Members.Count).ThenByDescending(g => g.RedundantBytes).ThenBy(g => g.Members.Min(m => m.FileId)).ToList();
        var (pendingContent, pendingImages) = CountPending(connection, ids);
        return new DuplicateResult(groups, pendingContent, pendingImages);
    }

    private readonly record struct RawEntry(int FileId, ulong? Image, byte[]? Content);

    /// <summary>Gleiche gültige Prüfsumme im Bereich (über den Index nach Prüfsumme sortiert).</summary>
    private static List<List<RawEntry>> LoadExactGroups(SqliteConnection connection, string ids)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT h.ContentHash, f.Id FROM FileFingerprints h JOIN Files f ON f.Id = h.FileId
            WHERE f.LibraryId IN ({ids}) AND f.Missing = 0 AND {DuplicateSql.ValidContent("h", "f")}
            ORDER BY h.ContentHash, f.Id;
            """;
        var groups = new List<List<RawEntry>>();
        byte[]? last = null;
        List<RawEntry>? current = null;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var hash = (byte[])reader.GetValue(0);
            var id = reader.GetInt32(1);
            if (last is null || !hash.AsSpan().SequenceEqual(last))
            {
                if (current is { Count: > 1 })
                {
                    groups.Add(current);
                }

                current = new List<RawEntry>();
                last = hash;
            }

            current!.Add(new RawEntry(id, null, hash));
        }

        if (current is { Count: > 1 })
        {
            groups.Add(current);
        }

        return groups;
    }

    /// <summary>pHash-Gruppen (Stern-Verfahren, ältere Dateien als Mittelpunkt zuerst).</summary>
    private static List<List<RawEntry>> LoadSimilarGroups(SqliteConnection connection, string ids, int maxDistance, CancellationToken ct)
    {
        var fileIds = new List<int>();
        var hashes = new List<ulong>();
        var contents = new List<byte[]?>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT f.Id, h.ImageHash, CASE WHEN {DuplicateSql.ValidContent("h", "f")} THEN h.ContentHash END
                FROM FileFingerprints h JOIN Files f ON f.Id = h.FileId
                WHERE f.LibraryId IN ({ids}) AND f.Missing = 0 AND {DuplicateSql.ValidImage("h", "f")}
                ORDER BY f.Id;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                fileIds.Add(reader.GetInt32(0));
                hashes.Add(PerceptualHash.FromStored(reader.GetInt64(1)));
                contents.Add(reader.IsDBNull(2) ? null : (byte[])reader.GetValue(2));
            }
        }

        var groups = new List<List<RawEntry>>();
        foreach (var indices in SimilarGrouping.Group(hashes, maxDistance, ct))
        {
            groups.Add(indices.Select(i => new RawEntry(fileIds[i], hashes[i], contents[i])).ToList());
        }

        return groups;
    }

    /// <summary>Was der Hintergrund-Index im Bereich noch vor sich hat (Hinweis „Ergebnis noch unvollständig“).</summary>
    private static (int Content, int Images) CountPending(SqliteConnection connection, string ids)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT
              (SELECT COUNT(*) FROM Files f LEFT JOIN FileFingerprints h ON h.FileId = f.Id
               WHERE f.LibraryId IN ({ids}) AND f.Missing = 0 AND f.Size > 0
                 AND (h.FileId IS NULL OR h.ContentSize IS NOT f.Size OR h.ContentModifiedAt IS NOT f.ModifiedAtUtc)
                 AND f.Size IN (SELECT Size FROM Files WHERE Missing = 0 AND Size > 0 GROUP BY Size HAVING COUNT(*) > 1)),
              (SELECT COUNT(*) FROM Files f LEFT JOIN FileFingerprints h ON h.FileId = f.Id
               WHERE f.LibraryId IN ({ids}) AND f.Missing = 0 AND f.MediaType = 0
                 AND (h.FileId IS NULL OR h.ImageHashVersion IS NOT {PerceptualHash.Version.ToString(CultureInfo.InvariantCulture)}
                      OR h.ImageModifiedAt IS NOT f.ModifiedAtUtc));
            """;
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? (reader.GetInt32(0), reader.GetInt32(1)) : (0, 0);
    }

    private static Dictionary<int, DuplicateMember> LoadMembers(SqliteConnection connection, IReadOnlyList<int> fileIds, CancellationToken ct)
    {
        var result = new Dictionary<int, DuplicateMember>(fileIds.Count);
        foreach (var chunk in fileIds.Chunk(ChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT f.Id, f.LibraryId, l.Name, f.Name, d.Path, f.MediaType, f.Size, f.Width, f.Height,
                       f.CapturedAt, f.CapturedAtSource, f.CapturedAtOffset, f.Rating, f.IsFavorite,
                       (SELECT COUNT(*) FROM FileTags t WHERE t.FileId = f.Id),
                       (SELECT COUNT(*) FROM FileCategoryValues c WHERE c.FileId = f.Id),
                       (SELECT COUNT(DISTINCT x.PersonId) FROM Faces x WHERE x.FileId = f.Id AND x.PersonId IS NOT NULL),
                       (SELECT r.Role FROM LibraryRoots r WHERE r.Id = d.RootId),
                       f.ModifiedAtUtc,
                       (SELECT t.CachePath FROM Thumbnails t WHERE t.FileId = f.Id AND t.Status = 2 ORDER BY t.Size DESC LIMIT 1)
                FROM Files f
                JOIN Folders d ON d.Id = f.FolderId
                JOIN Libraries l ON l.Id = f.LibraryId
                WHERE f.Id IN ({DuplicateSql.IdList(chunk)});
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var member = new DuplicateMember(
                    FileId: reader.GetInt32(0),
                    LibraryId: reader.GetInt32(1),
                    LibraryName: reader.GetString(2),
                    Name: reader.GetString(3),
                    FolderPath: reader.GetString(4),
                    MediaType: (MediaType)reader.GetInt32(5),
                    Size: reader.GetInt64(6),
                    Width: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    Height: reader.IsDBNull(8) ? null : reader.GetInt32(8),
                    CapturedAtUtc: reader.IsDBNull(9) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(9)).UtcDateTime,
                    CapturedAtSource: reader.IsDBNull(10) ? null : (CaptureDateSource)reader.GetInt32(10),
                    CapturedAtOffsetMinutes: reader.IsDBNull(11) ? null : reader.GetInt32(11),
                    Rating: reader.IsDBNull(12) ? null : (byte)reader.GetInt32(12),
                    IsFavorite: reader.GetBoolean(13),
                    TagCount: reader.GetInt32(14),
                    CategoryCount: reader.GetInt32(15),
                    PersonCount: reader.GetInt32(16),
                    IsInInbox: !reader.IsDBNull(17) && reader.GetInt32(17) == (int)LibraryRootRole.Inbox,
                    ModifiedAtUtc: DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(18)).UtcDateTime,
                    ThumbnailPath: reader.IsDBNull(19) ? null : reader.GetString(19));
                result[member.FileId] = member;
            }
        }

        return result;
    }

    private static IReadOnlyList<int> ReadIds(SqliteCommand cmd)
    {
        var ids = new List<int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetInt32(0));
        }

        return ids;
    }
}
