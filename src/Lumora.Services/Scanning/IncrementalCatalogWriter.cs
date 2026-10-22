using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Lumora.Data.RawSql;

namespace Lumora.Services.Scanning;

/// <summary>
/// Raw-SQLite-Schreibpfad des Incremental-Scans (ADR-0004: Bulk über Microsoft.Data.Sqlite, nicht EF).
/// Jede Methode arbeitet in genau einer Transaktion, damit ein Batch entweder ganz oder gar nicht im Katalog
/// landet — bei neuen Dateien inklusive ihrer FTS-Einträge (CLAUDE.md: FilesFts ist external content ohne Trigger,
/// nach INSERT gezielt <c>INSERT INTO FilesFts(rowid, Name, Extension) …</c>). Namen/Endungen ändern sich beim
/// Diff nie (Identität ist Ordner + Name), daher brauchen Updates und Missing-Markierungen keine FTS-Pflege.
/// </summary>
internal static class IncrementalCatalogWriter
{
    /// <summary>Neue Metadaten einer bestehenden Zeile (geändert oder wieder aufgetaucht).</summary>
    public readonly record struct FileUpdate(int Id, long Size, long CreatedAtUnix, long ModifiedAtUnix);

    public readonly record struct InsertBatchReport(int Inserted, long FtsRows, TimeSpan FtsDuration);

    private const string InsertFileSql = """
        INSERT OR IGNORE INTO Files (Id, LibraryId, FolderId, Name, Extension, Size,
                                     CreatedAtUtc, ModifiedAtUtc, MediaType,
                                     Width, Height, DurationMs, Codec,
                                     IsFavorite, Rating, Missing)
        VALUES ($id, $libId, $folderId, $name, $ext, $size,
                $created, $modified, $mediaType,
                NULL, NULL, NULL, NULL,
                0, NULL, 0);
        """;

    /// <summary>
    /// Gezielte FTS-Indexierung nur der neuen Zeilen (Id-Bereich dieses Batches) statt Komplett-Rebuild.
    /// Von INSERT OR IGNORE verworfene Zeilen fehlen in Files und werden damit automatisch nicht indexiert.
    /// </summary>
    private const string InsertFtsRangeSql =
        "INSERT INTO FilesFts(rowid, Name, Extension) SELECT Id, Name, Extension FROM Files WHERE LibraryId = $lib AND Id BETWEEN $from AND $to;";

    private const string CountRangeSql =
        "SELECT COUNT(*) FROM Files WHERE LibraryId = $lib AND Id BETWEEN $from AND $to;";

    // Aufnahmedatum (ADR-0046): Inhalt geändert → aus der Datei neu lesen (CapturedAtSource NULL = Nachtragen), außer
    // es wurde manuell gesetzt (4). Die SET-Ausdrücke sehen die alten Werte der Zeile.
    private const string UpdateFileSql = """
        UPDATE Files SET Size = $size, CreatedAtUtc = $created, ModifiedAtUtc = $modified, Missing = 0,
            CapturedAt = CASE WHEN CapturedAtSource = 4 OR (Size = $size AND ModifiedAtUtc = $modified) THEN CapturedAt END,
            CapturedAtPrecision = CASE WHEN CapturedAtSource = 4 OR (Size = $size AND ModifiedAtUtc = $modified) THEN CapturedAtPrecision ELSE 0 END,
            CapturedAtOffset = CASE WHEN CapturedAtSource = 4 OR (Size = $size AND ModifiedAtUtc = $modified) THEN CapturedAtOffset END,
            CapturedAtSource = CASE WHEN CapturedAtSource = 4 OR (Size = $size AND ModifiedAtUtc = $modified) THEN CapturedAtSource END,
            -- Orte (ADR-0050): ebenso neu lesen, außer in Lumora gesetzt/entfernt bzw. übernommen (GeoSource 3–5, ADR-0062)
            Latitude = CASE WHEN GeoSource >= 3 OR (Size = $size AND ModifiedAtUtc = $modified) THEN Latitude END,
            Longitude = CASE WHEN GeoSource >= 3 OR (Size = $size AND ModifiedAtUtc = $modified) THEN Longitude END,
            PlaceId = CASE WHEN GeoSource >= 3 OR (Size = $size AND ModifiedAtUtc = $modified) THEN PlaceId END,
            GeoSource = CASE WHEN GeoSource >= 3 OR (Size = $size AND ModifiedAtUtc = $modified) THEN GeoSource END,
            -- Genauigkeit (ADR-0062): bei geändertem Inhalt neu bewerten
            GeoAccuracyReason = CASE WHEN Size = $size AND ModifiedAtUtc = $modified THEN GeoAccuracyReason END,
            GpsTimeUtc = CASE WHEN Size = $size AND ModifiedAtUtc = $modified THEN GpsTimeUtc END,
            ExifTimeOffset = CASE WHEN Size = $size AND ModifiedAtUtc = $modified THEN ExifTimeOffset END,
            GeoAccuracy = CASE WHEN Size = $size AND ModifiedAtUtc = $modified THEN GeoAccuracy END
        WHERE Id = $id;
        """;

