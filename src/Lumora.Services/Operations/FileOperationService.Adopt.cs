using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lumora.Data;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Messaging;
using Lumora.Services.Watch;
using LibraryEntity = Lumora.Domain.Entities.Library;

namespace Lumora.Services.Operations;

/// <summary>
/// „Ordner in die Bibliothek übernehmen“ (ADR-0034) als ein protokollierter AdoptFolder-Batch (ADR-0012) unter der
/// Scan-Sperre (ADR-0025). Protokoll in Id-Reihenfolge: Ordner-Operation (Quelle → Zielordner), MkDir je neu angelegtem
/// Ordner (flach → tief), Move je Datei, optional Recycle des Quellordners. Undo läuft rückwärts: Dateien zurück (Ordner
/// werden dabei neu angelegt), leer gewordene Zielordner entfernen, ein ausgetragener Eingang wird wieder eingetragen.
/// </summary>
public sealed partial class FileOperationService
{
    public async Task<FolderAdoptionExecution> AdoptFolderAsync(
        int libraryId,
        FolderAdoptionPlan plan,
        bool recycleSource,
        bool keepSourceFolder = false,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.IsEmpty)
        {
            throw new ArgumentException("Der Ordner enthält keine Dateien zum Übernehmen.", nameof(plan));
        }

        var source = NormalizeDirectoryPath(plan.SourceFolder);
        var target = NormalizeDirectoryPath(plan.TargetFolder);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var lease = await AcquireLibrariesAsync([libraryId], ct).ConfigureAwait(false);
            using var own = _ownChanges.Begin();
            await using var ctx = _factory.Create();

            var library = await ctx.Libraries.FirstOrDefaultAsync(l => l.Id == libraryId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Bibliothek Id={libraryId} nicht im Katalog.");
            var roots = await ctx.LibraryRoots.AsNoTracking().Where(r => r.LibraryId == libraryId).ToListAsync(ct).ConfigureAwait(false);
            var targetRoot = FindRootContaining(roots, libraryId, target);
            if (targetRoot is not { Role: LibraryRootRole.Archive })
            {
                throw new InvalidOperationException($"Der Zielordner „{target}“ liegt nicht im Bibliotheksordner.");
            }

            var sourceRoot = FindRootContaining(roots, libraryId, source);
            if (sourceRoot is not { Role: LibraryRootRole.Inbox })
            {
                throw new InvalidOperationException($"Der Ordner „{source}“ liegt in keinem Eingang der Bibliothek.");
            }

            if (!Directory.Exists(source))
            {
                throw new DirectoryNotFoundException($"Der Ordner „{source}“ ist nicht (mehr) vorhanden.");
            }

            var rows = await LoadRowsUnderAsync(ctx, libraryId, source, ct).ConfigureAwait(false);

            // Neu anzulegende Ordner flach → tief (auch fehlende Zwischenordner bis zum vorhandenen Teil des Ziels)
            var created = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in plan.Folders)
            {
                foreach (var dir in Enumerable.Reverse(MissingDirectoryChain(NormalizeDirectoryPath(folder.TargetPath))))
                {
                    if (seen.Add(dir))
                    {
                        created.Add(dir);
                    }
                }
            }

            var batch = new OperationBatch
            {
                OperationType = OperationType.AdoptFolder,
                Status = BatchStatus.Pending,
                CreatedAtUtc = DateTime.UtcNow,
                Description = $"Ordner „{FolderNameOf(source)}“ in die Bibliothek übernommen ({CountText(plan.FileCount)}) → „{target}“",
            };
            var folderOp = new FileOperation { Action = OperationType.AdoptFolder, SourcePath = source, DestPath = target, Status = FileOperationStatus.Pending };
            var mkdirOps = created.Select(d => new FileOperation { Action = OperationType.MkDir, SourcePath = d, Status = FileOperationStatus.Pending }).ToList();
            var fileOps = plan.Files.Select(f => (Plan: f, Row: rows.GetValueOrDefault(f.SourcePath), Op: new FileOperation
            {
                Action = OperationType.Move,
                FileId = rows.GetValueOrDefault(f.SourcePath)?.Id,
                SourcePath = f.SourcePath,
                DestPath = f.DestPath,
                Status = FileOperationStatus.Pending,
            })).ToList();
            var recycleOp = recycleSource
                ? new FileOperation { Action = OperationType.Recycle, SourcePath = source, Status = FileOperationStatus.Pending }
                : null;

