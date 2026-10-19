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
    /// <summary>Scan mit Standardoptionen (<see cref="ScanOptions.Default"/>).</summary>
    Task<ScanResult> ScanAsync(
        int libraryId,
        ScanMode mode,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Scan mit Optionen — z. B. <see cref="ScanOptions.ConfirmMassMissing"/>, wenn der Benutzer nach
    /// <see cref="ScanOutcome.MassMissingBlocked"/> „Als fehlend markieren“ gewählt hat.
    /// </summary>
    Task<ScanResult> ScanAsync(
        int libraryId,
        ScanMode mode,
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Orchestriert einen Scan: Library laden, Root mit Timeout prüfen (PLAN Kap. 12), dann je nach Modus
/// <list type="bullet">
/// <item>Fresh/ClearAndRescan: (optional Katalog leeren,) Filesystem traversieren, Folders + Files via Raw-SQLite
/// Bulk-Insert persistieren, FTS5 komplett aufbauen;</item>
/// <item>Incremental: Diff gegen den Katalog mit stabilen FileIds, gezielte FTS-Pflege, Missing-Markierungen nur
/// bei erreichbarem Root und unterhalb des Mass-Missing-Schwellwerts (<see cref="IncrementalScanRunner"/>).</item>
/// </list>
/// Fortschritt geht an das optionale IProgress und gedrosselt als ScanProgressMessage an den Messenger. Am Ende
/// ScanCompletedMessage (Completed, MassMissingBlocked) bzw. ScanAbortedMessage (LibraryOffline, Abbruch, Fehler);
/// der Incremental-Scan sendet zusätzlich FilesChangedMessages (Added/Metadata/Removed) für Galerie und Ordnerbaum.
/// Ein nicht erreichbarer Root führt in allen Modi zu <see cref="ScanOutcome.LibraryOffline"/> ohne Katalogänderung.
/// </summary>
public sealed class ScanService : IScanService
{
    private const int InsertProgressEvery = 25_000;

    private readonly ILibraryContextFactory _contextFactory;
    private readonly FolderEnumerator _enumerator;
    private readonly BulkScanWriter _bulkWriter;
    private readonly FtsIndexer _ftsIndexer;
    private readonly ILibraryService _libraryService;
    private readonly ILogger<ScanService> _logger;
    private readonly IMessenger _messenger;

    /// <param name="messenger">Optional (Tests); produktiv WeakReferenceMessenger.Default.</param>
    public ScanService(
        ILibraryContextFactory contextFactory,
        FolderEnumerator enumerator,
        BulkScanWriter bulkWriter,
        FtsIndexer ftsIndexer,
        ILibraryService libraryService,
        ILogger<ScanService> logger,
        IMessenger? messenger = null)
    {
        _contextFactory = contextFactory;
        _enumerator = enumerator;
        _bulkWriter = bulkWriter;
        _ftsIndexer = ftsIndexer;
        _libraryService = libraryService;
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    public Task<ScanResult> ScanAsync(
        int libraryId,
        ScanMode mode,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
        => ScanAsync(libraryId, mode, ScanOptions.Default, progress, cancellationToken);

    public async Task<ScanResult> ScanAsync(
        int libraryId,
        ScanMode mode,
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Enumeration und Raw-SQLite-Persistenz sind synchrone I/O-Arbeit → nie auf dem UI-Thread laufen lassen.
        // Das Token geht nur in den Kern (nicht an Task.Run), damit auch ein bereits abgebrochener Aufruf die
        // ScanAbortedMessage sendet und Statusleiste/Seiten den Scan-Zustand sauber beenden.
        return await Task.Run(() => ScanCoreAsync(libraryId, mode, options, progress, cancellationToken))
            .ConfigureAwait(false);
    }

    private async Task<ScanResult> ScanCoreAsync(
        int libraryId,
        ScanMode mode,
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        var reporter = new ScanProgressReporter(libraryId, progress, _messenger);
        try
        {
            var result = mode == ScanMode.Incremental
                ? await RunIncrementalScanAsync(libraryId, options, reporter, ct).ConfigureAwait(false)
                : await RunFullScanAsync(libraryId, mode, reporter, ct).ConfigureAwait(false);

            if (result.Outcome == ScanOutcome.LibraryOffline)
            {
                _messenger.Send(new ScanAbortedMessage(libraryId, Canceled: false, LibraryOfflineException.Describe(result.RootPath), LibraryOffline: true));
            }
            else
            {
                _messenger.Send(new ScanCompletedMessage(libraryId, result));
            }

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

    // ---- Incremental ----------------------------------------------------------------------------

    private async Task<ScanResult> RunIncrementalScanAsync(int libraryId, ScanOptions options, ScanProgressReporter reporter, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var rootPath = await LoadRootPathAsync(libraryId, ct).ConfigureAwait(false);

        // Vorbedingung: Root erreichbar (Timeout lokal 1 s, UNC 3 s) — sonst keine Katalogänderung
        if (!await RootAvailabilityProbe.IsAvailableAsync(rootPath, ct).ConfigureAwait(false))
        {
            _logger.LogWarning("Incremental-Scan nicht gestartet, Bibliothek offline: {Root}", rootPath);
            return OfflineResult(libraryId, rootPath, ScanMode.Incremental, sw.Elapsed);
        }

        var (nextFolderId, nextFileId) = ReadNextIds();
        var runner = new IncrementalScanRunner(_contextFactory, _enumerator, _bulkWriter, _logger);
        var outcome = await runner.RunAsync(libraryId, rootPath, options, nextFolderId, nextFileId, reporter, ct).ConfigureAwait(false);
        var result = outcome.Result;

        _logger.LogInformation(
            "Incremental-Scan fertig ({Outcome}): {Added} neu, {Modified} geändert, {Missing} fehlend markiert ({Detected} erkannt), {Reappeared} wieder da, {Folders} neue Ordner, {Fts} FTS-Zeilen in {Sec:N2} s",
            result.Outcome, result.Added, result.Modified, result.MarkedMissing, result.MissingDetected, result.Reappeared, result.FoldersAdded, result.FtsRowsInserted, result.TotalDuration.TotalSeconds);

        // Galerie, Ordnerbaum und Lightbox nachziehen — nur für tatsächlich geschriebene Zeilen
        SendFilesChanged(libraryId, FilesChangeKind.Added, outcome.AddedIds);
        SendFilesChanged(libraryId, FilesChangeKind.Metadata, outcome.ModifiedIds);
        SendFilesChanged(libraryId, FilesChangeKind.Removed, outcome.RemovedIds);

        return result;
    }

    private void SendFilesChanged(int libraryId, FilesChangeKind kind, IReadOnlyList<int> fileIds)
    {
        if (fileIds.Count == 0)
        {
            return;
        }

        try
        {
            _messenger.Send(new FilesChangedMessage(libraryId, kind, fileIds));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Empfänger der FilesChangedMessage ({Kind}) warf eine Ausnahme", kind);
        }
    }

    // ---- Fresh / ClearAndRescan ------------------------------------------------------------------

    private async Task<ScanResult> RunFullScanAsync(int libraryId, ScanMode mode, ScanProgressReporter reporter, CancellationToken ct)
    {
        var totalSw = Stopwatch.StartNew();

        // 1) Library laden, RootPath holen
        var rootPath = await LoadRootPathAsync(libraryId, ct).ConfigureAwait(false);

        // 2) Root erreichbar? — VOR dem Leeren prüfen, damit ein Offline-Laufwerk nie den Katalog löscht (PLAN Kap. 12)
        if (!await RootAvailabilityProbe.IsAvailableAsync(rootPath, ct).ConfigureAwait(false))
        {
            _logger.LogWarning("Scan nicht gestartet, Bibliothek offline: {Root} (Mode={Mode})", rootPath, mode);
            return OfflineResult(libraryId, rootPath, mode, totalSw.Elapsed);
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
            FtsDuration: ftsReport.Duration)
        {
            Mode = mode,
        };
    }

    // ---- Gemeinsame Helfer ----------------------------------------------------------------------

    private static ScanResult OfflineResult(int libraryId, string rootPath, ScanMode mode, TimeSpan elapsed) =>
        new(libraryId, rootPath, 0, 0, 0, 0, elapsed, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero)
        {
            Mode = mode,
            Outcome = ScanOutcome.LibraryOffline,
        };

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
                "Für einen Neuaufbau ScanMode.ClearAndRescan, für ein Update ScanMode.Incremental verwenden.");
        }
    }

    /// <summary>
    /// Nächste freie Id je Tabelle: Maximum aus MAX(Id) und dem AUTOINCREMENT-Zähler (sqlite_sequence),
    /// damit nach ClearAsync keine Ids wiederverwendet werden (Thumbnail-Dedupe und Cache-Dateien sind FileId-basiert).
    /// Der Incremental-Scan setzt die Vergabe damit lückenlos hinter dem bisherigen Maximum fort.
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
}
