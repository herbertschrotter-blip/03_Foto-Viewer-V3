using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace PhotoViewerV3.Data.RawSql;

/// <summary>
/// Raw-SQLite Bulk-Inserter für Folders + Files, analog zum Pattern aus Spike #1.
/// EF Core wäre zu langsam für 500k Inserts (siehe ADR-0004). Single Connection,
/// eine Transaction für alle Files. INSERT OR IGNORE für Idempotenz.
/// </summary>
public sealed class BulkScanWriter
{
    public sealed record FolderRow(int Id, int LibraryId, string Path, int? ParentId, string Name, long ScannedAtUnix);

    public sealed record FileRow(
        int Id,
        int LibraryId,
        int FolderId,
        string Name,
        string Extension,
        long Size,
        long CreatedAtUnix,
        long ModifiedAtUnix,
        int MediaType,
        int? Width,
        int? Height,
        int? DurationMs,
        string? Codec,
        bool IsFavorite,
        byte? Rating,
        bool Missing);

    public sealed record InsertReport(int Attempted, int Inserted, TimeSpan Duration);

    public InsertReport InsertFolders(SqliteConnection connection, IReadOnlyList<FolderRow> folders)
    {
        var sw = Stopwatch.StartNew();
        using var tx = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO Folders (Id, LibraryId, Path, ParentId, Name, ScannedAt)
            VALUES ($id, $libId, $path, $parentId, $name, $scannedAt);
            """;

        var pId = AddParam(cmd, "$id", SqliteType.Integer);
        var pLib = AddParam(cmd, "$libId", SqliteType.Integer);
        var pPath = AddParam(cmd, "$path", SqliteType.Text);
        var pParent = AddParam(cmd, "$parentId", SqliteType.Integer);
        var pName = AddParam(cmd, "$name", SqliteType.Text);
        var pScanned = AddParam(cmd, "$scannedAt", SqliteType.Integer);
        cmd.Prepare();

        var inserted = 0;
        foreach (var f in folders)
        {
            pId.Value = f.Id;
            pLib.Value = f.LibraryId;
            pPath.Value = f.Path;
            pParent.Value = f.ParentId is { } pp ? pp : DBNull.Value;
            pName.Value = f.Name;
            pScanned.Value = f.ScannedAtUnix;
            inserted += cmd.ExecuteNonQuery();
        }

        tx.Commit();
        sw.Stop();
        return new InsertReport(folders.Count, inserted, sw.Elapsed);
    }

    public InsertReport InsertFiles(
        SqliteConnection connection,
        IEnumerable<FileRow> files,
        Action<int, TimeSpan>? onProgress = null,
        int progressEvery = 25_000,
        CancellationToken ct = default)
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

        var attempted = 0;
        var inserted = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            pId.Value = f.Id;
            pLib.Value = f.LibraryId;
            pFolder.Value = f.FolderId;
            pName.Value = f.Name;
            pExt.Value = f.Extension;
            pSize.Value = f.Size;
            pCreated.Value = f.CreatedAtUnix;
            pModified.Value = f.ModifiedAtUnix;
            pMediaType.Value = f.MediaType;
            pWidth.Value = f.Width is { } w ? w : DBNull.Value;
            pHeight.Value = f.Height is { } h ? h : DBNull.Value;
            pDuration.Value = f.DurationMs is { } d ? d : DBNull.Value;
            pCodec.Value = f.Codec is { } c ? c : DBNull.Value;
            pFavorite.Value = f.IsFavorite ? 1 : 0;
            pRating.Value = f.Rating is { } r ? r : DBNull.Value;
            pMissing.Value = f.Missing ? 1 : 0;

            inserted += cmd.ExecuteNonQuery();
            attempted++;

            if (onProgress != null && attempted % progressEvery == 0)
            {
                onProgress(attempted, sw.Elapsed);
            }
        }

        tx.Commit();
        sw.Stop();
        return new InsertReport(attempted, inserted, sw.Elapsed);
    }

    private static SqliteParameter AddParam(SqliteCommand cmd, string name, SqliteType type)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.SqliteType = type;
        cmd.Parameters.Add(p);
        return p;
    }
}
