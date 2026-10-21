using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;
using Lumora.Services.Catalog;
using Lumora.Services.Metadata;

namespace Lumora.Services.Places;

/// <summary>
/// „Ort setzen…“ und „Ort entfernen“ (ADR-0050): erst die Dateien schreiben (je Datei Temp-Datei + Ersetzen, unter der
/// Scan-Sperre der Bibliotheken, angemeldet bei der Ordnerüberwachung), dann in EINER Transaktion ein
/// <c>OperationBatch</c> (<see cref="OperationType.SetGeoLocation"/>) mit je Datei einer <c>FileOperation</c> und einer
/// <c>GeoTagUndo</c>-Zeile (alte Werte, alte Bytes) plus die neuen Katalogwerte. Scheitert die Transaktion, werden die
/// schon geschriebenen Dateien zurückgesetzt.
/// </summary>
public sealed partial class GeoLocationService
{
    private sealed record TargetRow(int Id, int LibraryId, string Path, int MediaType, bool Missing, double? Latitude, double? Longitude, int? Source, int? PlaceId);

    private sealed record Change(TargetRow Row, GeoFileWrite Write);

    public Task<GeoTagResult> SetLocationAsync(IReadOnlyCollection<int> fileIds, double latitude, double longitude, bool keepModifiedTime, CancellationToken ct)
    {
        var coordinate = new GeoCoordinate(latitude, longitude);
        if (!coordinate.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(latitude), "Ungültige Koordinate (Breite −90…90, Länge −180…180, nicht 0/0).");
        }

