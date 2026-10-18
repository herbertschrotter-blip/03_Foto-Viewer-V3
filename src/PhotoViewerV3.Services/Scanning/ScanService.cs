using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Data.RawSql;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Library;
using PhotoViewerV3.Services.Messaging;

namespace PhotoViewerV3.Services.Scanning;

public interface IScanService
{
    Task<ScanResult> ScanAsync(
        int libraryId,
        ScanMode mode,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Orchestriert einen Scan: Library laden, Root prüfen, (optional) Katalog leeren, Filesystem traversieren,
/// Folders + Files via Raw-SQLite Bulk-Insert persistieren, FTS5 indexieren.
/// Fortschritt geht an das optionale IProgress und gedrosselt als ScanProgressMessage an den Messenger,
/// am Ende ScanCompletedMessage bzw. ScanAbortedMessage. Fresh und ClearAndRescan; Incremental folgt in Welle 2.
/// </summary>
public sealed class ScanService : IScanService
{
    /// <summary>Fortschrittsmeldungen höchstens alle 250 ms oder alle 2000 Dateien (ADR-0013: gedrosselt).</summary>
    private const int ProgressFileInterval = 2_000;
    private static readonly TimeSpan ProgressTimeInterval = TimeSpan.FromMilliseconds(250);
    private const int InsertProgressEvery = 25_000;

    private readonly ILibraryContextFactory _contextFactory;
    private readonly FolderEnumerator _enumerator;
    private readonly BulkScanWriter _bulkWriter;
    private readonly FtsIndexer _ftsIndexer;
    private readonly ILibraryService _libraryService;
    private readonly ILogger<ScanService> _logger;
    private readonly IMessenger _messenger = WeakReferenceMessenger.Default;

    public ScanService(
        ILibraryContextFactory contextFactory,
        FolderEnumerator enumerator,
        BulkScanWriter bulkWriter,
        FtsIndexer ftsIndexer,
        ILibraryService libraryService,
        ILogger<ScanService> logger)
    {
        _contextFactory = contextFactory;
        _enumerator = enumerator;
        _bulkWriter = bulkWriter;
        _ftsIndexer = ftsIndexer;
        _libraryService = libraryService;
        _logger = logger;
    }

    public async Task<ScanResult> ScanAsync(
        int libraryId,
        ScanMode mode,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (mode == ScanMode.Incremental)
        {
            throw new NotSupportedException(
                "ScanMode.Incremental (Diff-Rescan mit Offline-vs-Missing-Guard) kommt in Welle 2. " +
                "Bitte Fresh oder ClearAndRescan verwenden.");
        }

        // Enumeration und Raw-SQLite-Persistenz sind synchrone I/O-Arbeit → nie auf dem UI-Thread laufen lassen.
        return await Task.Run(() => ScanCoreAsync(libraryId, mode, progress, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ScanResult> ScanCoreAsync(
        int libraryId,
        ScanMode mode,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        var reporter = new ProgressReporter(libraryId, progress, _messenger);
        try
        {
            var result = await RunScanAsync(libraryId, mode, reporter, ct).ConfigureAwait(false);
            _messenger.Send(new ScanCompletedMessage(libraryId, result));
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Scan abgebrochen: Library={LibraryId}", libraryId);
            _messenger.Send(new ScanAbortedMessage(libraryId, Canceled: true, "Scan abgebrochen."));
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan fehlgeschlagen: Library={LibraryId}", libraryId);
            _messenger.Send(new ScanAbortedMessage(libraryId, Canceled: false, ex.Message));
            throw;
        }
    }

    private async Task<ScanResult> RunScanAsync(int libraryId, ScanMode mode, ProgressReporter reporter, CancellationToken ct)
    {
        var totalSw = Stopwatch.StartNew();

        // 1) Library laden, RootPath holen
        var rootPath = await LoadRootPathAsync(libraryId, ct).ConfigureAwait(false);

        // 2) Root erreichbar? — VOR dem Leeren prüfen, damit ein Offline-Laufwerk nie den Katalog löscht (PLAN Kap. 12)
        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException(
                $"Der Bibliotheksordner „{rootPath}“ ist nicht erreichbar. " +
                "Bitte Laufwerk anschließen oder Netzwerkverbindung prüfen.");
        }

        // 3) ClearAndRescan: bestehende Katalogdaten dieser Library löschen (FileIds werden neu vergeben)
        if (mode == ScanMode.ClearAndRescan)
        {
            reporter.Report(new ScanProgress(0, 0, 0, null) { Phase = ScanPhase.Clearing }, force: true);
            await _libraryService.ClearAsync(libraryId, ct).ConfigureAwait(false);
        }

        // 4) Fresh-Check: keine bestehenden Folders/Files für diese Library
        await EnsureLibraryIsEmptyAsync(libraryId, ct).ConfigureAwait(false);

        // 5) Start-Ids: fortlaufend über alle Libraries und nie wiederverwendet (sqlite_sequence) —
        //    sonst kollidieren die Ids einer zweiten Library und INSERT OR IGNORE verwirft Zeilen still.
        var (nextFolderId, nextFileId) = ReadNextIds();

        _logger.LogInformation(
            "Scan startet: Library={LibraryId} Root={Root} Mode={Mode} StartIds Folder={FolderId} File={FileId}",
            libraryId, rootPath, mode, nextFolderId, nextFileId);

        // 6) Enumeration + In-Memory-Aggregation
        var enumSw = Stopwatch.StartNew();
        var folderMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var folderRows = new List<BulkScanWriter.FolderRow>();
        var fileRows = new List<BulkScanWriter.FileRow>();
        long totalBytes = 0;
        var scannedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var rootKey = TrimSeparators(rootPath);

        // Legt den Ordner und alle fehlenden Zwischenordner bis zum Root an, damit die ParentId-Kette
        // vollständig ist (Ordnerbaum + Container-Kacheln brauchen auch Ordner ohne direkte Medien).
        int GetOrCreateFolderId(string folderPath)
        {
            var isRoot = string.Equals(TrimSeparators(folderPath), rootKey, StringComparison.OrdinalIgnoreCase);
            if (isRoot)
            {
                folderPath = rootPath; // kanonische Schreibweise des Roots
            }

            if (folderMap.TryGetValue(folderPath, out var existingId))
            {
                return existingId;
            }

            int? parentId = null;
            if (!isRoot)
            {
                var parentPath = Path.GetDirectoryName(folderPath);
                if (!string.IsNullOrEmpty(parentPath) && TrimSeparators(parentPath).Length >= rootKey.Length)
                {
                    parentId = GetOrCreateFolderId(parentPath);
                }
            }

            var id = nextFolderId++;
            folderMap[folderPath] = id;
            folderRows.Add(new BulkScanWriter.FolderRow(id, libraryId, folderPath, parentId, FolderDisplayName(folderPath), scannedAtUnix));
            return id;
        }

        foreach (var df in _enumerator.Enumerate(rootPath, ct))
        {
            var folderId = GetOrCreateFolderId(df.FolderPath);

            fileRows.Add(new BulkScanWriter.FileRow(
                Id: nextFileId++,
                LibraryId: libraryId,
                FolderId: folderId,
                Name: df.Name,
                Extension: df.Extension,
                Size: df.Size,
                CreatedAtUnix: ToUnix(df.CreatedAtUtc),
                ModifiedAtUnix: ToUnix(df.ModifiedAtUtc),
                MediaType: (int)df.MediaType,
                Width: null,
                Height: null,
                DurationMs: null,
                Codec: null,
                IsFavorite: false,
                Rating: null,
                Missing: false));

            totalBytes += df.Size;
            reporter.Report(new ScanProgress(folderRows.Count, fileRows.Count, totalBytes, df.FolderPath));
        }

        enumSw.Stop();
        reporter.Report(new ScanProgress(folderRows.Count, fileRows.Count, totalBytes, null), force: true);
        _logger.LogInformation(
            "Enumeration fertig: {Folders} Folders, {Files} Files, {Bytes:N0} bytes in {Sec:N2} s",
            folderRows.Count, fileRows.Count, totalBytes, enumSw.Elapsed.TotalSeconds);
        if (fileRows.Count == 0)
        {
            _logger.LogWarning(
                "Keine Mediendateien unter {Root} gefunden — Ordner leer, nur nicht unterstützte Formate oder fehlende Leserechte.",
                rootPath);
        }

        // Abbruch vor der Persistenz: nichts schreiben
        ct.ThrowIfCancellationRequested();

        // 7) Persistenz via Raw-SQLite
        var persistSw = Stopwatch.StartNew();
        BulkScanWriter.InsertReport folderReport;
        BulkScanWriter.InsertReport fileReport;
        FtsIndexer.FtsReport ftsReport;
        var ftsPopulated = false;

        using (var connection = _contextFactory.OpenSqliteConnection())
        {
            try
            {
                reporter.Report(new ScanProgress(folderRows.Count, 0, totalBytes, null) { Phase = ScanPhase.Persisting }, force: true);
                folderReport = _bulkWriter.InsertFolders(connection, folderRows);
                _logger.LogInformation(
                    "Folders insert: {In}/{At} in {Ms:N0} ms",
                    folderReport.Inserted, folderReport.Attempted, folderReport.Duration.TotalMilliseconds);

                fileReport = _bulkWriter.InsertFiles(
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
                    ct: ct);
                _logger.LogInformation(
                    "Files insert: {In}/{At} in {Sec:N2} s",
                    fileReport.Inserted, fileReport.Attempted, fileReport.Duration.TotalSeconds);

                reporter.Report(new ScanProgress(folderRows.Count, fileRows.Count, totalBytes, null) { Phase = ScanPhase.Indexing }, force: true);
                ftsReport = _ftsIndexer.PopulateAll(connection);
                ftsPopulated = true;
                _logger.LogInformation(
                    "FTS5 populated: {Rows} rows in {Ms:N0} ms",
                    ftsReport.RowsInserted, ftsReport.Duration.TotalMilliseconds);
            }
            catch
            {
                // Keine halben Zustände: alles, was dieser Scan bereits geschrieben hat, wieder entfernen.
                // Die Library war vor dem Scan leer, daher ist "alles dieser Library löschen" exakt der Vorzustand.
                RollbackPartialScan(connection, libraryId, ftsPopulated);
                throw;
            }
        }

        persistSw.Stop();
        totalSw.Stop();

        return new ScanResult(
            LibraryId: libraryId,
            RootPath: rootPath,
            FoldersInserted: folderReport.Inserted,
            FilesInserted: fileReport.Inserted,
            FtsRowsInserted: (int)ftsReport.RowsInserted,
            TotalBytes: totalBytes,
            TotalDuration: totalSw.Elapsed,
            EnumerationDuration: enumSw.Elapsed,
            PersistenceDuration: persistSw.Elapsed - ftsReport.Duration,
            FtsDuration: ftsReport.Duration);
    }

    private async Task<string> LoadRootPathAsync(int libraryId, CancellationToken ct)
    {
        await using var ctx = _contextFactory.Create();
        var rootPath = await ctx.Libraries.AsNoTracking()
            .Where(l => l.Id == libraryId)
            .Select(l => l.RootPath)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return rootPath ?? throw new InvalidOperationException($"Library Id={libraryId} nicht gefunden.");
    }

    private async Task EnsureLibraryIsEmptyAsync(int libraryId, CancellationToken ct)
    {
        await using var ctx = _contextFactory.Create();
        var existingFolders = await ctx.Folders.CountAsync(f => f.LibraryId == libraryId, ct).ConfigureAwait(false);
        var existingFiles = await ctx.Files.CountAsync(f => f.LibraryId == libraryId, ct).ConfigureAwait(false);
        if (existingFolders > 0 || existingFiles > 0)
        {
            throw new InvalidOperationException(
                $"ScanMode.Fresh: Library {libraryId} ist nicht leer ({existingFolders} Folders, {existingFiles} Files). " +
                "Für einen Neuaufbau ScanMode.ClearAndRescan verwenden.");
        }
    }

    /// <summary>
    /// Nächste freie Id je Tabelle: Maximum aus MAX(Id) und dem AUTOINCREMENT-Zähler (sqlite_sequence),
    /// damit nach ClearAsync keine Ids wiederverwendet werden (Thumbnail-Dedupe und Cache-Dateien sind FileId-basiert).
    /// </summary>
    private (int NextFolderId, int NextFileId) ReadNextIds()
    {
        using var connection = _contextFactory.OpenSqliteConnection();
        return (ReadNextId(connection, "Folders"), ReadNextId(connection, "Files"));
    }

    private static int ReadNextId(SqliteConnection connection, string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT MAX(
                (SELECT COALESCE(MAX(Id), 0) FROM {table}),
                (SELECT COALESCE(MAX(seq), 0) FROM sqlite_sequence WHERE name = $table)) + 1;
            """;
        cmd.Parameters.AddWithValue("$table", table);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private void RollbackPartialScan(SqliteConnection connection, int libraryId, bool ftsPopulated)
    {
        try
        {
            var result = LibraryCatalogCleanup.DeleteLibraryRows(connection, libraryId, _logger, ftsEntriesExpected: ftsPopulated);
            _logger.LogWarning(
                "Unvollständiger Scan zurückgerollt: Library={LibraryId} ({Files} Files, {Folders} Folders entfernt, FTS-Rebuild: {Rebuilt})",
                libraryId, result.Files, result.Folders, result.FtsRebuilt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rollback des unvollständigen Scans fehlgeschlagen: Library={LibraryId}", libraryId);
        }
    }

    private static string FolderDisplayName(string fullPath)
    {
        var name = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(name))
        {
            // Trailing-Slash-Fall: nimm den parent directory-Namen
            name = Path.GetFileName(Path.GetDirectoryName(fullPath) ?? fullPath);
            if (string.IsNullOrEmpty(name))
            {
                name = fullPath; // Laufwerkswurzel
            }
        }

        return name;
    }

    private static string TrimSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static long ToUnix(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    /// <summary>Drosselt Fortschrittsmeldungen und verteilt sie an IProgress und Messenger.</summary>
    private sealed class ProgressReporter
    {
        private readonly int _libraryId;
        private readonly IProgress<ScanProgress>? _progress;
        private readonly IMessenger _messenger;
        private readonly Stopwatch _sinceLast = Stopwatch.StartNew();
        private int _filesAtLast = -ProgressFileInterval; // erste Meldung sofort

        public ProgressReporter(int libraryId, IProgress<ScanProgress>? progress, IMessenger messenger)
        {
            _libraryId = libraryId;
            _progress = progress;
            _messenger = messenger;
        }

        public void Report(ScanProgress p, bool force = false)
        {
            if (!force
                && p.FilesDiscovered - _filesAtLast < ProgressFileInterval
                && _sinceLast.Elapsed < ProgressTimeInterval)
            {
                return;
            }

            _filesAtLast = p.FilesDiscovered;
            _sinceLast.Restart();
            _progress?.Report(p);
            _messenger.Send(new ScanProgressMessage(_libraryId, p));
        }
    }
}
