using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Data.Sqlite;

using Lumora.PerfTests.Spikes.SyntheticData;

namespace Lumora.PerfTests.Spikes.Bulk;

/// <summary>
/// Raw Microsoft.Data.Sqlite Bulk-Insert für Spike-Messung gemäß ADR-0004.
/// Prepared Command + single Transaction. FTS-Population separat nach Files-Insert.
/// </summary>
internal static class BulkInserter
{
    private const int LibraryId = 1;
    private const int ProgressIntervalRows = 50_000;

    public static FolderInsertReport InsertFolders(SqliteConnection connection, IReadOnlyList<SyntheticFolder> folders)
    {
        var sw = Stopwatch.StartNew();
        using var tx = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO Folders (Id, LibraryId, Path, ParentId, Name, ScannedAt)
            VALUES ($id, $libId, $path, $parentId, $name, $scannedAt);
            """;

        var pId = AddParam(cmd, "$id", SqliteType.Integer);
        var pLib = AddParam(cmd, "$libId", SqliteType.Integer);
        var pPath = AddParam(cmd, "$path", SqliteType.Text);
        var pParent = AddParam(cmd, "$parentId", SqliteType.Integer);
        var pName = AddParam(cmd, "$name", SqliteType.Text);
        var pScanned = AddParam(cmd, "$scannedAt", SqliteType.Integer);

        cmd.Prepare();

        var scannedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var folder in folders)
        {
            pId.Value = folder.Id;
            pLib.Value = LibraryId;
            pPath.Value = folder.Path;
            pParent.Value = folder.ParentId is { } parentId ? parentId : DBNull.Value;
            pName.Value = folder.Name;
            pScanned.Value = scannedAt;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        sw.Stop();
        return new FolderInsertReport(folders.Count, sw.Elapsed);
    }

    public static FileInsertReport InsertFiles(
        SqliteConnection connection,
        IEnumerable<SyntheticFile> files,
        int expectedTotal,
        Action<long, TimeSpan>? onProgress = null)
    {
        var sw = Stopwatch.StartNew();
        using var tx = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO Files (Id, LibraryId, FolderId, Name, Extension, Size,
                                         CreatedAtUtc, ModifiedAtUtc, MediaType,
                                         Width, Height, DurationMs, Codec,
                                         IsFavorite, Rating, Missing)
            VALUES ($id, $libId, $folderId, $name, $ext, $size,
                    $created, $modified, $mediaType,
                    $width, $height, $duration, $codec,
                    $favorite, $rating, $missing);
            """;

        var pId = AddParam(cmd, "$id", SqliteType.Integer);
        var pLib = AddParam(cmd, "$libId", SqliteType.Integer);
        var pFolder = AddParam(cmd, "$folderId", SqliteType.Integer);
        var pName = AddParam(cmd, "$name", SqliteType.Text);
        var pExt = AddParam(cmd, "$ext", SqliteType.Text);
        var pSize = AddParam(cmd, "$size", SqliteType.Integer);
        var pCreated = AddParam(cmd, "$created", SqliteType.Integer);
        var pModified = AddParam(cmd, "$modified", SqliteType.Integer);
        var pMediaType = AddParam(cmd, "$mediaType", SqliteType.Integer);
        var pWidth = AddParam(cmd, "$width", SqliteType.Integer);
        var pHeight = AddParam(cmd, "$height", SqliteType.Integer);
        var pDuration = AddParam(cmd, "$duration", SqliteType.Integer);
        var pCodec = AddParam(cmd, "$codec", SqliteType.Text);
        var pFavorite = AddParam(cmd, "$favorite", SqliteType.Integer);
        var pRating = AddParam(cmd, "$rating", SqliteType.Integer);
        var pMissing = AddParam(cmd, "$missing", SqliteType.Integer);

        cmd.Prepare();

        long iterationCount = 0;
        long insertedCount = 0;
        foreach (var f in files)
        {
            pId.Value = f.Id;
            pLib.Value = LibraryId;
            pFolder.Value = f.FolderId;
            pName.Value = f.Name;
            pExt.Value = f.Extension;
            pSize.Value = f.Size;
            pCreated.Value = ToUnixSeconds(f.CreatedAtUtc);
            pModified.Value = ToUnixSeconds(f.ModifiedAtUtc);
            pMediaType.Value = (int)f.MediaType;
            pWidth.Value = f.Width is { } w ? w : DBNull.Value;
            pHeight.Value = f.Height is { } h ? h : DBNull.Value;
            pDuration.Value = f.DurationMs is { } d ? d : DBNull.Value;
            pCodec.Value = f.Codec is { } c ? c : DBNull.Value;
            pFavorite.Value = f.IsFavorite ? 1 : 0;
            pRating.Value = f.Rating is { } r ? r : DBNull.Value;
            pMissing.Value = f.Missing ? 1 : 0;

            insertedCount += cmd.ExecuteNonQuery();
            iterationCount++;

            if (onProgress != null && iterationCount % ProgressIntervalRows == 0)
            {
                onProgress(iterationCount, sw.Elapsed);
            }
        }

        var insertDuration = sw.Elapsed;
        sw.Restart();
        tx.Commit();
        var commitDuration = sw.Elapsed;

        return new FileInsertReport(iterationCount, insertedCount, insertDuration, commitDuration);
    }

    public static FtsPopulationReport PopulateFts(SqliteConnection connection)
    {
        var sw = Stopwatch.StartNew();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO FilesFts(rowid, Name, Extension)
            SELECT Id, Name, Extension FROM Files;
            """;
        var affected = cmd.ExecuteNonQuery();
        sw.Stop();
        return new FtsPopulationReport(affected, sw.Elapsed);
    }

    public static TimeSpan WalCheckpoint(SqliteConnection connection)
    {
        var sw = Stopwatch.StartNew();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        cmd.ExecuteNonQuery();
        sw.Stop();
        return sw.Elapsed;
    }

    public static TimeSpan Analyze(SqliteConnection connection)
    {
        var sw = Stopwatch.StartNew();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "ANALYZE;";
        cmd.ExecuteNonQuery();
        sw.Stop();
        return sw.Elapsed;
    }

    private static SqliteParameter AddParam(SqliteCommand cmd, string name, SqliteType type)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.SqliteType = type;
        cmd.Parameters.Add(p);
        return p;
    }

    private static long ToUnixSeconds(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
}

internal sealed record FolderInsertReport(int RowCount, TimeSpan Duration);

internal sealed record FileInsertReport(long Attempted, long Inserted, TimeSpan InsertDuration, TimeSpan CommitDuration)
{
    public TimeSpan TotalDuration => InsertDuration + CommitDuration;
    public double RowsPerSecond => InsertDuration.TotalSeconds <= 0 ? 0 : Attempted / InsertDuration.TotalSeconds;
    public long Skipped => Attempted - Inserted;
}

internal sealed record FtsPopulationReport(long RowCount, TimeSpan Duration);
