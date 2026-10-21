using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;
using Lumora.Services.Metadata;
using Lumora.Services.Watch;

namespace Lumora.Services.Places;

/// <summary>
/// Rückgängig für „Ort setzen/entfernen“ (ADR-0050): je Datei die alten Bytes zurück (JPEG: altes APP1-Segment an
/// derselben Stelle, Änderungszeit wie vorher — die Datei ist danach Byte für Byte die alte; XMP: alte Begleitdatei bzw.
/// die von Lumora angelegte in den Papierkorb) und die alten Katalogwerte. Nur wenn die Datei seit dem Setzen unverändert
/// ist (SHA-256 des geschriebenen Stands) — sonst bleibt sie, wie sie ist, und das Ergebnis nennt sie.
/// </summary>
public sealed partial class GeoLocationService
{
    private sealed record UndoEntry(
        long OperationId, int? FileId, string SourcePath, double? OldLatitude, double? OldLongitude, int? OldSource, int? OldPlaceId,
        GeoTagWriteKind Kind, string? WrittenPath, byte[]? OldBytes, byte[]? WrittenHash, long? SegmentOffset, long? OldModifiedTicks, long? OldSize);

    public async Task<int?> GetUndoableBatchIdAsync(CancellationToken ct = default)
    {
        await using var ctx = _factory.Create();
        return await ctx.OperationBatches.AsNoTracking()
            .Where(b => b.OperationType == OperationType.SetGeoLocation && b.Status == BatchStatus.Applied)
            .OrderByDescending(b => b.Id)
            .Select(b => (int?)b.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<GeoTagUndoResult?> UndoAsync(int batchId, CancellationToken ct = default)
    {
        var (description, entries) = await Task.Run(() => LoadUndo(batchId), ct).ConfigureAwait(false);
        if (description is null)
        {
            return null;
        }

        var targets = await Task.Run(() => ResolveTargets(entries), ct).ConfigureAwait(false);
        var errors = new List<string>();
        var restoredOps = new List<(UndoEntry Entry, int FileId, int LibraryId)>();
        var failedOps = new List<(UndoEntry Entry, string Error)>();
        var leases = await AcquireLibrariesAsync(targets.Values.Select(t => t.LibraryId), ct).ConfigureAwait(false);
        try
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var scope = _ownChanges?.Begin();
                await Task.Run(
                    () =>
                    {
                        foreach (var entry in entries)
                        {
                            var name = Path.GetFileName(entry.SourcePath);
                            if (entry.Kind != GeoTagWriteKind.CatalogOnly
                                && RestoreFile(new GeoFileWrite(entry.Kind, entry.WrittenPath, entry.OldBytes, entry.WrittenHash, entry.SegmentOffset, entry.OldModifiedTicks, entry.OldSize), scope) is { } error)
                            {
                                failedOps.Add((entry, error));
                                errors.Add($"{name}: {error}");
                                continue;
                            }

                            if (targets.TryGetValue(entry.OperationId, out var target))
                            {
                                restoredOps.Add((entry, target.FileId, target.LibraryId));
                            }
                            else
                            {
                                restoredOps.Add((entry, 0, 0)); // Datei nicht mehr im Katalog: Datei zurück, Katalog entfällt
                            }
                        }
                    },
                    CancellationToken.None).ConfigureAwait(false);

                await Task.Run(
                    async () =>
                    {
                        using var connection = _factory.OpenSqliteConnection();
                        using var tx = await _factory.BusyRetry.BeginTransactionAsync(connection, CancellationToken.None).ConfigureAwait(false);
                        WriteUndo(connection, tx, batchId, restoredOps, failedOps);
                        tx.Commit();
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        finally
        {
            Release(leases);
        }

        var restored = restoredOps.Count;
        _logger.LogInformation("Ort setzen rückgängig: Batch {BatchId} — {Restored} zurückgesetzt, {Failed} nicht möglich", batchId, restored, failedOps.Count);
        var byLibrary = new Dictionary<int, List<int>>();
        foreach (var (_, fileId, libraryId) in restoredOps.Where(r => r.FileId > 0))
        {
            AddTo(byLibrary, libraryId, fileId);
        }

        Publish(byLibrary);
        return new GeoTagUndoResult(batchId, restored, failedOps.Count, errors, description);
    }

    /// <summary>Datei auf den Stand vor dem Schreiben (<see cref="GeoFileWriter.Restore"/>); null bei Erfolg, sonst der Grund.</summary>
    private string? RestoreFile(GeoFileWrite write, IOwnChangeScope? scope)
        => GeoFileWriter.Restore(write, scope, _recycleBin is { } bin ? bin.MoveToRecycleBin : null);

    private (string? Description, List<UndoEntry> Entries) LoadUndo(int batchId)
    {
        using var connection = _factory.OpenSqliteConnection();
        string? description;
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT Description FROM OperationBatches WHERE Id = $b AND OperationType = $type AND Status = $applied;";
            check.Parameters.AddWithValue("$b", batchId);
            check.Parameters.AddWithValue("$type", (int)OperationType.SetGeoLocation);
            check.Parameters.AddWithValue("$applied", (int)BatchStatus.Applied);
            var value = check.ExecuteScalar();
            if (value is null)
            {
                return (null, []);
            }

            description = value as string ?? "Ort setzen";
        }

        var entries = new List<UndoEntry>();
        using var read = connection.CreateCommand();
        read.CommandText = """
            SELECT o.Id, o.FileId, o.SourcePath, u.OldLatitude, u.OldLongitude, u.OldGeoSource, u.OldPlaceId,
                   u.WriteKind, u.WrittenPath, u.OldBytes, u.WrittenHash, u.SegmentOffset, u.OldModifiedTicks, u.OldSize
            FROM FileOperations o JOIN GeoTagUndo u ON u.FileOperationId = o.Id
            WHERE o.BatchId = $b AND o.Status = $applied
            ORDER BY o.Id DESC;
            """;
        read.Parameters.AddWithValue("$b", batchId);
        read.Parameters.AddWithValue("$applied", (int)FileOperationStatus.Applied);
        using var reader = read.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new UndoEntry(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetDouble(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                (GeoTagWriteKind)reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : (byte[])reader.GetValue(9),
                reader.IsDBNull(10) ? null : (byte[])reader.GetValue(10),
                reader.IsDBNull(11) ? null : reader.GetInt64(11),
                reader.IsDBNull(12) ? null : reader.GetInt64(12),
                reader.IsDBNull(13) ? null : reader.GetInt64(13)));
        }

        return (description, entries);
    }

    /// <summary>
    /// Aktuelle Katalogzeile je Operation: FileId mit gleichem Pfad, sonst Ordnerpfad + Name (nach „Neu aufbauen“), sonst
    /// die FileId allein (verschoben/umbenannt — FileIds bleiben stabil).
    /// </summary>
    private Dictionary<long, (int FileId, int LibraryId)> ResolveTargets(IReadOnlyList<UndoEntry> entries)
    {
        var result = new Dictionary<long, (int, int)>();
        using var connection = _factory.OpenSqliteConnection();
        using var byId = connection.CreateCommand();
        byId.CommandText = "SELECT f.Id, f.LibraryId FROM Files f JOIN Folders d ON d.Id = f.FolderId WHERE f.Id = $id AND d.Path = $dir AND f.Name = $name;";
        var pId = byId.Parameters.Add("$id", SqliteType.Integer);
        var pDir = byId.Parameters.Add("$dir", SqliteType.Text);
        var pName = byId.Parameters.Add("$name", SqliteType.Text);
        using var idOnly = connection.CreateCommand();
        idOnly.CommandText = "SELECT Id, LibraryId FROM Files WHERE Id = $id;";
        var oId = idOnly.Parameters.Add("$id", SqliteType.Integer);
        using var byPath = connection.CreateCommand();
        byPath.CommandText = "SELECT f.Id, f.LibraryId FROM Files f JOIN Folders d ON d.Id = f.FolderId WHERE d.Path = $dir AND f.Name = $name LIMIT 1;";
        var qDir = byPath.Parameters.Add("$dir", SqliteType.Text);
        var qName = byPath.Parameters.Add("$name", SqliteType.Text);
        foreach (var entry in entries)
        {
            var dir = Path.GetDirectoryName(entry.SourcePath) ?? string.Empty;
            var name = Path.GetFileName(entry.SourcePath);
            if (entry.FileId is { } id)
            {
                pId.Value = id;
                pDir.Value = dir;
                pName.Value = name;
                using var reader = byId.ExecuteReader();
                if (reader.Read())
                {
                    result[entry.OperationId] = (reader.GetInt32(0), reader.GetInt32(1));
                    continue;
                }
            }

            qDir.Value = dir;
            qName.Value = name;
            using (var reader = byPath.ExecuteReader())
            {
                if (reader.Read())
                {
                    result[entry.OperationId] = (reader.GetInt32(0), reader.GetInt32(1));
                    continue;
                }
            }

            if (entry.FileId is { } movedId)
            {
                oId.Value = movedId;
                using var reader = idOnly.ExecuteReader();
                if (reader.Read())
                {
                    result[entry.OperationId] = (reader.GetInt32(0), reader.GetInt32(1));
                }
            }
        }

        return result;
    }

    private static void WriteUndo(
        SqliteConnection connection,
        SqliteTransaction tx,
        int batchId,
        IReadOnlyList<(UndoEntry Entry, int FileId, int LibraryId)> restored,
        IReadOnlyList<(UndoEntry Entry, string Error)> failed)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var geo = CreateGeoUpdate(connection, tx);
        using var size = connection.CreateCommand();
        size.Transaction = tx;
        size.CommandText = "UPDATE Files SET Size = $size, ModifiedAtUtc = $modified WHERE Id = $id;";
        var sSize = size.Parameters.Add("$size", SqliteType.Integer);
        var sModified = size.Parameters.Add("$modified", SqliteType.Integer);
        var sId = size.Parameters.Add("$id", SqliteType.Integer);
        using var mark = connection.CreateCommand();
        mark.Transaction = tx;
        mark.CommandText = "UPDATE FileOperations SET Status = $status, UndoneAtUtc = $now, Error = $error WHERE Id = $op;";
        var mStatus = mark.Parameters.Add("$status", SqliteType.Integer);
        mark.Parameters.AddWithValue("$now", now);
        var mError = mark.Parameters.Add("$error", SqliteType.Text);
        var mOp = mark.Parameters.Add("$op", SqliteType.Integer);

        foreach (var (entry, fileId, _) in restored)
        {
            if (fileId > 0)
            {
                SetGeoUpdate(geo, fileId, entry.OldLatitude, entry.OldLongitude, entry.OldSource, entry.OldPlaceId);
                geo.ExecuteNonQuery();
                if (entry.Kind == GeoTagWriteKind.JpegExif && entry.OldSize is { } oldSize && entry.OldModifiedTicks is { } oldTicks)
                {
                    sSize.Value = oldSize;
                    sModified.Value = GeoFileWriter.ToUnix(new DateTime(oldTicks, DateTimeKind.Utc));
                    sId.Value = fileId;
                    size.ExecuteNonQuery();
                }
            }

            mStatus.Value = (int)FileOperationStatus.Undone;
            mError.Value = DBNull.Value;
            mOp.Value = entry.OperationId;
            mark.ExecuteNonQuery();
        }

        foreach (var (entry, error) in failed)
        {
            // Nicht zurückgesetzt (Datei inzwischen verändert): als fehlgeschlagen vermerken, damit „Rückgängig“ weitergeht.
            mStatus.Value = (int)FileOperationStatus.Failed;
            mError.Value = "Rückgängig: " + error;
            mOp.Value = entry.OperationId;
            mark.ExecuteNonQuery();
        }

        using var batch = connection.CreateCommand();
        batch.Transaction = tx;
        batch.CommandText = "UPDATE OperationBatches SET Status = $undone, CompletedAtUtc = $now WHERE Id = $b;";
        batch.Parameters.AddWithValue("$undone", (int)BatchStatus.Undone);
        batch.Parameters.AddWithValue("$now", now);
        batch.Parameters.AddWithValue("$b", batchId);
        batch.ExecuteNonQuery();
    }
}