        return ApplyAsync(fileIds, coordinate, keepModifiedTime, ct);
    }

    public Task<GeoTagResult> ClearLocationAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct)
        => ApplyAsync(fileIds, null, keepModifiedTime: true, ct);

    private async Task<GeoTagResult> ApplyAsync(IReadOnlyCollection<int> fileIds, GeoCoordinate? value, bool keepModifiedTime, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        var ids = fileIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new GeoTagResult(0, 0, 0, []);
        }

        // Bibliotheken für die Scan-Sperre; nach dem Belegen frisch gelesen (inzwischen verschoben/nachgetragen?).
        var libraries = (await Task.Run(() => LoadTargets(ids), ct).ConfigureAwait(false)).Select(r => r.LibraryId).Distinct().ToList();
        var place = value is { } v ? _geocoder.Nearest(v.Latitude, v.Longitude) : null;
        var errors = new List<string>();
        var changes = new List<Change>();
        var unchanged = 0;
        var leases = await AcquireLibrariesAsync(libraries, ct).ConfigureAwait(false);
        try
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var rows = await Task.Run(() => LoadTargets(ids), ct).ConfigureAwait(false);
                using var scope = _ownChanges?.Begin();
                await Task.Run(
                    () =>
                    {
                        foreach (var row in rows)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (value is null && row.Latitude is null && row.Source is (int)GeoSource.Manual or (int)GeoSource.None)
                            {
                                unchanged++; // nichts zu entfernen
                                continue;
                            }

                            if (row.Missing)
                            {
                                errors.Add($"{Path.GetFileName(row.Path)}: Datei fehlt.");
                                continue;
                            }

                            try
                            {
                                changes.Add(new Change(row, GeoFileWriter.Write(row.Path, row.MediaType, value, keepModifiedTime, scope)));
                            }
                            catch (GeoWriteException ex)
                            {
                                errors.Add($"{Path.GetFileName(row.Path)}: {ex.Message}");
                                _logger.LogDebug("Ort setzen: Datei {FileId} nicht geschrieben — {Reason}", row.Id, ex.Message);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                // Unerwartet, aber vor dem Ersetzen (GeoFileWriter wirft danach nicht mehr): Datei unverändert.
                                errors.Add($"{Path.GetFileName(row.Path)}: {GeoFileWriter.DescribeIo(ex)}");
                                _logger.LogWarning("Ort setzen: Datei {FileId} nicht geschrieben ({Type}, 0x{HResult:X8})", row.Id, ex.GetType().Name, ex.HResult); // ohne Meldungstext: enthält Pfade
                            }
                        }
                    },
                    ct).ConfigureAwait(false);

                if (changes.Count > 0)
                {
                    await WriteCatalogAsync(changes, value, place, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (OperationCanceledException) when (changes.Count > 0)
        {
            // Abbruch nach geschriebenen Dateien: Dateien zurück, damit Katalog und Dateien zusammenpassen.
            RollBackFiles(changes);
            throw;
        }
        finally
        {
            Release(leases);
        }

        var written = changes.Count(c => c.Write.Kind != GeoTagWriteKind.CatalogOnly);
        var catalogOnly = changes.Count - written + unchanged;
        var verb = value is null ? "Ort entfernen" : "Ort setzen";
        _logger.LogInformation("{Verb}: {Written} Datei(en) geschrieben, {CatalogOnly} nur Katalog, {Failed} fehlgeschlagen", verb, written, catalogOnly, errors.Count);

        var byLibrary = new Dictionary<int, List<int>>();
        foreach (var change in changes)
        {
            AddTo(byLibrary, change.Row.LibraryId, change.Row.Id);
        }

        Publish(byLibrary);
        return new GeoTagResult(written, catalogOnly, errors.Count, errors);
    }

    private async Task WriteCatalogAsync(IReadOnlyList<Change> changes, GeoCoordinate? value, PlaceInfo? place, CancellationToken ct)
    {
        try
        {
            await Task.Run(
                async () =>
                {
                    using var connection = _factory.OpenSqliteConnection();
                    using var tx = await _factory.BusyRetry.BeginTransactionAsync(connection, CancellationToken.None).ConfigureAwait(false);
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var description = value is { } v
                        ? $"Ort setzen: {PlaceText.Short(place) ?? v.DisplayText} ({changes.Count} Datei(en))"
                        : $"Ort entfernen ({changes.Count} Datei(en))";
                    var batchId = InsertBatch(connection, tx, now, description);
                    WriteChanges(connection, tx, batchId, now, changes, value, place?.PlaceId);
                    tx.Commit();
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ort setzen: Katalog konnte nicht geschrieben werden — {Count} Datei(en) werden zurückgesetzt", changes.Count);
            RollBackFiles(changes);
            throw;
        }
    }

    private static int InsertBatch(SqliteConnection connection, SqliteTransaction tx, long now, string description)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO OperationBatches (OperationType, Status, CreatedAtUtc, CompletedAtUtc, PlanId, Description)
            VALUES ($type, $status, $now, $now, NULL, $description);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$type", (int)OperationType.SetGeoLocation);
        cmd.Parameters.AddWithValue("$status", (int)BatchStatus.Applied);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$description", description);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Je Datei: FileOperation (Applied) + GeoTagUndo + neue Katalogwerte (bei JPEG auch Größe/Änderungszeit).</summary>
    private static void WriteChanges(SqliteConnection connection, SqliteTransaction tx, int batchId, long now, IReadOnlyList<Change> changes, GeoCoordinate? value, int? placeId)
    {
        using var op = connection.CreateCommand();
        op.Transaction = tx;
        op.CommandText = """
            INSERT INTO FileOperations (BatchId, FileId, Action, SourcePath, DestPath, Status, Error, ExecutedAtUtc, UndoneAtUtc)
            VALUES ($batch, $file, $action, $path, $dest, $status, NULL, $now, NULL);
            SELECT last_insert_rowid();
            """;
        op.Parameters.AddWithValue("$batch", batchId);
        var pFile = op.Parameters.Add("$file", SqliteType.Integer);
        op.Parameters.AddWithValue("$action", (int)OperationType.SetGeoLocation);
        var pPath = op.Parameters.Add("$path", SqliteType.Text);
        var pDest = op.Parameters.Add("$dest", SqliteType.Text);
        op.Parameters.AddWithValue("$status", (int)FileOperationStatus.Applied);
        op.Parameters.AddWithValue("$now", now);

        using var undo = connection.CreateCommand();
        undo.Transaction = tx;
        undo.CommandText = """
            INSERT INTO GeoTagUndo (FileOperationId, OldLatitude, OldLongitude, OldGeoSource, OldPlaceId, NewLatitude, NewLongitude,
                                    WriteKind, WrittenPath, OldBytes, WrittenHash, SegmentOffset, OldModifiedTicks, OldSize)
            VALUES ($op, $oldLat, $oldLon, $oldSource, $oldPlace, $newLat, $newLon, $kind, $written, $oldBytes, $hash, $offset, $oldModified, $oldSize);
            """;
        var u = new[] { "$op", "$oldLat", "$oldLon", "$oldSource", "$oldPlace", "$newLat", "$newLon", "$kind", "$written", "$oldBytes", "$hash", "$offset", "$oldModified", "$oldSize" }
            .ToDictionary(n => n, n => undo.Parameters.Add(n, n is "$oldLat" or "$oldLon" or "$newLat" or "$newLon" ? SqliteType.Real : n is "$written" ? SqliteType.Text : n is "$oldBytes" or "$hash" ? SqliteType.Blob : SqliteType.Integer));

        using var geo = CreateGeoUpdate(connection, tx);
        using var size = connection.CreateCommand();
        size.Transaction = tx;
        size.CommandText = "UPDATE Files SET Size = $size, ModifiedAtUtc = $modified WHERE Id = $id;";
        var sSize = size.Parameters.Add("$size", SqliteType.Integer);
        var sModified = size.Parameters.Add("$modified", SqliteType.Integer);
        var sId = size.Parameters.Add("$id", SqliteType.Integer);

        foreach (var (row, write) in changes)
        {
            pFile.Value = row.Id;
            pPath.Value = row.Path;
            pDest.Value = (object?)write.WrittenPath ?? DBNull.Value;
            var opId = Convert.ToInt64(op.ExecuteScalar(), CultureInfo.InvariantCulture);

            u["$op"].Value = opId;
            u["$oldLat"].Value = (object?)row.Latitude ?? DBNull.Value;
            u["$oldLon"].Value = (object?)row.Longitude ?? DBNull.Value;
            u["$oldSource"].Value = (object?)row.Source ?? DBNull.Value;
            u["$oldPlace"].Value = (object?)row.PlaceId ?? DBNull.Value;
            u["$newLat"].Value = (object?)value?.Latitude ?? DBNull.Value;
            u["$newLon"].Value = (object?)value?.Longitude ?? DBNull.Value;
            u["$kind"].Value = (int)write.Kind;
            u["$written"].Value = (object?)write.WrittenPath ?? DBNull.Value;
            u["$oldBytes"].Value = (object?)write.OldBytes ?? DBNull.Value;
            u["$hash"].Value = (object?)write.WrittenHash ?? DBNull.Value;
            u["$offset"].Value = (object?)write.SegmentOffset ?? DBNull.Value;
            u["$oldModified"].Value = (object?)write.OldModifiedTicks ?? DBNull.Value;
            u["$oldSize"].Value = (object?)write.OldSize ?? DBNull.Value;
            undo.ExecuteNonQuery();

            SetGeoUpdate(geo, row.Id, value?.Latitude, value?.Longitude, (int)GeoSource.Manual, value is null ? null : placeId);
            geo.ExecuteNonQuery();

            if (write.Kind == GeoTagWriteKind.JpegExif && write.NewSize is { } newSize && write.NewModifiedAt is { } newModified)
            {
                // Katalog kennt den neuen Stand der Datei — der nächste Incremental-Scan sieht keine Änderung.
                sSize.Value = newSize;
                sModified.Value = newModified;
                sId.Value = row.Id;
                size.ExecuteNonQuery();
            }
        }
    }

    /// <summary>Schon geschriebene Dateien auf den alten Stand (Katalog-Transaktion gescheitert bzw. abgebrochen).</summary>
    private void RollBackFiles(IReadOnlyList<Change> changes)
    {
        using var scope = _ownChanges?.Begin();
        foreach (var (row, write) in changes)
        {
            if (write.Kind == GeoTagWriteKind.CatalogOnly)
            {
                continue;
            }

            if (RestoreFile(write, scope) is { } error)
            {
                _logger.LogWarning("Ort setzen: Datei {FileId} konnte nicht zurückgesetzt werden — {Reason}", row.Id, error);
            }
        }
    }

    private List<TargetRow> LoadTargets(IReadOnlyList<int> ids)
    {
        using var connection = _factory.OpenSqliteConnection();
        var rows = new List<TargetRow>(ids.Count);
        foreach (var chunk in ids.Chunk(ChunkSize))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT f.Id, f.LibraryId, d.Path, f.Name, f.MediaType, f.Missing, f.Latitude, f.Longitude, f.GeoSource, f.PlaceId " +
                $"FROM Files f JOIN Folders d ON d.Id = f.FolderId WHERE f.Id IN ({CatalogFileTargets.BindIds(cmd, chunk, "$f")}) ORDER BY f.Id;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new TargetRow(
                    reader.GetInt32(0),
                    reader.GetInt32(1),
                    Path.Combine(reader.GetString(2), reader.GetString(3)),
                    reader.GetInt32(4),
                    reader.GetInt32(5) == 1,
                    reader.IsDBNull(6) ? null : reader.GetDouble(6),
                    reader.IsDBNull(7) ? null : reader.GetDouble(7),
                    reader.IsDBNull(8) ? null : reader.GetInt32(8),
                    reader.IsDBNull(9) ? null : reader.GetInt32(9)));
            }
        }

        return rows;
    }
}
