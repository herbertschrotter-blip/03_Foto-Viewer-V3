using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Data;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Infrastructure;
using PhotoViewerV3.Services.Media;
using PhotoViewerV3.Services.Messaging;
using LibraryEntity = PhotoViewerV3.Domain.Entities.Library; // „Library“ allein wäre der Namespace Services.Library

namespace PhotoViewerV3.Services.Operations;

/// <summary>
/// Datei-Operationen (Papierkorb, Verschieben, Umbenennen, Undo) mit Protokoll in
/// OperationBatches/FileOperations gemäß ADR-0012.
/// Ablauf pro Batch: Batch + alle Items als Pending persistieren → Applying → je Datei erst
/// Dateisystem, dann Katalog (Files, Folders, Thumbnails, FTS) in einer kurzen Transaktion →
/// Applied/Failed mit CompletedAtUtc. Fehler einzelner Dateien brechen den Batch nicht ab;
/// der Batch ist nur Failed, wenn kein Item gelingt.
/// Status-Regel: der Item-Status beschreibt immer den Dateisystem-Zustand. Scheitert nur das
/// Katalog-Update nach erfolgreichem Dateisystem-Schritt, bleibt Applied/Undone stehen und
/// <c>Error</c> trägt den Grund (der nächste Scan heilt den Katalog).
/// Single-Writer: alle Aufrufe laufen nacheinander durch ein SemaphoreSlim; gegenüber anderen
/// Schreibern (ThumbnailService) fängt busy_timeout=5000 SQLITE_BUSY ab.
/// Nachrichten: <see cref="FilesChangedMessage"/> (Moved/Removed/Added) je Library nach Batch-Ende.
/// </summary>
public sealed class FileOperationService : IFileOperationService, IDisposable
{
    private const int QueryChunkSize = 500;
    private const int ErrorMaxLen = 500;

    private readonly ILibraryContextFactory _factory;
    private readonly IAppDataPaths _paths;
    private readonly IRecycleBin _recycleBin;
    private readonly IMediaFormatRegistry _formats;
    private readonly ILogger<FileOperationService> _logger;
    private readonly IMessenger _messenger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public FileOperationService(
        ILibraryContextFactory factory,
        IAppDataPaths paths,
        IRecycleBin recycleBin,
        IMediaFormatRegistry formats,
        ILogger<FileOperationService> logger,
        IMessenger? messenger = null)
    {
        _factory = factory;
        _paths = paths;
        _recycleBin = recycleBin;
        _formats = formats;
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    // ------------------------------------------------------------------ Verträge

    public Task<FileOperationBatchResult> RecycleAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default)
    {
        var ids = NormalizeIds(fileIds);
        return RunBatchAsync(
            OperationType.Recycle,
            ids,
            describe: known => $"{CountText(known.Count)} in den Papierkorb",
            destOf: _ => null,
            prepare: null,
            executeItem: ExecuteRecycleItemAsync,
            ct);
    }

    public Task<FileOperationBatchResult> MoveAsync(IReadOnlyList<int> fileIds, string targetFolderPath, CancellationToken ct = default)
    {
        var ids = NormalizeIds(fileIds);
        if (string.IsNullOrWhiteSpace(targetFolderPath))
        {
            throw new ArgumentException("Der Zielordner darf nicht leer sein.", nameof(targetFolderPath));
        }

        var target = NormalizeDirectoryPath(targetFolderPath);
        var targetFolders = new Dictionary<int, Folder>(); // Zielordner-Zeile je Library (nur innerhalb der Root)

        return RunBatchAsync(
            OperationType.Move,
            ids,
            describe: known => $"{CountText(known.Count)} nach „{target}“ verschoben",
            destOf: cf => Path.Combine(target, cf.File.Name),
            prepare: (ctx, run, token) => PrepareMoveAsync(ctx, run, target, token),
            executeItem: (ctx, run, cf, op, token) => ExecuteMoveItemAsync(ctx, run, cf, op, target, targetFolders, token),
            ct);
    }

    public Task<FileOperationBatchResult> RenameAsync(int fileId, string newName, CancellationToken ct = default)
    {
        var name = ValidateFileName(newName);
        return RunBatchAsync(
            OperationType.Rename,
            new[] { fileId },
            describe: known => $"„{known[0].File.Name}“ in „{name}“ umbenannt",
            destOf: cf => Path.Combine(cf.Folder.Path, name),
            prepare: null,
            executeItem: (ctx, run, cf, op, token) => ExecuteRenameItemAsync(ctx, run, cf, op, name, token),
            ct);
    }