            // ADR-0012: komplette Operationsliste vor dem ersten Dateisystem-Schritt
            await using (var tx = await ctx.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
            {
                ctx.OperationBatches.Add(batch);
                await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                foreach (var op in mkdirOps.Prepend(folderOp).Concat(fileOps.Select(f => f.Op)))
                {
                    op.BatchId = batch.Id;
                    ctx.FileOperations.Add(op);
                }

                if (recycleOp is not null)
                {
                    recycleOp.BatchId = batch.Id;
                    ctx.FileOperations.Add(recycleOp);
                }

                await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }

            // Datei-Operationen nicht dauerhaft verfolgen: jedes SaveChanges prüft sonst alle (bei 50k Dateien quadratisch);
            // AdoptFileAsync hängt Operation und Katalogzeile nur für die Dauer ihres Items an.
            foreach (var (_, _, op) in fileOps)
            {
                ctx.Entry(op).State = EntityState.Detached;
            }

            batch.Status = BatchStatus.Applying;
            await MarkApplyingAsync(ctx, folderOp, ct).ConfigureAwait(false);
            var run = new BatchRun { Batch = batch, Items = new(), Roots = roots, Own = own };
            var moved = 0;
            var failed = 0;
            var renamed = 0;
            try
            {
                foreach (var op in mkdirOps)
                {
                    own.Add(op.SourcePath);
                    try
                    {
                        Directory.CreateDirectory(op.SourcePath);
                        MarkDone(op, undo: false, null);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        own.Revoke(op.SourcePath);
                        op.Status = FileOperationStatus.Failed;
                        op.Error = Truncate($"Ordner konnte nicht angelegt werden: {ex.Message}");
                        op.ExecutedAtUtc = DateTime.UtcNow;
                    }
                }

                await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

                var folderRows = new Dictionary<string, Folder>(StringComparer.OrdinalIgnoreCase);
                var done = 0;
                foreach (var (filePlan, row, op) in fileOps)
                {
                    ct.ThrowIfCancellationRequested();
                    var result = await AdoptFileAsync(ctx, library, targetRoot, op, row, folderRows, run, ct).ConfigureAwait(false);
                    run.Results.Add(result);
                    if (result.Success)
                    {
                        moved++;
                        renamed += filePlan.Renamed ? 1 : 0;
                    }
                    else
                    {
                        failed++;
                    }

                    progress?.Report(++done);
                }
            }
            catch (Exception ex)
            {
                await FinalizeAfterAbortAsync(run, ex).ConfigureAwait(false);
                throw;
            }

            await MoveFolderLinksAsync(ctx, libraryId, plan).ConfigureAwait(false); // Verknüpfungen in übernommene Ordner (ADR-0054)

            // Quellordner: nur ohne verbliebene Dateien in den Papierkorb (nie Hard-Delete)
            var remaining = FolderAdoptionSourceReader.FindRemainingFiles(source);
            var recycled = false;
            string? note = null;
            if (recycleOp is not null)
            {
                if (failed > 0 || remaining.Count > 0)
                {
                    note = remaining.Count > 0
                        ? $"Quellordner bleibt: noch {CountText(remaining.Count)} darin."
                        : $"Quellordner bleibt: {CountText(failed)} nicht übernommen.";
                    recycleOp.Status = FileOperationStatus.Failed;
                    recycleOp.Error = Truncate(note);
                    recycleOp.ExecutedAtUtc = DateTime.UtcNow;
                    await ctx.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    (recycled, note) = await RecycleAdoptedSourceAsync(ctx, libraryId, recycleOp, source, keepSourceFolder, run).ConfigureAwait(false);
                }
            }

            if (moved == 0)
            {
                // Nichts verschoben: der Batch ist nicht rückgängig machbar — angelegte (leere) Zielordner gleich wieder weg.
                foreach (var op in Enumerable.Reverse(mkdirOps).Where(o => o.Status == FileOperationStatus.Applied))
                {
                    await UndoCreatedDirectoryAsync(ctx, op, own).ConfigureAwait(false);
                }
            }

            MarkDone(folderOp, undo: false, null);
            batch.Status = moved > 0 ? BatchStatus.Applied : BatchStatus.Failed;
            batch.CompletedAtUtc = DateTime.UtcNow;
            await ctx.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            Publish(run.Changes);
            PublishFoldersChanged(libraryId);
            _logger.LogInformation(
                "Ordner übernommen: {Source} → {Target} — {Moved} verschoben ({Renamed} umbenannt), {Failed} fehlgeschlagen, {Created} Ordner angelegt, Papierkorb={Recycled}, Rest={Remaining} (Batch {BatchId})",
                source, target, moved, renamed, failed, created.Count, recycled, remaining.Count, batch.Id);
            return new FolderAdoptionExecution(batch.Id, moved, failed, renamed, recycled, note, remaining, run.Results)
            {
                RecycleFailed = recycleOp is not null && !recycled,
            };
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Ordner-Verknüpfungen der Quellordner in die Zielordner umhängen (ADR-0054); ein Fehler bricht die Übernahme nicht ab.</summary>
    private async Task MoveFolderLinksAsync(LibraryContext ctx, int libraryId, FolderAdoptionPlan plan)
    {
        try
        {
            await Albums.FolderLinkAdoption.MoveLinksAsync(ctx, libraryId, plan, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Übernahme: Ordner-Verknüpfungen konnten nicht umgehängt werden");
        }
    }

    /// <summary>Katalogzeilen aller Dateien unter dem Quellordner (nicht verfolgt), Schlüssel = vollständiger Pfad.</summary>
    private static async Task<Dictionary<string, MediaFile>> LoadRowsUnderAsync(LibraryContext ctx, int libraryId, string folder, CancellationToken ct)
    {
        var lower = AsciiLower(folder);
        var prefix = AsciiLower(folder + Path.DirectorySeparatorChar);
        var folders = await ctx.Folders.AsNoTracking()
            .Where(f => f.LibraryId == libraryId && (f.Path.ToLower() == lower || f.Path.ToLower().StartsWith(prefix)))
            .Select(f => new { f.Id, f.Path })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var pathById = folders.ToDictionary(f => f.Id, f => NormalizeDirectoryPath(f.Path));
        var result = new Dictionary<string, MediaFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in pathById.Keys.Chunk(QueryChunkSize))
        {
            var files = await ctx.Files.AsNoTracking().Where(f => chunk.Contains(f.FolderId)).ToListAsync(ct).ConfigureAwait(false);
            foreach (var file in files)
            {
                result[Path.Combine(pathById[file.FolderId], file.Name)] = file;
            }
        }

        return result;
    }

    /// <summary>Ein Item: Operation und Katalogzeile werden nur für seine Dauer verfolgt (siehe Aufrufer).</summary>
    private async Task<FileOperationItemResult> AdoptFileAsync(
        LibraryContext ctx,
        LibraryEntity library,
        LibraryRoot targetRoot,
        FileOperation op,
        MediaFile? row,
        Dictionary<string, Folder> folderRows,
        BatchRun run,
        CancellationToken ct)
    {
        ctx.FileOperations.Attach(op);
        if (row is not null)
        {
            ctx.Files.Attach(row);
        }

        try
        {
            return await AdoptFileCoreAsync(ctx, library, targetRoot, op, row, folderRows, run, ct).ConfigureAwait(false);
        }
        finally
        {
            // Nur die Item-Objekte lösen (Ordner-, MkDir- und Papierkorb-Operation des Batches bleiben verfolgt).
            ctx.Entry(op).State = EntityState.Detached;
            foreach (var entry in ctx.ChangeTracker.Entries<MediaFile>().Where(e => e.State == EntityState.Unchanged).ToList())
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    private async Task<FileOperationItemResult> AdoptFileCoreAsync(
        LibraryContext ctx,
        LibraryEntity library,
        LibraryRoot targetRoot,
        FileOperation op,
        MediaFile? row,
        Dictionary<string, Folder> folderRows,
        BatchRun run,
        CancellationToken ct)
    {
        var source = op.SourcePath;
        var dest = op.DestPath!;
        var destDir = Path.GetDirectoryName(dest)!;
        var destName = Path.GetFileName(dest);
        var ext = Path.GetExtension(destName).ToLowerInvariant();
        var catalogued = _formats.TryClassify(ext, out var mediaType);

        if (!File.Exists(source))
        {
            return await FailAdoptItemAsync(ctx, op, row?.Id, "Quelldatei nicht gefunden.", ct).ConfigureAwait(false);
        }

        if (File.Exists(dest))
        {
            return await FailAdoptItemAsync(ctx, op, row?.Id, "Ziel existiert bereits.", ct).ConfigureAwait(false);
        }

        if (!Directory.Exists(destDir))
        {
            return await FailAdoptItemAsync(ctx, op, row?.Id, "Zielordner konnte nicht angelegt werden.", ct).ConfigureAwait(false);
        }

        Folder? folder = null;
        if (row is not null || catalogued)
        {
            if (!folderRows.TryGetValue(destDir, out folder))
            {
                folder = await EnsureFolderChainAsync(ctx, targetRoot, destDir, ct).ConfigureAwait(false);
                folderRows[destDir] = folder;
            }

            var folderId = folder.Id;
            if (await ctx.Files.AnyAsync(f => f.FolderId == folderId && f.Name == destName, ct).ConfigureAwait(false))
            {
                return await FailAdoptItemAsync(ctx, op, row?.Id, "Ziel existiert bereits im Katalog.", ct).ConfigureAwait(false);
            }
        }

        // Eigene Pfade: Quelle und Ziel nur, wenn der Katalog sie bucht — eine Nicht-Mediendatei bucht er nicht, ihr Ziel
        // bleibt aber ohne Bedeutung für den Katalog (die Überwachung filtert Nicht-Medien ohnehin).
        string[] ownPaths = [source, dest];
        await MarkApplyingAsync(ctx, op, ct).ConfigureAwait(false);
        AddAll(run.Own, ownPaths);
        try
        {
            File.Move(source, dest);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RevokeAll(run.Own, ownPaths);
            return await FailAdoptItemAsync(ctx, op, row?.Id, $"Verschieben fehlgeschlagen: {ex.Message}", ct).ConfigureAwait(false);
        }

        MediaFile? inserted = null;
        var error = await CommitItemAsync(ctx, op, undo: false, null, async token =>
        {
            if (row is not null)
            {
                row.FolderId = folder!.Id; // FileId bleibt → Bewertung, Tags, Kategorien, Vorschaubild wandern mit
                if (!string.Equals(row.Name, destName, StringComparison.Ordinal))
                {
                    await FtsDeleteAsync(ctx, row.Id, row.Name, row.Extension, token).ConfigureAwait(false);
                    row.Name = destName;
                    row.Extension = ext;
                    row.MediaType = catalogued ? mediaType : row.MediaType;
                    await FtsInsertAsync(ctx, row.Id, row.Name, row.Extension, token).ConfigureAwait(false);
                }
            }
            else if (catalogued)
            {
                inserted = await InsertCatalogRowAsync(ctx, library, folder!, dest, token).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);

        if (error is not null)
        {
            RevokeAll(run.Own, ownPaths);
            if (inserted is not null)
            {
                ctx.Entry(inserted).State = EntityState.Detached;
            }
        }
        else if (row is not null)
        {
            run.Changes.Add(library.Id, FilesChangeKind.Moved, row.Id);
        }
        else if (inserted is not null)
        {
            run.Changes.Add(library.Id, FilesChangeKind.Added, inserted.Id);
        }

        return new FileOperationItemResult(row?.Id ?? inserted?.Id ?? 0, source, dest, error is null, error);
    }

    private async Task<FileOperationItemResult> FailAdoptItemAsync(LibraryContext ctx, FileOperation op, int? fileId, string message, CancellationToken ct)
    {
        op.Status = FileOperationStatus.Failed;
        op.Error = Truncate(message);
        op.ExecutedAtUtc = DateTime.UtcNow;
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogWarning("Übernahme: Datei fehlgeschlagen: {Source} → {Message}", op.SourcePath, message);
        return new FileOperationItemResult(fileId ?? 0, op.SourcePath, op.DestPath, false, message);
    }

    /// <summary>
    /// Quellordner (bzw. bei <paramref name="keepFolder"/> nur sein Inhalt) in den Papierkorb, danach die Katalogzeilen
    /// darunter entfernen: Dateien, die noch katalogisiert waren, liegen nur in Systemordnern (z. B. V2-„.thumbs“, die der
    /// Scanner als Bilder aufnimmt) und sind jetzt im Papierkorb — sie werden wie beim Papierkorb ausgetragen (FTS,
    /// Thumbnails); danach die leeren Folders-Zeilen. Ohne CancellationToken: die Dateien sind bereits verschoben.
    /// </summary>
    private async Task<(bool Recycled, string? Note)> RecycleAdoptedSourceAsync(
        LibraryContext ctx, int libraryId, FileOperation op, string source, bool keepFolder, BatchRun run)
    {
        await MarkApplyingAsync(ctx, op, CancellationToken.None).ConfigureAwait(false);
        run.Own.AddTree(source);
        try
        {
            if (keepFolder)
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(source).ToList())
                {
                    if (Directory.Exists(entry))
                    {
                        _recycleBin.MoveDirectoryToRecycleBin(entry);
                    }
                    else
                    {
                        _recycleBin.MoveToRecycleBin(entry);
                    }
                }
            }
            else
            {
                _recycleBin.MoveDirectoryToRecycleBin(source);
            }
        }
        catch (Exception ex)
        {
            // Auch OperationCanceledException der Shell (Benutzer-Abbruch) — der Ordner bleibt einfach liegen.
            var message = Truncate($"Papierkorb: {ex.Message}");
            op.Status = FileOperationStatus.Failed;
            op.Error = message;
            op.ExecutedAtUtc = DateTime.UtcNow;
            await ctx.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.LogWarning(ex, "Übernahme: Quellordner nicht in den Papierkorb: {Source}", source);
            return (false, $"Quellordner bleibt: {message}");
        }

        var hint = keepFolder ? "Eingangsordner bleibt; sein Inhalt liegt im Papierkorb." : null;
        var leftovers = (await LoadRowsUnderAsync(ctx, libraryId, source, CancellationToken.None).ConfigureAwait(false)).Values.ToList();
        var error = await CommitItemAsync(ctx, op, undo: false, hint, async token =>
        {
            foreach (var file in leftovers)
            {
                await RemoveCatalogEntryAsync(ctx, file, token, op.Id).ConfigureAwait(false); // Verknüpfungen gesichert (ADR-0054)
            }

            await ctx.SaveChangesAsync(token).ConfigureAwait(false);
            var lower = AsciiLower(source);
            var prefix = AsciiLower(source + Path.DirectorySeparatorChar);
            await ctx.Folders
                .Where(f => f.LibraryId == libraryId
                            && (f.Path.ToLower().StartsWith(prefix) || (!keepFolder && f.Path.ToLower() == lower))
                            && !ctx.Files.Any(x => x.FolderId == f.Id))
                .ExecuteDeleteAsync(token)
                .ConfigureAwait(false);
        }).ConfigureAwait(false);
        if (error is null)
        {
            foreach (var file in leftovers)
            {
                DeleteThumbnailFiles(libraryId, file.Id);
                run.Changes.Add(libraryId, FilesChangeKind.Removed, file.Id);
            }
        }

        return (true, error ?? hint);
    }

    // ------------------------------------------------------------------ Undo

    /// <summary>
    /// Undo eines AdoptFolder-Batches (rückwärts): Papierkorb-Schritt nur vermerken (der Ordner bleibt im Papierkorb, die
    /// Ordner entstehen beim Zurückverschieben neu), Dateien zurück an die alten Pfade (FileId bleibt), leer gewordene
    /// Zielordner entfernen. Liegt der Quellordner unter keiner Wurzel mehr (Eingang ausgetragen), wird er vorher wieder
    /// als Eingang eingetragen — sonst fänden die Dateien keinen Katalog-Ordner.
    /// </summary>
    private async Task<List<FileOperationItemResult>> UndoAdoptFolderAsync(LibraryContext ctx, List<FileOperation> ops, ChangeSet changes, IOwnChangeScope own, CancellationToken ct)
    {
        var folderOp = ops.FirstOrDefault(o => o.FileId is null && o.Action == OperationType.AdoptFolder);
        if (folderOp is null || string.IsNullOrEmpty(folderOp.DestPath))
        {
            return new List<FileOperationItemResult> { new(0, folderOp?.SourcePath ?? string.Empty, null, false, "Ordner-Operation fehlt im Protokoll.") };
        }

        var source = NormalizeDirectoryPath(folderOp.SourcePath);
        var targetRoot = await FindRootContainingAsync(ctx, null, folderOp.DestPath, ct).ConfigureAwait(false);
        var reRegistered = targetRoot is not null && await ReRegisterInboxAsync(ctx, targetRoot.LibraryId, source, ct).ConfigureAwait(false);
        if (targetRoot is null || await FindRootContainingAsync(ctx, targetRoot.LibraryId, source, ct).ConfigureAwait(false) is null)
        {
            // Ohne Wurzel fänden die Katalogzeilen keinen Ordner — lieber nichts zurückverschieben als den Katalog verbiegen.
            return new List<FileOperationItemResult>
            {
                await UndoFailAsync(ctx, folderOp, "Der Quellordner kann nicht wieder als Eingang eingetragen werden (überlappt eine andere Wurzel) — Rückgängig nicht möglich.", ct).ConfigureAwait(false),
            };
        }

        var results = new List<FileOperationItemResult>();
        foreach (var op in ops)
        {
            ct.ThrowIfCancellationRequested();
            if (op.Status != FileOperationStatus.Applied || ReferenceEquals(op, folderOp))
            {
                continue;
            }

            switch (op.Action)
            {
                case OperationType.Move:
                    results.Add(await UndoItemAsync(ctx, op, changes, own, ct).ConfigureAwait(false));
                    break;
                case OperationType.MkDir:
                    results.Add(await UndoCreatedDirectoryAsync(ctx, op, own).ConfigureAwait(false));
                    break;
                case OperationType.Recycle:
                    MarkDone(op, undo: true, "Quellordner bleibt im Papierkorb; die Ordner wurden neu angelegt.");
                    await ctx.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                    results.Add(new FileOperationItemResult(0, op.SourcePath, null, true, op.Error));
                    break;
            }
        }

        if (folderOp.Status == FileOperationStatus.Applied)
        {
            MarkDone(folderOp, undo: true, reRegistered ? "Eingang wieder eingetragen." : null);
            await ctx.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (targetRoot is not null)
        {
            if (reRegistered)
            {
                try
                {
                    _messenger.Send(new LibraryRootsChangedMessage(targetRoot.LibraryId));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Empfänger der LibraryRootsChangedMessage warf eine Ausnahme");
                }
            }

            PublishFoldersChanged(targetRoot.LibraryId);
        }

        _logger.LogInformation("Übernahme rückgängig: {Source} ← {Target} ({Items} Operationen, Eingang neu eingetragen: {ReRegistered})", source, folderOp.DestPath, results.Count, reRegistered);
        return results;
    }

    /// <summary>
    /// Trägt den Quellordner wieder als Eingang ein, wenn er unter keiner Wurzel der Bibliothek liegt und mit keiner Wurzel
    /// irgendeiner Bibliothek überlappt (sonst würden Dateien doppelt katalogisiert). Name = Ordnername.
    /// </summary>
    private async Task<bool> ReRegisterInboxAsync(LibraryContext ctx, int libraryId, string source, CancellationToken ct)
    {
        var all = await ctx.LibraryRoots.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        if (FindRootContaining(all, libraryId, source) is not null)
        {
            return false;
        }

        if (all.Any(r => IsInsideRoot(source, NormalizeDirectoryPath(r.Path)) || IsInsideRoot(NormalizeDirectoryPath(r.Path), source)))
        {
            _logger.LogWarning("Übernahme rückgängig: {Source} überlappt eine andere Wurzel — Eingang wird nicht wieder eingetragen", source);
            return false;
        }

        var sortOrder = all.Where(r => r.LibraryId == libraryId).Select(r => (int?)r.SortOrder).Max() ?? -1;
        ctx.LibraryRoots.Add(new LibraryRoot
        {
            LibraryId = libraryId,
            Path = source,
            Role = LibraryRootRole.Inbox,
            Name = FolderNameOf(source),
            SortOrder = sortOrder + 1,
        });
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Übernahme rückgängig: Eingang {Source} wieder eingetragen (Library {LibraryId})", source, libraryId);
        return true;
    }

    /// <summary>Vom Batch angelegten Zielordner entfernen, wenn er leer ist (sonst bleibt er, Hinweis); Folders-Zeile mit.</summary>
    private async Task<FileOperationItemResult> UndoCreatedDirectoryAsync(LibraryContext ctx, FileOperation op, IOwnChangeScope own)
    {
        var dir = op.SourcePath;
        string? hint = null;
        if (Directory.Exists(dir))
        {
            if (Directory.EnumerateFileSystemEntries(dir).Any())
            {
                op.Error = "Ordner ist nicht leer und bleibt bestehen.";
                await ctx.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                return new FileOperationItemResult(0, dir, null, true, op.Error);
            }

            own.Add(dir);
            try
            {
                Directory.Delete(dir); // nur leer (nicht rekursiv) — kein Datenverlust möglich
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                own.Revoke(dir);
                op.Error = Truncate($"Ordner konnte nicht entfernt werden: {ex.Message}");
                await ctx.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                return new FileOperationItemResult(0, dir, null, true, op.Error);
            }
        }
        else
        {
            hint = "Ordner war bereits entfernt.";
        }

        var lower = AsciiLower(NormalizeDirectoryPath(dir));
        var error = await CommitItemAsync(ctx, op, undo: true, hint, token => ctx.Folders
            .Where(f => f.Path.ToLower() == lower && !ctx.Files.Any(x => x.FolderId == f.Id) && !ctx.Folders.Any(c => c.ParentId == f.Id))
            .ExecuteDeleteAsync(token)).ConfigureAwait(false);
        return new FileOperationItemResult(0, dir, null, true, error ?? hint);
    }
}