    // Gesichtserkennung (ADR-0035): Inhalt an Ort und Stelle geändert → Datei zur erneuten Analyse freigeben (der Abgleich
    // über die Box behält bestätigte Zuordnungen). Läuft VOR dem Update, solange die alten Werte noch in der Zeile stehen.
    // Motive (ADR-0052): ebenso Bild-Vektor und Treffer verwerfen — der Motiv-Index rechnet die Datei neu.
    private const string InvalidateFaceScanSql =
        "DELETE FROM FaceScans WHERE FileId = $id AND EXISTS (SELECT 1 FROM Files WHERE Id = $id AND (Size <> $size OR ModifiedAtUtc <> $modified));" +
        "DELETE FROM FileMotifs WHERE FileId = $id AND EXISTS (SELECT 1 FROM Files WHERE Id = $id AND (Size <> $size OR ModifiedAtUtc <> $modified));" +
        "DELETE FROM FileEmbeddings WHERE FileId = $id AND EXISTS (SELECT 1 FROM Files WHERE Id = $id AND (Size <> $size OR ModifiedAtUtc <> $modified));";

    private const string MarkMissingSql = "UPDATE Files SET Missing = 1 WHERE Id = $id AND Missing = 0;";

    /// <summary>Fügt einen Batch neuer Dateien samt FTS-Einträgen in einer Transaktion ein.</summary>
    public static InsertBatchReport InsertFilesWithFts(
        SqliteConnection connection,
        int libraryId,
        IReadOnlyList<BulkScanWriter.FileRow> rows,
        CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return default;
        }

        using var tx = connection.BeginTransaction();

        var inserted = 0;
        var minId = int.MaxValue;
        var maxId = int.MinValue;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = InsertFileSql;
            var pId = AddParam(cmd, "$id", SqliteType.Integer);
            var pLib = AddParam(cmd, "$libId", SqliteType.Integer);
            var pFolder = AddParam(cmd, "$folderId", SqliteType.Integer);
            var pName = AddParam(cmd, "$name", SqliteType.Text);
            var pExt = AddParam(cmd, "$ext", SqliteType.Text);
            var pSize = AddParam(cmd, "$size", SqliteType.Integer);
            var pCreated = AddParam(cmd, "$created", SqliteType.Integer);
            var pModified = AddParam(cmd, "$modified", SqliteType.Integer);
            var pMediaType = AddParam(cmd, "$mediaType", SqliteType.Integer);
            cmd.Prepare();

            foreach (var f in rows)
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
                inserted += cmd.ExecuteNonQuery();
                minId = Math.Min(minId, f.Id);
                maxId = Math.Max(maxId, f.Id);
            }
        }

        var ftsSw = Stopwatch.StartNew();
        using (var fts = connection.CreateCommand())
        {
            fts.Transaction = tx;
            fts.CommandText = InsertFtsRangeSql;
            fts.Parameters.AddWithValue("$lib", libraryId);
            fts.Parameters.AddWithValue("$from", minId);
            fts.Parameters.AddWithValue("$to", maxId);
            fts.ExecuteNonQuery();
        }

        // Zählung über die Content-Tabelle: ExecuteNonQuery liefert für FTS5-Inserts keine verlässliche Zeilenzahl.
        long ftsRows;
        using (var count = connection.CreateCommand())
        {
            count.Transaction = tx;
            count.CommandText = CountRangeSql;
            count.Parameters.AddWithValue("$lib", libraryId);
            count.Parameters.AddWithValue("$from", minId);
            count.Parameters.AddWithValue("$to", maxId);
            ftsRows = (long)(count.ExecuteScalar() ?? 0L);
        }

        ftsSw.Stop();
        tx.Commit();
        return new InsertBatchReport(inserted, ftsRows, ftsSw.Elapsed);
    }

    /// <summary>Size/CreatedAtUtc/ModifiedAtUtc nachführen und Missing zurücksetzen (geändert oder wieder da).</summary>
    public static int UpdateFiles(SqliteConnection connection, IReadOnlyList<FileUpdate> updates, CancellationToken ct)
    {
        if (updates.Count == 0)
        {
            return 0;
        }

        using var tx = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = UpdateFileSql;
        var pSize = AddParam(cmd, "$size", SqliteType.Integer);
        var pCreated = AddParam(cmd, "$created", SqliteType.Integer);
        var pModified = AddParam(cmd, "$modified", SqliteType.Integer);
        var pId = AddParam(cmd, "$id", SqliteType.Integer);
        cmd.Prepare();

        using var faces = connection.CreateCommand(); // ADR-0035
        faces.Transaction = tx;
        faces.CommandText = InvalidateFaceScanSql;
        var fSize = AddParam(faces, "$size", SqliteType.Integer);
        var fModified = AddParam(faces, "$modified", SqliteType.Integer);
        var fId = AddParam(faces, "$id", SqliteType.Integer);
        faces.Prepare();

        var updated = 0;
        foreach (var u in updates)
        {
            ct.ThrowIfCancellationRequested();
            (fSize.Value, fModified.Value, fId.Value) = (u.Size, u.ModifiedAtUnix, u.Id);
            faces.ExecuteNonQuery();
            pSize.Value = u.Size;
            pCreated.Value = u.CreatedAtUnix;
            pModified.Value = u.ModifiedAtUnix;
            pId.Value = u.Id;
            updated += cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return updated;
    }

    /// <summary>Markiert die Zeilen als fehlend; Favorit, Bewertung und Thumbnail-Zeilen bleiben unangetastet.</summary>
    public static int MarkMissing(SqliteConnection connection, IReadOnlyList<int> fileIds, CancellationToken ct)
    {
        if (fileIds.Count == 0)
        {
            return 0;
        }

        using var tx = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = MarkMissingSql;
        var pId = AddParam(cmd, "$id", SqliteType.Integer);
        cmd.Prepare();

        var marked = 0;
        foreach (var id in fileIds)
        {
            ct.ThrowIfCancellationRequested();
            pId.Value = id;
            marked += cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return marked;
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