    public async Task<FileOperationBatchResult?> UndoLastAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            var batchId = await ctx.OperationBatches
                .Where(b => b.Status == BatchStatus.Applied
                            && (b.OperationType == OperationType.Move || b.OperationType == OperationType.Rename))
                .OrderByDescending(b => b.Id)
                .Select(b => (int?)b.Id)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            return batchId is null
                ? null
                : await UndoCoreAsync(ctx, batchId.Value, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<FileOperationBatchResult?> UndoBatchAsync(int batchId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            return await UndoCoreAsync(ctx, batchId, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<OperationBatchInfo>> GetRecentBatchesAsync(int take = 50, CancellationToken ct = default)
    {
        take = Math.Max(1, take);
        await using var ctx = _factory.Create();
        var rows = await ctx.OperationBatches
            .AsNoTracking()
            .OrderByDescending(b => b.CreatedAtUtc)
            .ThenByDescending(b => b.Id)
            .Take(take)
            .Select(b => new
            {
                b.Id,
                b.OperationType,
                b.Status,
                b.CreatedAtUtc,
                b.CompletedAtUtc,
                b.Description,
                ItemCount = ctx.FileOperations.Count(o => o.BatchId == b.Id),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .Select(r => new OperationBatchInfo(
                r.Id,
                r.OperationType,
                r.Status,
                r.CreatedAtUtc,
                r.CompletedAtUtc,
                r.ItemCount,
                r.Description,
                CanUndo(r.OperationType, r.Status)))
            .ToList();
    }

    public void Dispose() => _writeLock.Dispose();

    // ------------------------------------------------------------------ Batch-Rahmen

    /// <summary>Katalogdatei inkl. Ordner- und Library-Zeile (für Pfade und Root-Vergleich).</summary>
    private sealed record CatalogFile(MediaFile File, Folder Folder, LibraryEntity Library)
    {
        public string FullPath => Path.Combine(Folder.Path, File.Name);
    }

    /// <summary>Sammelt Katalog-Änderungen je Library und Art für die Nachrichten am Batch-Ende.</summary>
    private sealed class ChangeSet
    {
        private readonly Dictionary<(int LibraryId, FilesChangeKind Kind), List<int>> _map = new();

        public void Add(int libraryId, FilesChangeKind kind, int fileId)
        {
            if (!_map.TryGetValue((libraryId, kind), out var list))
            {
                list = new List<int>();
                _map[(libraryId, kind)] = list;
            }

            list.Add(fileId);
        }

        public IEnumerable<FilesChangedMessage> ToMessages()
            => _map.Select(kv => new FilesChangedMessage(kv.Key.LibraryId, kv.Key.Kind, kv.Value));
    }

    private sealed class BatchRun
    {
        public required OperationBatch Batch { get; init; }
        public required List<(CatalogFile File, FileOperation Op)> Items { get; init; }
        public List<FileOperationItemResult> Results { get; } = new();
        public ChangeSet Changes { get; } = new();
    }

    private delegate Task ItemExecutor(LibraryContext ctx, BatchRun run, CatalogFile cf, FileOperation op, CancellationToken ct);

    /// <summary>
    /// Gemeinsamer Rahmen: Lock → Dateien laden → Batch + Items (Pending) persistieren →
    /// Applying → prepare → Items ausführen → Batch abschließen → Nachrichten senden.
    /// </summary>
    private async Task<FileOperationBatchResult> RunBatchAsync(
        OperationType type,
        IReadOnlyList<int> ids,
        Func<IReadOnlyList<CatalogFile>, string> describe,
        Func<CatalogFile, string?> destOf,
        Func<LibraryContext, BatchRun, CancellationToken, Task<bool>>? prepare,
        ItemExecutor executeItem,
        CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            var (known, unknown) = await LoadFilesAsync(ctx, ids, ct).ConfigureAwait(false);
            if (known.Count == 0)
            {
                return new FileOperationBatchResult(0, type, 0, unknown.Count, unknown);
            }

            var run = await CreateBatchAsync(
                ctx,
                type,
                describe(known),
                known.Select(cf => (cf, cf.FullPath, destOf(cf))),
                ct).ConfigureAwait(false);

            try
            {
                if (prepare is null || await prepare(ctx, run, ct).ConfigureAwait(false))
                {
                    foreach (var (cf, op) in run.Items)
                    {
                        ct.ThrowIfCancellationRequested();
                        await executeItem(ctx, run, cf, op, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                await FinalizeAfterAbortAsync(run.Batch.Id, ex).ConfigureAwait(false);
                throw;
            }

            return await FinishBatchAsync(ctx, run, unknown, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task<BatchRun> CreateBatchAsync(
        LibraryContext ctx,
        OperationType type,
        string description,
        IEnumerable<(CatalogFile File, string Source, string? Dest)> items,
        CancellationToken ct)
    {
        var batch = new OperationBatch
        {
            OperationType = type,
            Status = BatchStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow,
            Description = description,
        };
        var list = new List<(CatalogFile File, FileOperation Op)>();

        // ADR-0012: komplette Operationsliste vor dem ersten Dateisystem-Schritt persistieren.
        await using (var tx = await ctx.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            ctx.OperationBatches.Add(batch);
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false); // Batch-Id

            foreach (var (cf, source, dest) in items)
            {
                var op = new FileOperation
                {
                    BatchId = batch.Id,
                    FileId = cf.File.Id,
                    Action = type,
                    SourcePath = source,
                    DestPath = dest,
                    Status = FileOperationStatus.Pending,
                };
                ctx.FileOperations.Add(op);
                list.Add((cf, op));
            }

            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        batch.Status = BatchStatus.Applying;
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

        return new BatchRun { Batch = batch, Items = list };
    }

    private async Task<FileOperationBatchResult> FinishBatchAsync(
        LibraryContext ctx,
        BatchRun run,
        IReadOnlyList<FileOperationItemResult> unknown,
        CancellationToken ct)
    {
        var succeeded = run.Results.Count(r => r.Success);
        run.Batch.Status = succeeded > 0 ? BatchStatus.Applied : BatchStatus.Failed;
        run.Batch.CompletedAtUtc = DateTime.UtcNow;
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

        Publish(run.Changes);

        var items = run.Results.Concat(unknown).ToList();
        var failed = items.Count - succeeded;
        _logger.LogInformation(
            "Batch {BatchId} ({Type}) abgeschlossen: {Ok} ok, {Failed} fehlgeschlagen — {Description}",
            run.Batch.Id, run.Batch.OperationType, succeeded, failed, run.Batch.Description);

        return new FileOperationBatchResult(run.Batch.Id, run.Batch.OperationType, succeeded, failed, items);
    }

    /// <summary>
    /// Nach einer Ausnahme (inkl. Abbruch) mitten im Batch: offene Items als Failed markieren
    /// und den Batch-Status festschreiben — mit frischem Context, best-effort.
    /// </summary>
    private async Task FinalizeAfterAbortAsync(int batchId, Exception ex)
    {
        _logger.LogError(ex, "Batch {BatchId} abgebrochen", batchId);
        try
        {
            await using var ctx = _factory.Create();
            var now = DateTime.UtcNow;
            var error = Truncate(ex is OperationCanceledException ? "Abgebrochen." : $"Abgebrochen: {ex.Message}");

            await ctx.FileOperations
                .Where(o => o.BatchId == batchId
                            && (o.Status == FileOperationStatus.Pending || o.Status == FileOperationStatus.Applying))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.Status, FileOperationStatus.Failed)
                    .SetProperty(o => o.Error, error)
                    .SetProperty(o => o.ExecutedAtUtc, (DateTime?)now), CancellationToken.None)
                .ConfigureAwait(false);

            var anyApplied = await ctx.FileOperations
                .AnyAsync(o => o.BatchId == batchId && o.Status == FileOperationStatus.Applied, CancellationToken.None)
                .ConfigureAwait(false);
            var status = anyApplied ? BatchStatus.Applied : BatchStatus.Failed;

            await ctx.OperationBatches
                .Where(b => b.Id == batchId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, status)
                    .SetProperty(b => b.CompletedAtUtc, (DateTime?)now), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception inner)
        {
            _logger.LogError(inner, "Batch {BatchId}: Status nach Abbruch konnte nicht gesichert werden", batchId);
        }
    }

    // ------------------------------------------------------------------ Item-Ausführung

    private async Task ExecuteRecycleItemAsync(LibraryContext ctx, BatchRun run, CatalogFile cf, FileOperation op, CancellationToken ct)
    {
        string? hint = null;
        if (File.Exists(op.SourcePath))
        {
            try
            {
                _recycleBin.MoveToRecycleBin(op.SourcePath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await FailItemAsync(ctx, run, cf, op, $"Papierkorb: {ex.Message}", ct).ConfigureAwait(false);
                return;
            }
        }
        else
        {
            hint = "Quelldatei war nicht mehr vorhanden; Katalogeintrag wurde bereinigt.";
        }

        var error = await CommitItemAsync(ctx, op, undo: false, hint, ct,
            () => RemoveCatalogEntryAsync(ctx, cf.File, ct)).ConfigureAwait(false);

        if (error is null)
        {
            DeleteThumbnailFiles(cf.Library.Id, cf.File.Id);
            run.Changes.Add(cf.Library.Id, FilesChangeKind.Removed, cf.File.Id);
        }

        run.Results.Add(new FileOperationItemResult(cf.File.Id, op.SourcePath, null, error is null, error ?? hint));
    }

    /// <summary>Zielordner im Dateisystem anlegen; scheitert das, sind alle Items Failed.</summary>
    private async Task<bool> PrepareMoveAsync(LibraryContext ctx, BatchRun run, string target, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(target);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var (cf, op) in run.Items)
            {
                await FailItemAsync(ctx, run, cf, op, $"Zielordner konnte nicht angelegt werden: {ex.Message}", ct).ConfigureAwait(false);
            }

            return false;
        }
    }

    private async Task ExecuteMoveItemAsync(
        LibraryContext ctx,
        BatchRun run,
        CatalogFile cf,
        FileOperation op,
        string target,
        Dictionary<int, Folder> targetFolders,
        CancellationToken ct)
    {
        var dest = op.DestPath!;

        if (!File.Exists(op.SourcePath))
        {
            await FailItemAsync(ctx, run, cf, op, "Quelldatei nicht gefunden.", ct).ConfigureAwait(false);
            return;
        }

        if (PathEquals(NormalizeDirectoryPath(cf.Folder.Path), target))
        {
            await FailItemAsync(ctx, run, cf, op, "Datei liegt bereits im Zielordner.", ct).ConfigureAwait(false);
            return;
        }

        if (File.Exists(dest))
        {
            await FailItemAsync(ctx, run, cf, op, "Ziel existiert bereits.", ct).ConfigureAwait(false);
            return;
        }

        // Innerhalb der Library-Root: Zielordner-Zeile (+ fehlende Zwischenordner) vor dem Move
        // anlegen — außerhalb der Item-Transaktion, damit ein Rollback keine Ordner-Ids verliert.
        Folder? targetFolder = null;
        if (IsInsideRoot(target, NormalizeDirectoryPath(cf.Library.RootPath)))
        {
            if (!targetFolders.TryGetValue(cf.Library.Id, out targetFolder))
            {
                targetFolder = await EnsureFolderChainAsync(ctx, cf.Library, target, ct).ConfigureAwait(false);
                targetFolders[cf.Library.Id] = targetFolder;
            }

            var folderId = targetFolder.Id;
            var name = cf.File.Name;
            if (await ctx.Files.AnyAsync(f => f.FolderId == folderId && f.Name == name, ct).ConfigureAwait(false))
            {
                await FailItemAsync(ctx, run, cf, op, "Ziel existiert bereits im Katalog.", ct).ConfigureAwait(false);
                return;
            }
        }

        try
        {
            File.Move(op.SourcePath, dest);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await FailItemAsync(ctx, run, cf, op, $"Verschieben fehlgeschlagen: {ex.Message}", ct).ConfigureAwait(false);
            return;
        }

        var error = await CommitItemAsync(ctx, op, undo: false, null, ct, async () =>
        {
            if (targetFolder is not null)
            {
                cf.File.FolderId = targetFolder.Id; // FileId bleibt → Thumbnails + FTS bleiben gültig
            }
            else
            {
                await RemoveCatalogEntryAsync(ctx, cf.File, ct).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);

        if (error is null)
        {
            if (targetFolder is not null)
            {
                run.Changes.Add(cf.Library.Id, FilesChangeKind.Moved, cf.File.Id);
            }
            else
            {
                DeleteThumbnailFiles(cf.Library.Id, cf.File.Id);
                run.Changes.Add(cf.Library.Id, FilesChangeKind.Removed, cf.File.Id);
            }
        }

        run.Results.Add(new FileOperationItemResult(cf.File.Id, op.SourcePath, dest, error is null, error));
    }

    private async Task ExecuteRenameItemAsync(
        LibraryContext ctx,
        BatchRun run,
        CatalogFile cf,
        FileOperation op,
        string name,
        CancellationToken ct)
    {
        var dest = op.DestPath!;
        var oldName = cf.File.Name;
        var oldExt = cf.File.Extension;

        if (string.Equals(oldName, name, StringComparison.Ordinal))
        {
            await FailItemAsync(ctx, run, cf, op, "Der neue Name ist identisch mit dem bisherigen.", ct).ConfigureAwait(false);
            return;
        }

        if (!File.Exists(op.SourcePath))
        {
            await FailItemAsync(ctx, run, cf, op, "Quelldatei nicht gefunden.", ct).ConfigureAwait(false);
            return;
        }

        // Nur Groß-/Kleinschreibung geändert: auf NTFS ist das dieselbe Datei → Existenzprüfung überspringen.
        var caseOnly = string.Equals(oldName, name, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly)
        {
            if (File.Exists(dest))
            {
                await FailItemAsync(ctx, run, cf, op, "Ziel existiert bereits.", ct).ConfigureAwait(false);
                return;
            }

            var folderId = cf.File.FolderId;
            if (await ctx.Files.AnyAsync(f => f.FolderId == folderId && f.Name == name, ct).ConfigureAwait(false))
            {
                await FailItemAsync(ctx, run, cf, op, "Ziel existiert bereits im Katalog.", ct).ConfigureAwait(false);
                return;
            }
        }

        try
        {
            File.Move(op.SourcePath, dest);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await FailItemAsync(ctx, run, cf, op, $"Umbenennen fehlgeschlagen: {ex.Message}", ct).ConfigureAwait(false);
            return;
        }

        var newExt = Path.GetExtension(name).ToLowerInvariant();
        var mediaType = _formats.TryClassify(newExt, out var classified) ? classified : cf.File.MediaType;

        var error = await CommitItemAsync(ctx, op, undo: false, null, ct, async () =>
        {
            await FtsDeleteAsync(ctx, cf.File.Id, oldName, oldExt, ct).ConfigureAwait(false);
            cf.File.Name = name;
            cf.File.Extension = newExt;
            cf.File.MediaType = mediaType;
            await FtsInsertAsync(ctx, cf.File.Id, name, newExt, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (error is null)
        {
            run.Changes.Add(cf.Library.Id, FilesChangeKind.Moved, cf.File.Id);
        }

        run.Results.Add(new FileOperationItemResult(cf.File.Id, op.SourcePath, dest, error is null, error));
    }

    // ------------------------------------------------------------------ Undo

    private async Task<FileOperationBatchResult?> UndoCoreAsync(LibraryContext ctx, int batchId, CancellationToken ct)
    {
        var batch = await ctx.OperationBatches.FirstOrDefaultAsync(b => b.Id == batchId, ct).ConfigureAwait(false);
        if (batch is null)
        {
            return null;
        }

        // Rückwärts: jüngste Operation zuerst.
        var ops = await ctx.FileOperations
            .Where(o => o.BatchId == batchId)
            .OrderByDescending(o => o.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (!CanUndo(batch.OperationType, batch.Status))
        {
            var reason = batch.OperationType is OperationType.Move or OperationType.Rename
                ? $"Batch {batch.Id} hat Status {batch.Status} und ist nicht (mehr) rückgängig machbar."
                : $"Batch {batch.Id} ({batch.OperationType}) ist nicht rückgängig machbar.";
            var items = ops.Count > 0
                ? ops.Select(o => new FileOperationItemResult(o.FileId ?? 0, o.SourcePath, o.DestPath, false, reason)).ToList()
                : new List<FileOperationItemResult> { new(0, string.Empty, null, false, reason) };
            return new FileOperationBatchResult(batch.Id, batch.OperationType, 0, items.Count, items);
        }

        batch.Status = BatchStatus.Undoing;
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

        var results = new List<FileOperationItemResult>();
        var changes = new ChangeSet();
        try
        {
            foreach (var op in ops)
            {
                ct.ThrowIfCancellationRequested();
                if (op.Status != FileOperationStatus.Applied)
                {
                    continue; // Failed/Undone: nichts zurückzunehmen
                }

                results.Add(await UndoItemAsync(ctx, op, changes, ct).ConfigureAwait(false));
            }
        }
        catch (Exception ex)
        {
            await RestoreBatchStatusAfterUndoAbortAsync(batch.Id, ex).ConfigureAwait(false);
            throw;
        }

        // Teilweise zurückgenommen → Undone (offene Items behalten Status Applied + Error);
        // gar nichts zurückgenommen → Applied, erneuter Versuch möglich.
        var undone = results.Count(r => r.Success);
        batch.Status = undone > 0 ? BatchStatus.Undone : BatchStatus.Applied;
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

        Publish(changes);
        _logger.LogInformation(
            "Batch {BatchId} ({Type}) rückgängig: {Ok} ok, {Failed} fehlgeschlagen",
            batch.Id, batch.OperationType, undone, results.Count - undone);

        return new FileOperationBatchResult(batch.Id, batch.OperationType, undone, results.Count - undone, results);
    }

    private async Task<FileOperationItemResult> UndoItemAsync(LibraryContext ctx, FileOperation op, ChangeSet changes, CancellationToken ct)
    {
        var source = op.SourcePath;
        var dest = op.DestPath;

        if (string.IsNullOrEmpty(dest))
        {
            return await UndoFailAsync(ctx, op, "Kein Zielpfad protokolliert.", ct).ConfigureAwait(false);
        }

        if (!File.Exists(dest))
        {
            return await UndoFailAsync(ctx, op, "Datei ist am Zielort nicht mehr vorhanden.", ct).ConfigureAwait(false);
        }

        var caseOnly = string.Equals(dest, source, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && File.Exists(source))
        {
            return await UndoFailAsync(ctx, op, "Ursprungsort ist inzwischen belegt.", ct).ConfigureAwait(false);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.Move(dest, source);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await UndoFailAsync(ctx, op, $"Zurückverschieben fehlgeschlagen: {ex.Message}", ct).ConfigureAwait(false);
        }

        // Katalog: Zeile existiert noch (Move innerhalb / Rename) → FolderId/Name zurück;
        // Zeile fehlt (Move nach außen) → neu anlegen mit Daten aus FileInfo.
        var sourceDir = Path.GetDirectoryName(source)!;
        var sourceName = Path.GetFileName(source);

        var row = op.FileId is int fileId
            ? await ctx.Files.FirstOrDefaultAsync(f => f.Id == fileId, ct).ConfigureAwait(false)
            : null;
        var library = row is not null
            ? await ctx.Libraries.FirstOrDefaultAsync(l => l.Id == row.LibraryId, ct).ConfigureAwait(false)
            : await FindLibraryContainingAsync(ctx, sourceDir, ct).ConfigureAwait(false);

        Folder? folder = null;
        if (library is not null && IsInsideRoot(NormalizeDirectoryPath(sourceDir), NormalizeDirectoryPath(library.RootPath)))
        {
            folder = await EnsureFolderChainAsync(ctx, library, sourceDir, ct).ConfigureAwait(false);
        }

        var resultFileId = op.FileId ?? 0;
        int? movedId = null;
        int? addedId = null;
        string? hint = null;

        var error = await CommitItemAsync(ctx, op, undo: true, null, ct, async () =>
        {
            if (row is not null)
            {
                if (folder is not null)
                {
                    row.FolderId = folder.Id;
                }

                if (!string.Equals(row.Name, sourceName, StringComparison.Ordinal))
                {
                    var ext = Path.GetExtension(sourceName).ToLowerInvariant();
                    await FtsDeleteAsync(ctx, row.Id, row.Name, row.Extension, ct).ConfigureAwait(false);
                    row.Name = sourceName;
                    row.Extension = ext;
                    row.MediaType = _formats.TryClassify(ext, out var mt) ? mt : row.MediaType;
                    await FtsInsertAsync(ctx, row.Id, row.Name, row.Extension, ct).ConfigureAwait(false);
                }

                movedId = row.Id;
            }
            else if (library is not null && folder is not null)
            {
                var created = await InsertCatalogRowAsync(ctx, library, folder, source, ct).ConfigureAwait(false);
                if (created is not null)
                {
                    addedId = created.Id;
                }
                else
                {
                    hint = "Datei wiederhergestellt, aber nicht im Katalog (Endung nicht registriert).";
                }
            }
            else
            {
                hint = "Datei wiederhergestellt, liegt aber außerhalb jeder Library (kein Katalogeintrag).";
            }
        }).ConfigureAwait(false);

        if (error is null)
        {
            if (movedId is int m)
            {
                changes.Add(row!.LibraryId, FilesChangeKind.Moved, m);
            }

            if (addedId is int a)
            {
                changes.Add(library!.Id, FilesChangeKind.Added, a);
                resultFileId = a;
            }
        }

        return new FileOperationItemResult(resultFileId, source, dest, error is null, error ?? hint);
    }

    private async Task<FileOperationItemResult> UndoFailAsync(LibraryContext ctx, FileOperation op, string message, CancellationToken ct)
    {
        // Status bleibt Applied (nichts passiert) — Error dokumentiert den gescheiterten Undo-Versuch.
        op.Error = Truncate(message);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogWarning("Undo fehlgeschlagen: {Dest} → {Message}", op.DestPath, message);
        return new FileOperationItemResult(op.FileId ?? 0, op.SourcePath, op.DestPath, false, message);
    }

    private async Task RestoreBatchStatusAfterUndoAbortAsync(int batchId, Exception ex)
    {
        _logger.LogError(ex, "Undo von Batch {BatchId} abgebrochen", batchId);
        try
        {
            await using var ctx = _factory.Create();
            var anyApplied = await ctx.FileOperations
                .AnyAsync(o => o.BatchId == batchId && o.Status == FileOperationStatus.Applied, CancellationToken.None)
                .ConfigureAwait(false);
            var status = anyApplied ? BatchStatus.Applied : BatchStatus.Undone;
            await ctx.OperationBatches
                .Where(b => b.Id == batchId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, status), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception inner)
        {
            _logger.LogError(inner, "Batch {BatchId}: Status nach Undo-Abbruch konnte nicht gesichert werden", batchId);
        }
    }

    // ------------------------------------------------------------------ Item-Helfer

    /// <summary>
    /// Katalog-Teil eines Items in einer kurzen Transaktion; danach Item-Status Applied bzw. Undone.
    /// Scheitert der Katalog-Teil, wird der Tracker zurückgesetzt, der Status trotzdem gesetzt
    /// (Dateisystem-Schritt ist ja erfolgt) und der Fehler als Text zurückgegeben.
    /// </summary>
    private async Task<string?> CommitItemAsync(
        LibraryContext ctx,
        FileOperation op,
        bool undo,
        string? hint,
        CancellationToken ct,
        Func<Task> catalogWork)
    {
        try
        {
            await using var tx = await ctx.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            await catalogWork().ConfigureAwait(false);
            MarkDone(op, undo, hint);
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DiscardTrackedChanges(ctx);
            var message = Truncate(
                $"Katalog-Update fehlgeschlagen (Datei wurde bereits {(undo ? "zurückverschoben" : "verarbeitet")}): {ex.Message}");
            _logger.LogWarning(ex, "Katalog-Update fehlgeschlagen: Op {OpId} {Source}", op.Id, op.SourcePath);
            MarkDone(op, undo, message);
            await ctx.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            return message;
        }
    }

    private static void MarkDone(FileOperation op, bool undo, string? error)
    {
        var now = DateTime.UtcNow;
        op.Error = error;
        if (undo)
        {
            op.Status = FileOperationStatus.Undone;
            op.UndoneAtUtc = now;
        }
        else
        {
            op.Status = FileOperationStatus.Applied;
            op.ExecutedAtUtc = now;
        }
    }

    private async Task FailItemAsync(LibraryContext ctx, BatchRun run, CatalogFile cf, FileOperation op, string message, CancellationToken ct)
    {
        op.Status = FileOperationStatus.Failed;
        op.Error = Truncate(message);
        op.ExecutedAtUtc = DateTime.UtcNow;
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogWarning("Item fehlgeschlagen: {Source} → {Message}", op.SourcePath, message);
        run.Results.Add(new FileOperationItemResult(cf.File.Id, op.SourcePath, op.DestPath, false, message));
    }

    /// <summary>Setzt alle offenen Änderungen im Tracker zurück (nach Rollback einer Item-Transaktion).</summary>
    private static void DiscardTrackedChanges(LibraryContext ctx)
    {
        foreach (var entry in ctx.ChangeTracker.Entries().ToList())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.State = EntityState.Detached;
                    break;
                case EntityState.Modified:
                    entry.CurrentValues.SetValues(entry.OriginalValues);
                    entry.State = EntityState.Unchanged;
                    break;
                case EntityState.Deleted:
                    entry.State = EntityState.Unchanged;
                    break;
            }
        }
    }

    // ------------------------------------------------------------------ Katalog

    private async Task<(List<CatalogFile> Known, List<FileOperationItemResult> Unknown)> LoadFilesAsync(
        LibraryContext ctx,
        IReadOnlyList<int> ids,
        CancellationToken ct)
    {
        var found = new Dictionary<int, CatalogFile>();
        foreach (var chunk in ids.Chunk(QueryChunkSize))
        {
            var rows = await ctx.Files
                .Where(f => chunk.Contains(f.Id))
                .Join(ctx.Folders, f => f.FolderId, d => d.Id, (f, d) => new { File = f, Folder = d })
                .Join(ctx.Libraries, x => x.File.LibraryId, l => l.Id, (x, l) => new { x.File, x.Folder, Library = l })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var r in rows)
            {
                found[r.File.Id] = new CatalogFile(r.File, r.Folder, r.Library);
            }
        }

        var known = new List<CatalogFile>(ids.Count);
        var unknown = new List<FileOperationItemResult>();
        foreach (var id in ids)
        {
            if (found.TryGetValue(id, out var cf))
            {
                known.Add(cf);
            }
            else
            {
                unknown.Add(new FileOperationItemResult(id, string.Empty, null, false, "Datei nicht im Katalog."));
                _logger.LogWarning("FileId {FileId} nicht im Katalog (oder Ordner/Library fehlt)", id);
            }
        }

        return (known, unknown);
    }

    /// <summary>FTS-Eintrag (alte Werte), Thumbnails-Zeilen und Files-Zeile entfernen — innerhalb der laufenden Transaktion.</summary>
    private static async Task RemoveCatalogEntryAsync(LibraryContext ctx, MediaFile file, CancellationToken ct)
    {
        await FtsDeleteAsync(ctx, file.Id, file.Name, file.Extension, ct).ConfigureAwait(false);
        var fileId = file.Id;
        await ctx.Thumbnails.Where(t => t.FileId == fileId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        ctx.Files.Remove(file);
    }

    /// <summary>Legt eine Files-Zeile aus FileInfo neu an (Undo eines Moves nach außen) inkl. FTS-Insert; null bei unbekannter Endung.</summary>
    private async Task<MediaFile?> InsertCatalogRowAsync(LibraryContext ctx, LibraryEntity library, Folder folder, string fullPath, CancellationToken ct)
    {
        var info = new FileInfo(fullPath);
        var ext = info.Extension.ToLowerInvariant();
        if (!_formats.TryClassify(ext, out var mediaType))
        {
            return null;
        }

        var name = info.Name;
        var folderId = folder.Id;
        var existing = await ctx.Files.FirstOrDefaultAsync(f => f.FolderId == folderId && f.Name == name, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var row = new MediaFile
        {
            LibraryId = library.Id,
            FolderId = folder.Id,
            Name = name,
            Extension = ext,
            Size = info.Length,
            CreatedAtUtc = info.CreationTimeUtc,
            ModifiedAtUtc = info.LastWriteTimeUtc,
            MediaType = mediaType,
            IsFavorite = false,
            Rating = null,
            Missing = false,
        };
        ctx.Files.Add(row);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false); // neue Id
        await FtsInsertAsync(ctx, row.Id, row.Name, row.Extension, ct).ConfigureAwait(false);
        return row;
    }

    private static Task FtsDeleteAsync(LibraryContext ctx, int id, string name, string extension, CancellationToken ct)
        => ctx.Database.ExecuteSqlAsync(
            $"INSERT INTO FilesFts(FilesFts, rowid, Name, Extension) VALUES('delete', {id}, {name}, {extension})", ct);

    private static Task FtsInsertAsync(LibraryContext ctx, int id, string name, string extension, CancellationToken ct)
        => ctx.Database.ExecuteSqlAsync(
            $"INSERT INTO FilesFts(rowid, Name, Extension) VALUES({id}, {name}, {extension})", ct);

    /// <summary>
    /// Stellt die Folders-Kette von der Library-Root bis <paramref name="folderPath"/> sicher
    /// (Path, ParentId-Kette, Name, ScannedAt) und liefert die Zeile des Zielordners.
    /// Vergleich ASCII-case-insensitiv (wie SQLite <c>lower()</c>); erwartet eine normalisierte Library-Root.
    /// </summary>
    private static async Task<Folder> EnsureFolderChainAsync(LibraryContext ctx, LibraryEntity library, string folderPath, CancellationToken ct)
    {
        var root = NormalizeDirectoryPath(library.RootPath);
        var target = NormalizeDirectoryPath(folderPath);
        if (!IsInsideRoot(target, root))
        {
            throw new InvalidOperationException($"„{target}“ liegt nicht unterhalb der Library-Root „{root}“.");
        }

        var chain = new List<string> { root };
        if (!PathEquals(target, root))
        {
            var relative = target[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var current = root;
            foreach (var segment in relative.Split(
                         new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                chain.Add(current);
            }
        }

        var candidates = chain.Select(AsciiLower).Append(AsciiLower(library.RootPath)).Distinct().ToList();
        var libraryId = library.Id;
        var existing = await ctx.Folders
            .Where(f => f.LibraryId == libraryId && candidates.Contains(f.Path.ToLower()))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        Folder? parent = null;
        foreach (var path in chain)
        {
            var row = existing.FirstOrDefault(f => PathEquals(NormalizeDirectoryPath(f.Path), path));
            if (row is null)
            {
                row = new Folder
                {
                    LibraryId = library.Id,
                    Path = path,
                    ParentId = parent?.Id,
                    Name = FolderNameOf(path),
                    ScannedAt = DateTime.UtcNow,
                };
                ctx.Folders.Add(row);
                await ctx.SaveChangesAsync(ct).ConfigureAwait(false); // Id für die ParentId-Kette
                existing.Add(row);
            }

            parent = row;
        }

        return parent!;
    }

    private static async Task<LibraryEntity?> FindLibraryContainingAsync(LibraryContext ctx, string directory, CancellationToken ct)
    {
        var dir = NormalizeDirectoryPath(directory);
        var libraries = await ctx.Libraries.ToListAsync(ct).ConfigureAwait(false);
        return libraries
            .Where(l => IsInsideRoot(dir, NormalizeDirectoryPath(l.RootPath)))
            .OrderByDescending(l => l.RootPath.Length)
            .FirstOrDefault();
    }

    private void DeleteThumbnailFiles(int libraryId, int fileId)
    {
        var dir = _paths.GetLibraryThumbnailsPath(libraryId);
        if (!Directory.Exists(dir))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, $"{fileId}_*"))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Thumbnail-Datei konnte nicht gelöscht werden: {Path}", file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Thumbnail-Cache nicht lesbar: {Dir}", dir);
        }
    }

    private void Publish(ChangeSet changes)
    {
        foreach (var message in changes.ToMessages())
        {
            try
            {
                _messenger.Send(message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Empfänger der FilesChangedMessage ({Kind}) warf eine Ausnahme", message.Kind);
            }
        }
    }

    // ------------------------------------------------------------------ Validierung / Pfade

    private static IReadOnlyList<int> NormalizeIds(IReadOnlyList<int> fileIds)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        var ids = fileIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            throw new ArgumentException("Es wurden keine Dateien angegeben.", nameof(fileIds));
        }

        return ids;
    }

    private static string ValidateFileName(string? newName)
    {
        var name = newName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("Der Dateiname darf nicht leer sein.", nameof(newName));
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
        {
            throw new ArgumentException("Der Dateiname enthält ungültige Zeichen.", nameof(newName));
        }

        if (name is "." or ".." || name.EndsWith('.'))
        {
            throw new ArgumentException("Der Dateiname ist ungültig.", nameof(newName));
        }

        return name;
    }

    private static bool CanUndo(OperationType type, BatchStatus status)
        => status == BatchStatus.Applied && type is OperationType.Move or OperationType.Rename;

    /// <summary>Voller Pfad ohne abschließende Trennzeichen; Laufwerks-/UNC-Wurzeln behalten ihres („E:\“).</summary>
    private static string NormalizeDirectoryPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (root is not null && string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            return full;
        }

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsInsideRoot(string normalizedPath, string normalizedRoot)
    {
        if (PathEquals(normalizedPath, normalizedRoot))
        {
            return true;
        }

        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string FolderNameOf(string path)
    {
        var name = Path.GetFileName(path);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    /// <summary>Nur A–Z falten — exakt das Verhalten von SQLite <c>lower()</c> ohne ICU.</summary>
    private static string AsciiLower(string value)
        => string.Create(value.Length, value, static (span, src) =>
        {
            for (var i = 0; i < src.Length; i++)
            {
                var c = src[i];
                span[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
            }
        });

    private static string CountText(int count) => count == 1 ? "1 Datei" : $"{count} Dateien";

    private static string Truncate(string value)
        => value.Length > ErrorMaxLen ? value[..ErrorMaxLen] : value;
}
