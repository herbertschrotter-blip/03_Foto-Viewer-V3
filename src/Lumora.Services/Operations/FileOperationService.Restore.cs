using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Lumora.Data;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Messaging;
using Lumora.Services.Watch;

namespace Lumora.Services.Operations;

/// <summary>
/// Zurückholen aus dem Papierkorb (ADR-0053, Rückgängig von „Übrige in den Papierkorb“ der Seite „Doppelte“): je Datei des
/// Recycle-Batches erst die Folders-Kette des Ursprungsordners, dann die Datei aus dem Windows-Papierkorb an ihren Platz
/// (<see cref="IRecycleBin.RestoreFromRecycleBin"/>), dann in einer Item-Transaktion die Katalogzeile — über
/// <see cref="RecycleRestoreCatalog"/> mit alter FileId und allen Angaben oder als neue Zeile — samt FTS-Eintrag.
/// Dieselben Regeln wie beim Undo (ADR-0012): Single-Writer, Scan-Sperre der betroffenen Bibliotheken, eigene Pfade für
/// die Ordnerüberwachung, Item danach Undone, Batch Undone sobald eines gelang, <see cref="FilesChangedMessage"/> (Added).
/// Die Galerie-Taste „Rückgängig“ bleibt davon unberührt (Papierkorb-Batches sind dort weiter nicht rückgängig zu machen).
/// </summary>
public sealed partial class FileOperationService
{
    public async Task<FileOperationBatchResult?> RestoreRecycledAsync(int batchId, RecycleRestoreCatalog? restoreCatalog = null, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            var batch = await ctx.OperationBatches.FirstOrDefaultAsync(b => b.Id == batchId, ct).ConfigureAwait(false);
            if (batch is null)
            {
                return null;
            }

            var ops = await ctx.FileOperations
                .Where(o => o.BatchId == batchId)
                .OrderByDescending(o => o.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            // Undone mit noch offenen Items: ein früherer Versuch holte nur einen Teil zurück — der Rest darf erneut versucht werden
            var retry = batch.Status == BatchStatus.Undone && ops.Any(o => o.Status == FileOperationStatus.Applied);
            if (batch.OperationType != OperationType.Recycle || (batch.Status != BatchStatus.Applied && !retry))
            {
                var reason = $"Batch {batch.Id} ({batch.OperationType}, {batch.Status}) kann nicht aus dem Papierkorb zurückgeholt werden.";
                var failed = ops.Select(o => new FileOperationItemResult(o.FileId ?? 0, o.SourcePath, null, false, reason)).ToList();
                return new FileOperationBatchResult(batch.Id, batch.OperationType, 0, failed.Count, failed);
            }

            var directories = ops
                .Select(o => Path.GetDirectoryName(o.SourcePath))
                .Where(d => !string.IsNullOrEmpty(d))
                .Select(d => d!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var libraries = await LibrariesContainingAsync(ctx, directories, ct).ConfigureAwait(false);
            using var lease = await AcquireLibrariesAsync(libraries, ct).ConfigureAwait(false);
            using var own = _ownChanges.Begin();

            batch.Status = BatchStatus.Undoing;
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            var results = new List<FileOperationItemResult>();
            var changes = new ChangeSet();
            try
            {
                foreach (var op in ops)
                {
                    ct.ThrowIfCancellationRequested();
                    if (op.Status == FileOperationStatus.Applied)
                    {
                        results.Add(await RestoreItemAsync(ctx, batch, op, restoreCatalog, changes, own, ct).ConfigureAwait(false));
                    }
                }
            }
            catch (Exception ex)
            {
                await RestoreBatchStatusAfterUndoAbortAsync(batch.Id, ex).ConfigureAwait(false);
                Publish(changes);
                throw;
            }

            var restored = results.Count(r => r.Success);
            batch.Status = restored > 0 || retry ? BatchStatus.Undone : BatchStatus.Applied;
            await ctx.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            Publish(changes);
            _logger.LogInformation("Batch {BatchId} aus dem Papierkorb zurückgeholt: {Ok} ok, {Failed} fehlgeschlagen", batch.Id, restored, results.Count - restored);
            return new FileOperationBatchResult(batch.Id, batch.OperationType, restored, results.Count - restored, results);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<FileOperationItemResult> RestoreItemAsync(
        LibraryContext ctx,
        OperationBatch batch,
        FileOperation op,
        RecycleRestoreCatalog? restoreCatalog,
        ChangeSet changes,
        IOwnChangeScope own,
        CancellationToken ct)
    {
        var source = op.SourcePath;
        if (File.Exists(source))
        {
            return await UndoFailAsync(ctx, op, "Ursprungsort ist inzwischen belegt.", ct).ConfigureAwait(false);
        }

        var directory = Path.GetDirectoryName(source)!;
        var root = await FindRootContainingAsync(ctx, null, directory, ct).ConfigureAwait(false);
        Folder? folder = null;
        if (root is not null)
        {
            folder = await EnsureFolderChainAsync(ctx, root, directory, ct).ConfigureAwait(false);
        }

        var ownPaths = new List<string>();
        if (folder is not null)
        {
            ownPaths.AddRange(MissingDirectoryChain(directory));
            ownPaths.Add(source);
        }

        AddAll(own, ownPaths);
        try
        {
            Directory.CreateDirectory(directory);
            _recycleBin.RestoreFromRecycleBin(source, batch.CreatedAtUtc);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RevokeAll(own, ownPaths);
            return await UndoFailAsync(ctx, op, $"Aus dem Papierkorb zurückholen fehlgeschlagen: {ex.Message}", ct).ConfigureAwait(false);
        }

        int? restoredId = null;
        MediaFile? created = null;
        string? hint = null;
        var error = await CommitItemAsync(ctx, op, undo: true, null, async token =>
        {
            if (root is null || folder is null)
            {
                hint = "Datei zurückgeholt, liegt aber außerhalb jeder Bibliothek (kein Katalogeintrag).";
                return;
            }

            if (restoreCatalog is not null)
            {
                var connection = (SqliteConnection)ctx.Database.GetDbConnection();
                var transaction = (SqliteTransaction)ctx.Database.CurrentTransaction!.GetDbTransaction();
                if (restoreCatalog(connection, transaction, new RecycledFile(op.FileId, source, root.LibraryId, folder.Id)) is { } id)
                {
                    var row = await ctx.Files.AsNoTracking().Where(f => f.Id == id).Select(f => new { f.Name, f.Extension }).FirstAsync(token).ConfigureAwait(false);
                    await FtsInsertAsync(ctx, id, row.Name, row.Extension, token).ConfigureAwait(false);
                    restoredId = id;
                    return;
                }
            }

            var library = await ctx.Libraries.FirstAsync(l => l.Id == root.LibraryId, token).ConfigureAwait(false);
            created = await InsertCatalogRowAsync(ctx, library, folder, source, token).ConfigureAwait(false);
            if (created is not null)
            {
                restoredId = created.Id;
            }
            else
            {
                hint = "Datei zurückgeholt, aber nicht im Katalog (Endung nicht registriert).";
            }
        }).ConfigureAwait(false);

        if (error is not null)
        {
            RevokeAll(own, ownPaths);
            if (created is not null)
            {
                ctx.Entry(created).State = EntityState.Detached;
            }
        }
        else if (restoredId is int added && root is not null)
        {
            changes.Add(root.LibraryId, FilesChangeKind.Added, added);
        }

        return new FileOperationItemResult(restoredId ?? op.FileId ?? 0, source, null, error is null, error ?? hint);
    }
}
