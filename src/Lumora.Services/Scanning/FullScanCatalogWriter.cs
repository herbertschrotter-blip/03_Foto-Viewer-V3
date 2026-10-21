using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Lumora.Data.RawSql;
using Lumora.Services.Library;

namespace Lumora.Services.Scanning;

/// <summary>
/// Schreibpfad von Fresh und „Neu aufbauen“ (<see cref="ScanMode.ClearAndRescan"/>, ADR-0028): alles, was der Scan
/// am Katalog ändert, läuft in EINER Schreibtransaktion (BEGIN IMMEDIATE) auf einer Verbindung —
/// <list type="number">
/// <item>nur ClearAndRescan: Benutzerdaten sichern (<see cref="UserDataCarryOver.Capture"/>, TEMP-Tabellen), dann
/// die alten Thumbnails-, Files- und Folders-Zeilen löschen (Tag- und Kategorie-Zuweisungen per Kaskade);</item>
/// <item>Folders + Files per Bulk-Insert;</item>
/// <item>nur ClearAndRescan: Benutzerdaten auf die neuen FileIds zurückschreiben;</item>
/// <item>Suchindex per <c>'rebuild'</c> aus der Files-Tabelle (ersetzt auch die FTS-Einträge der gelöschten Zeilen);</item>
/// <item>Commit.</item>
/// </list>
/// Bis zum Commit sehen alle anderen Verbindungen (WAL) den bisherigen Katalog. Abbruch, Ausnahme oder Absturz vor
/// dem Commit lassen ihn samt Favoriten, Bewertungen, Tags und Kategorie-Werten unverändert — SQLite verwirft die
/// offene Transaktion (bei einem Absturz beim nächsten Öffnen der DB); es gibt keinen Zwischenzustand, den eine
/// Wiederaufnahme reparieren müsste. Die Thumbnail-Cache-Dateien räumt der Aufrufer erst nach dem Commit.
/// </summary>
internal sealed class FullScanCatalogWriter
{
    private const int InsertProgressEvery = 25_000;

    private readonly BulkScanWriter _bulkWriter;
    private readonly FtsIndexer _ftsIndexer;
    private readonly ILogger _logger;

    public FullScanCatalogWriter(BulkScanWriter bulkWriter, FtsIndexer ftsIndexer, ILogger logger)
    {
        _bulkWriter = bulkWriter;
        _ftsIndexer = ftsIndexer;
        _logger = logger;
    }

    /// <summary>Ergebnis eines committeten Laufs.</summary>
    public sealed record Report(
        BulkScanWriter.InsertReport Folders,
        BulkScanWriter.InsertReport Files,
        FtsIndexer.FtsReport Fts);

