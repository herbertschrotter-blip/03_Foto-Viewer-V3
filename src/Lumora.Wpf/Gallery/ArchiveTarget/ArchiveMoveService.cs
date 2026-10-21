using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Database;
using Lumora.Services.Operations;

namespace Lumora.Gallery.ArchiveTarget;

/// <summary>
/// Implementierung von <see cref="IArchiveMoveService"/> (ADR-0020): Archiv-Wurzel der Bibliothek der Dateien ermitteln,
/// zuletzt benutzten Zielordner (Setting <c>archive.lastTarget.{libraryId}</c>) und Datumsvorschlag laden, Zielordner
/// über <see cref="IArchiveTargetPicker"/> abfragen, Dateien per <see cref="IFileOperationService.MoveAsync"/> verschieben
/// und den Zielordner merken. Dateien mehrerer Bibliotheken: es zählt die Bibliothek der ersten Datei, die übrigen
/// werden übersprungen (geloggt).
/// </summary>
public sealed class ArchiveMoveService : IArchiveMoveService
{
    public const string LastTargetSettingPrefix = "archive.lastTarget.";

    private readonly ILibraryContextFactory _factory;
    private readonly IFileOperationService _fileOps;
    private readonly IArchiveTargetPicker _picker;
    private readonly ILogger<ArchiveMoveService> _logger;

    public ArchiveMoveService(
        ILibraryContextFactory factory,
        IFileOperationService fileOps,
        IArchiveTargetPicker picker,
        ILogger<ArchiveMoveService> logger)
    {
        _factory = factory;
        _fileOps = fileOps;
        _picker = picker;
        _logger = logger;
    }

    public async Task<FileOperationBatchResult?> ArchiveAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        if (fileIds.Count == 0)
        {
            return null;
        }

        ArchiveTargetRequest request;
        List<int> ids;
        await using (var ctx = _factory.Create())
        {
            var first = fileIds[0];
            var libraryId = await ctx.Files.AsNoTracking()
                .Where(f => f.Id == first)
                .Select(f => (int?)f.LibraryId)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(true);
            if (libraryId is not { } lib)
            {
                _logger.LogWarning("Ins Archiv: Datei {FileId} nicht im Katalog", first);
                return null;
            }

            var archiveRoot = await ctx.LibraryRoots.AsNoTracking()
                .FirstOrDefaultAsync(r => r.LibraryId == lib && r.Role == LibraryRootRole.Archive, ct)
                .ConfigureAwait(true);
            if (archiveRoot is null)
            {
                _logger.LogWarning("Ins Archiv: Bibliothek {LibraryId} hat keine Archiv-Wurzel", lib);
                return null;
            }

            ids = new List<int>(fileIds.Count);
            foreach (var chunk in fileIds.Chunk(500))
            {
                ids.AddRange(await ctx.Files.AsNoTracking()
                    .Where(f => chunk.Contains(f.Id) && f.LibraryId == lib)
                    .Select(f => f.Id)
                    .ToListAsync(ct)
                    .ConfigureAwait(true));
            }

            if (ids.Count < fileIds.Count)
            {
                _logger.LogInformation("Ins Archiv: {Skipped} Datei(en) anderer Bibliotheken übersprungen", fileIds.Count - ids.Count);
            }

            var firstPath = await ctx.Files.AsNoTracking()
                .Where(f => f.Id == first)
                .Join(ctx.Folders.AsNoTracking(), f => f.FolderId, d => d.Id, (f, d) => Path.Combine(d.Path, f.Name))
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(true);

            var lastTarget = await ctx.Settings.AsNoTracking()
                .Where(s => s.Key == LastTargetSettingPrefix + lib)
                .Select(s => s.Value)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(true);

            var suggestion = firstPath is null
                ? null
                : await Task.Run(() => ArchiveDateSuggestion.SuggestFor(firstPath), ct).ConfigureAwait(true);

            request = new ArchiveTargetRequest(lib, archiveRoot.Id, archiveRoot.Path, archiveRoot.Name, ids, lastTarget, suggestion);
        }

        var target = await _picker.PickAsync(request).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(target))
        {
            _logger.LogInformation("Ins Archiv: abgebrochen ({Count} Dateien)", ids.Count);
            return null;
        }

        await RememberTargetAsync(request.LibraryId, target, ct).ConfigureAwait(true);
        _logger.LogInformation("Ins Archiv: {Count} Datei(en) → {Target}", ids.Count, target);
        return await _fileOps.MoveAsync(ids, target, ct).ConfigureAwait(true);
    }

    private async Task RememberTargetAsync(int libraryId, string target, CancellationToken ct)
    {
        try
        {
            await using var ctx = _factory.Create();
            var key = LastTargetSettingPrefix + libraryId;
            var setting = await ctx.Settings.FirstOrDefaultAsync(s => s.Key == key, ct).ConfigureAwait(true);
            if (setting is null)
            {
                ctx.Settings.Add(new Setting { Key = key, Value = target });
            }
            else
            {
                setting.Value = target;
            }

            await ctx.SaveChangesAsync(ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ins Archiv: Zielordner konnte nicht gemerkt werden");
        }
    }
}
