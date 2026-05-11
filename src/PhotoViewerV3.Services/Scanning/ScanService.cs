using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Data.RawSql;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Services.Database;

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
/// Orchestriert einen Initial-Scan: Library laden, Filesystem traversieren,
/// Folders + Files via Raw-SQLite Bulk-Insert persistieren, FTS5 indexieren.
/// V0.1: nur ScanMode.Fresh — DB muss für die Library leer sein.
/// </summary>
public sealed class ScanService : IScanService
{
    private const int ProgressInterval = 5_000;

    private readonly ILibraryContextFactory _contextFactory;
    private readonly FolderEnumerator _enumerator;
    private readonly BulkScanWriter _bulkWriter;
    private readonly FtsIndexer _ftsIndexer;
    private readonly ILogger<ScanService> _logger;

    public ScanService(
        ILibraryContextFactory contextFactory,
        FolderEnumerator enumerator,
        BulkScanWriter bulkWriter,
        FtsIndexer ftsIndexer,
        ILogger<ScanService> logger)
    {
        _contextFactory = contextFactory;
        _enumerator = enumerator;
        _bulkWriter = bulkWriter;
        _ftsIndexer = ftsIndexer;
        _logger = logger;
    }

    public async Task<ScanResult> ScanAsync(
        int libraryId,
        ScanMode mode,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (mode != ScanMode.Fresh)
        {
            throw new NotSupportedException("V0.1 unterstützt nur ScanMode.Fresh.");
        }

        var totalSw = Stopwatch.StartNew();

        // 1) Library laden, RootPath holen
        string rootPath;
        await using (var ctx = _contextFactory.Create())
        {
            var library = await ctx.Libraries.FirstOrDefaultAsync(l => l.Id == libraryId, cancellationToken)
                .ConfigureAwait(false);
            if (library is null)
            {
                throw new InvalidOperationException($"Library Id={libraryId} nicht gefunden.");
            }

            rootPath = library.RootPath;

            // Fresh-Scan-Check: keine bestehenden Folders/Files für diese Library
            var existingFolders = await ctx.Folders.CountAsync(f => f.LibraryId == libraryId, cancellationToken)
                .ConfigureAwait(false);
            var existingFiles = await ctx.Files.CountAsync(f => f.LibraryId == libraryId, cancellationToken)
                .ConfigureAwait(false);
            if (existingFolders > 0 || existingFiles > 0)
            {
                throw new InvalidOperationException(
                    $"ScanMode.Fresh: Library {libraryId} ist nicht leer ({existingFolders} Folders, {existingFiles} Files). " +
                    "Re-Scan ist V0.2+.");
            }
        }

        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException($"Library-RootPath nicht erreichbar: {rootPath}");
        }

        _logger.LogInformation("Scan startet: Library={LibraryId} Root={Root}", libraryId, rootPath);

        // 2) Enumeration + In-Memory-Aggregation
        var enumSw = Stopwatch.StartNew();
        var folderMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var folderRows = new List<BulkScanWriter.FolderRow>();
        var fileRows = new List<BulkScanWriter.FileRow>();
        long totalBytes = 0;
        var nextFolderId = 1;
        var nextFileId = 1;
        var scannedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var lastReport = 0;

        foreach (var df in _enumerator.Enumerate(rootPath, cancellationToken))
        {
            if (!folderMap.TryGetValue(df.FolderPath, out var folderId))
            {
                folderId = nextFolderId++;
                folderMap[df.FolderPath] = folderId;
                folderRows.Add(BuildFolderRow(folderId, libraryId, df.FolderPath, folderMap, scannedAtUnix));
            }

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

            if (fileRows.Count - lastReport >= ProgressInterval)
            {
                lastReport = fileRows.Count;
                progress?.Report(new ScanProgress(folderRows.Count, fileRows.Count, totalBytes, df.FolderPath));
            }
        }
        enumSw.Stop();
        progress?.Report(new ScanProgress(folderRows.Count, fileRows.Count, totalBytes, null));
        _logger.LogInformation(
            "Enumeration fertig: {Folders} Folders, {Files} Files, {Bytes:N0} bytes in {Sec:N2} s",
            folderRows.Count, fileRows.Count, totalBytes, enumSw.Elapsed.TotalSeconds);

        // 3) Persistenz via Raw-SQLite
        var persistSw = Stopwatch.StartNew();
        BulkScanWriter.InsertReport folderReport;
        BulkScanWriter.InsertReport fileReport;
        FtsIndexer.FtsReport ftsReport;
        TimeSpan ftsDuration;

        using (var connection = _contextFactory.OpenSqliteConnection())
        {
            folderReport = _bulkWriter.InsertFolders(connection, folderRows);
            _logger.LogInformation(
                "Folders insert: {In}/{At} in {Ms:N0} ms",
                folderReport.Inserted, folderReport.Attempted, folderReport.Duration.TotalMilliseconds);

            fileReport = _bulkWriter.InsertFiles(
                connection,
                fileRows,
                onProgress: (count, elapsed) =>
                    _logger.LogInformation(
                        "Files insert progress: {Count}/{Total} ({Pct:N1} %) in {Sec:N1} s",
                        count, fileRows.Count, 100.0 * count / Math.Max(1, fileRows.Count), elapsed.TotalSeconds),
                progressEvery: 25_000,
                ct: cancellationToken);
            _logger.LogInformation(
                "Files insert: {In}/{At} in {Sec:N2} s",
                fileReport.Inserted, fileReport.Attempted, fileReport.Duration.TotalSeconds);

            ftsReport = _ftsIndexer.PopulateAll(connection);
            _logger.LogInformation("FTS5 populated: {Rows} rows in {Ms:N0} ms",
                ftsReport.RowsInserted, ftsReport.Duration.TotalMilliseconds);
            ftsDuration = ftsReport.Duration;
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
            PersistenceDuration: persistSw.Elapsed - ftsDuration,
            FtsDuration: ftsDuration);
    }

    private static BulkScanWriter.FolderRow BuildFolderRow(
        int id,
        int libraryId,
        string fullPath,
        Dictionary<string, int> folderMap,
        long scannedAtUnix)
    {
        var name = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(name))
        {
            // Trailing-Slash-Fall: nimm den parent directory-Namen
            name = Path.GetFileName(Path.GetDirectoryName(fullPath) ?? fullPath);
            if (string.IsNullOrEmpty(name))
            {
                name = fullPath; // Last resort
            }
        }

        var parent = Path.GetDirectoryName(fullPath);
        int? parentId = null;
        if (!string.IsNullOrEmpty(parent) && folderMap.TryGetValue(parent, out var p))
        {
            parentId = p;
        }

        return new BulkScanWriter.FolderRow(id, libraryId, fullPath, parentId, name, scannedAtUnix);
    }

    private static long ToUnix(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
}