    /// <summary>
    /// Nächste freie Id einer Tabelle: Maximum aus MAX(Id) und dem AUTOINCREMENT-Zähler (sqlite_sequence), damit Ids
    /// nie wiederverwendet werden (Thumbnail-Cache und FileOperations sind FileId-basiert).
    /// </summary>
    public static int ReadNextId(SqliteConnection connection, SqliteTransaction? transaction, string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"""
            SELECT MAX(
                (SELECT COALESCE(MAX(Id), 0) FROM {table}),
                (SELECT COALESCE(MAX(seq), 0) FROM sqlite_sequence WHERE name = $table)) + 1;
            """;
        cmd.Parameters.AddWithValue("$table", table);
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Schreibt den Scan in einer Transaktion. Wirft (und rollt zurück) bei Abbruch über <paramref name="ct"/> und bei
    /// jedem Fehler — dann ist am Katalog nichts geändert.
    /// </summary>
    public Report Write(
        SqliteConnection connection,
        int libraryId,
        ScanMode mode,
        IReadOnlyList<BulkScanWriter.FolderRow> folderRows,
        IReadOnlyList<BulkScanWriter.FileRow> fileRows,
        long totalBytes,
        ScanProgressReporter reporter,
        CancellationToken ct)
    {
        var replace = mode == ScanMode.ClearAndRescan;
        using var tx = connection.BeginTransaction(); // BEGIN IMMEDIATE: Schreibsperre sofort, busy_timeout greift
        try
        {
            (folderRows, fileRows) = ShiftIdsIfTaken(connection, tx, libraryId, folderRows, fileRows);

            UserDataCarryOver? carry = null;
            if (replace)
            {
                reporter.Report(new ScanProgress(folderRows.Count, 0, totalBytes, null) { Phase = ScanPhase.Clearing }, force: true);
                carry = UserDataCarryOver.Capture(connection, tx, libraryId);
                _logger.LogInformation(
                    "Benutzerdaten gesichert: Library={LibraryId} — {Files} Dateien mit Favorit/Bewertung/Tags/Kategorien, {Tags} Tag-Zuweisungen, {Categories} Kategorie-Werte in {Ms:N0} ms",
                    libraryId, carry.CapturedFiles, carry.CapturedTagAssignments, carry.CapturedCategoryValues, carry.CaptureDuration.TotalMilliseconds);

                var replaced = LibraryCatalogCleanup.DeleteLibraryRowsBeforeFtsRebuild(connection, tx, libraryId);
                _logger.LogInformation(
                    "Alte Katalogzeilen entfernt (noch nicht committet): Library={LibraryId} — {Files} Dateien, {Folders} Ordner, {Thumbnails} Thumbnail-Zeilen",
                    libraryId, replaced.Files, replaced.Folders, replaced.Thumbnails);
                ct.ThrowIfCancellationRequested();
            }

            reporter.Report(new ScanProgress(folderRows.Count, 0, totalBytes, null) { Phase = ScanPhase.Persisting }, force: true);
            var folderReport = _bulkWriter.InsertFolders(connection, folderRows, tx);
            _logger.LogInformation(
                "Folders insert: {In}/{At} in {Ms:N0} ms",
                folderReport.Inserted, folderReport.Attempted, folderReport.Duration.TotalMilliseconds);

            var fileReport = _bulkWriter.InsertFiles(
                connection,
                fileRows,
                onProgress: (count, elapsed) =>
                {
                    _logger.LogInformation(
                        "Files insert progress: {Count}/{Total} ({Pct:N1} %) in {Sec:N1} s",
                        count, fileRows.Count, 100.0 * count / Math.Max(1, fileRows.Count), elapsed.TotalSeconds);
                    reporter.Report(new ScanProgress(folderRows.Count, count, totalBytes, null) { Phase = ScanPhase.Persisting }, force: true);
                },
                progressEvery: InsertProgressEvery,
                ct: ct,
                transaction: tx);
            _logger.LogInformation(
                "Files insert: {In}/{At} in {Sec:N2} s",
                fileReport.Inserted, fileReport.Attempted, fileReport.Duration.TotalSeconds);

            if (carry is not null)
            {
                var restored = carry.Restore();
                _logger.LogInformation(
                    "Benutzerdaten übernommen: Library={LibraryId} — {Restored}/{Captured} Dateien ({Values} mit Favorit/Bewertung, {Tags} Tag-Zuweisungen, {Categories} Kategorie-Werte), {Lost} nicht mehr vorhanden, in {Ms:N0} ms",
                    libraryId, restored.RestoredFiles, restored.CapturedFiles, restored.FavoritesOrRatingsRestored, restored.TagAssignmentsRestored,
                    restored.CategoryValuesRestored, restored.LostFiles, restored.Duration.TotalMilliseconds);
            }

            ct.ThrowIfCancellationRequested();
            reporter.Report(new ScanProgress(folderRows.Count, fileRows.Count, totalBytes, null) { Phase = ScanPhase.Indexing }, force: true);
            var ftsReport = _ftsIndexer.PopulateAll(connection, tx);
            _logger.LogInformation(
                "FTS5 populated: {Rows} rows in {Ms:N0} ms",
                ftsReport.RowsInserted, ftsReport.Duration.TotalMilliseconds);

            // Letzte Gelegenheit zum Abbruch — nach dem Commit ist der neue Katalog verbindlich.
            ct.ThrowIfCancellationRequested();
            tx.Commit();
            return new Report(folderReport, fileReport, ftsReport);
        }
        catch (Exception ex)
        {
            // Rollback beim Dispose der Transaktion (using): der bisherige Katalog bleibt vollständig erhalten.
            if (ex is OperationCanceledException)
            {
                _logger.LogInformation("Scan vor dem Commit abgebrochen, Katalog unverändert: Library={LibraryId} Mode={Mode}", libraryId, mode);
            }
            else
            {
                _logger.LogWarning(ex, "Scan-Transaktion zurückgerollt, Katalog unverändert: Library={LibraryId} Mode={Mode}", libraryId, mode);
            }

            throw;
        }
    }

    /// <summary>
    /// Die Start-Ids hat der Scan VOR der Enumeration gelesen. Hat in der Zwischenzeit ein anderer Schreiber Ids
    /// vergeben (Scan oder Datei-Batch einer anderen Bibliothek — die Scan-Sperre gilt je Bibliothek), würden
    /// kollidierende Zeilen beim INSERT OR IGNORE still verworfen. Unter der Schreibsperre neu lesen und die Zeilen
    /// samt ParentId/FolderId-Verweisen nach hinten verschieben, falls nötig.
    /// </summary>
    private (IReadOnlyList<BulkScanWriter.FolderRow> Folders, IReadOnlyList<BulkScanWriter.FileRow> Files) ShiftIdsIfTaken(
        SqliteConnection connection,
        SqliteTransaction tx,
        int libraryId,
        IReadOnlyList<BulkScanWriter.FolderRow> folderRows,
        IReadOnlyList<BulkScanWriter.FileRow> fileRows)
    {
        var folderShift = folderRows.Count == 0 ? 0 : Math.Max(0, ReadNextId(connection, tx, "Folders") - folderRows.Min(r => r.Id));
        var fileShift = fileRows.Count == 0 ? 0 : Math.Max(0, ReadNextId(connection, tx, "Files") - fileRows.Min(r => r.Id));
        if (folderShift == 0 && fileShift == 0)
        {
            return (folderRows, fileRows);
        }

        _logger.LogWarning(
            "Ids während der Enumeration anderweitig vergeben — Zeilen werden verschoben: Library={LibraryId} Folders +{FolderShift}, Files +{FileShift}",
            libraryId, folderShift, fileShift);
        var folders = folderRows.Select(r => r with { Id = r.Id + folderShift, ParentId = r.ParentId + folderShift }).ToList();
        var files = fileRows.Select(r => r with { Id = r.Id + fileShift, FolderId = r.FolderId + folderShift }).ToList();
        return (folders, files);
    }
}
