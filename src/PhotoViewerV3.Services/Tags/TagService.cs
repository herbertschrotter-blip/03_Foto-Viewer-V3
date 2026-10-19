using System.Text;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Messaging;

namespace PhotoViewerV3.Services.Tags;

/// <summary>
/// Tag-Stammdaten über EF Core (CRUD), Zuweisungen über Raw-SQL in Chunks zu <see cref="ChunkSize"/> Ids
/// (<c>INSERT OR IGNORE … SELECT Id FROM Files WHERE Id IN (…)</c> — unbekannte Ids werden still übergangen,
/// der Fremdschlüssel bleibt gültig). Eigener Single-Writer-Lock wie <c>CatalogUpdateService</c>.
/// Nach Zuweisungen eine <see cref="FilesChangedMessage"/> (Metadata) je Library, nach Stammdaten-Änderungen eine
/// <see cref="TagsChangedMessage"/>; Ausnahmen von Empfängern werden geloggt.
/// </summary>
public sealed class TagService : ITagService, IDisposable
{
    public const int ChunkSize = 500;
    public const int MaxNameLength = 64;

    private readonly ILibraryContextFactory _factory;
    private readonly ILogger<TagService> _logger;
    private readonly IMessenger _messenger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public TagService(ILibraryContextFactory factory, ILogger<TagService> logger, IMessenger? messenger = null)
    {
        _factory = factory;
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    // ---- Stammdaten ----------------------------------------------------------------------------

    public async Task<IReadOnlyList<TagInfo>> GetAllAsync(CancellationToken ct = default)
    {
        await using var ctx = _factory.Create();
        return await ctx.Tags.AsNoTracking()
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .Select(t => new TagInfo(t.Id, t.Name, t.Color, t.SortOrder))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<TagInfo> CreateAsync(string name, string? color = null, CancellationToken ct = default)
    {
        var normalized = NormalizeName(name);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            await EnsureUniqueAsync(ctx, normalized, excludeId: null, ct).ConfigureAwait(false);
            var count = await ctx.Tags.CountAsync(ct).ConfigureAwait(false);
            var maxOrder = count == 0 ? -1 : await ctx.Tags.MaxAsync(t => t.SortOrder, ct).ConfigureAwait(false);
            var tag = new Tag
            {
                Name = normalized,
                Color = color is null ? TagPalette.ForIndex(count) : TagPalette.Normalize(color),
                SortOrder = maxOrder + 1,
                CreatedAtUtc = DateTime.UtcNow,
            };
            ctx.Tags.Add(tag);
            await SaveAsync(ctx, normalized, ct).ConfigureAwait(false);
            _logger.LogInformation("Tag angelegt: {Name} (Id {Id}, {Color})", tag.Name, tag.Id, tag.Color);
            Publish(new TagsChangedMessage(TagsChangeKind.Created, tag.Id));
            return new TagInfo(tag.Id, tag.Name, tag.Color, tag.SortOrder);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task RenameAsync(int tagId, string newName, CancellationToken ct = default)
    {
        var normalized = NormalizeName(newName);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            var tag = await FindAsync(ctx, tagId, ct).ConfigureAwait(false);
            if (string.Equals(tag.Name, normalized, StringComparison.Ordinal))
            {
                return;
            }

            await EnsureUniqueAsync(ctx, normalized, excludeId: tagId, ct).ConfigureAwait(false);
            tag.Name = normalized;
            await SaveAsync(ctx, normalized, ct).ConfigureAwait(false);
            _logger.LogInformation("Tag umbenannt: Id {Id} → {Name}", tagId, normalized);
            Publish(new TagsChangedMessage(TagsChangeKind.Renamed, tagId));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task SetColorAsync(int tagId, string color, CancellationToken ct = default)
    {
        var normalized = TagPalette.Normalize(color);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            var tag = await FindAsync(ctx, tagId, ct).ConfigureAwait(false);
            if (string.Equals(tag.Color, normalized, StringComparison.Ordinal))
            {
                return;
            }

            tag.Color = normalized;
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            Publish(new TagsChangedMessage(TagsChangeKind.Recolored, tagId));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task ReorderAsync(IReadOnlyList<int> tagIdsInOrder, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tagIdsInOrder);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            var tags = await ctx.Tags.OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync(ct).ConfigureAwait(false);
            var byId = tags.ToDictionary(t => t.Id);
            var order = 0;
            var placed = new HashSet<int>();
            foreach (var id in tagIdsInOrder)
            {
                if (byId.TryGetValue(id, out var tag) && placed.Add(id))
                {
                    tag.SortOrder = order++;
                }
            }

            foreach (var tag in tags)
            {
                if (!placed.Contains(tag.Id))
                {
                    tag.SortOrder = order++;
                }
            }

            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            Publish(new TagsChangedMessage(TagsChangeKind.Reordered, null));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> DeleteAsync(int tagId, CancellationToken ct = default)
    {
        var byLibrary = new Dictionary<int, List<int>>();
        int removed;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            var tag = await FindAsync(ctx, tagId, ct).ConfigureAwait(false);

            // Betroffene Dateien für die Nachricht merken, bevor die Zuweisungen mit dem Tag verschwinden.
            var affected = await (
                    from ft in ctx.FileTags.AsNoTracking()
                    join f in ctx.Files.AsNoTracking() on ft.FileId equals f.Id
                    where ft.TagId == tagId
                    select new { f.Id, f.LibraryId })
                .ToListAsync(ct)
                .ConfigureAwait(false);
            foreach (var row in affected)
            {
                Collect(byLibrary, row.LibraryId, row.Id);
            }

            // Zuweisungen ausdrücklich löschen (unabhängig davon, ob der FK erzwungen wird), dann das Tag.
            removed = await ctx.FileTags.Where(ft => ft.TagId == tagId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            ctx.Tags.Remove(tag);
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Tag gelöscht: {Name} (Id {Id}), {Count} Zuweisungen entfernt", tag.Name, tagId, removed);
        }
        finally
        {
            _writeLock.Release();
        }

        Publish(new TagsChangedMessage(TagsChangeKind.Deleted, tagId));
        PublishFilesChanged(byLibrary);
        return removed;
    }

    public async Task<IReadOnlyDictionary<int, int>> GetFileCountsAsync(CancellationToken ct = default)
    {
        await using var ctx = _factory.Create();
        var rows = await ctx.FileTags.AsNoTracking()
            .GroupBy(ft => ft.TagId)
            .Select(g => new { TagId = g.Key, Count = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.ToDictionary(r => r.TagId, r => r.Count);
    }

    // ---- Zuweisungen ---------------------------------------------------------------------------

    public Task<int> AssignAsync(IReadOnlyList<int> fileIds, IReadOnlyList<int> tagIds, CancellationToken ct = default)
        => WriteAssignmentsAsync(fileIds, tagIds, assign: true, ct);

    public Task<int> RemoveAsync(IReadOnlyList<int> fileIds, IReadOnlyList<int> tagIds, CancellationToken ct = default)
        => WriteAssignmentsAsync(fileIds, tagIds, assign: false, ct);

    public async Task<int> RemoveForFilesAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        var ids = fileIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return 0;
        }

        var removed = 0;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var connection = _factory.OpenSqliteConnection();
            using var tx = connection.BeginTransaction();
            foreach (var chunk in ids.Chunk(ChunkSize))
            {
                ct.ThrowIfCancellationRequested();
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"DELETE FROM FileTags WHERE FileId IN ({BindIds(cmd, chunk, "$f")});";
                removed += cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        finally
        {
            _writeLock.Release();
        }

        return removed;
    }

    public async Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> GetTagIdsForFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        var result = new Dictionary<int, IReadOnlyList<int>>();
        if (fileIds.Count == 0)
        {
            return result;
        }

        await using var ctx = _factory.Create();
        foreach (var chunk in fileIds.Distinct().Chunk(ChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            var rows = await (
                    from ft in ctx.FileTags.AsNoTracking()
                    join t in ctx.Tags.AsNoTracking() on ft.TagId equals t.Id
                    where chunk.Contains(ft.FileId)
                    orderby ft.FileId, t.SortOrder, t.Name
                    select new { ft.FileId, ft.TagId })
                .ToListAsync(ct)
                .ConfigureAwait(false);
            foreach (var group in rows.GroupBy(r => r.FileId))
            {
                result[group.Key] = group.Select(r => r.TagId).ToArray();
            }
        }

        return result;
    }

    private async Task<int> WriteAssignmentsAsync(IReadOnlyList<int> fileIds, IReadOnlyList<int> tagIds, bool assign, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        ArgumentNullException.ThrowIfNull(tagIds);
        var ids = fileIds.Distinct().ToArray();
        var tags = tagIds.Distinct().ToArray();
        if (ids.Length == 0 || tags.Length == 0)
        {
            return 0;
        }

        var byLibrary = new Dictionary<int, List<int>>();
        var changed = 0;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var connection = _factory.OpenSqliteConnection();
            using var tx = connection.BeginTransaction();
            foreach (var chunk in ids.Chunk(ChunkSize))
            {
                ct.ThrowIfCancellationRequested();

                // Nur Dateien, die es gibt (für die Nachricht und als FK-Schutz beim Zuweisen).
                using (var lookup = connection.CreateCommand())
                {
                    lookup.Transaction = tx;
                    lookup.CommandText = $"SELECT Id, LibraryId FROM Files WHERE Id IN ({BindIds(lookup, chunk, "$f")});";
                    using var reader = lookup.ExecuteReader();
                    while (reader.Read())
                    {
                        Collect(byLibrary, reader.GetInt32(1), reader.GetInt32(0));
                    }
                }

                foreach (var tagId in tags)
                {
                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.Parameters.AddWithValue("$t", tagId);
                    var inList = BindIds(cmd, chunk, "$f");
                    cmd.CommandText = assign
                        ? $"INSERT OR IGNORE INTO FileTags(FileId, TagId) SELECT f.Id, $t FROM Files f WHERE f.Id IN ({inList}) AND EXISTS (SELECT 1 FROM Tags t WHERE t.Id = $t);"
                        : $"DELETE FROM FileTags WHERE TagId = $t AND FileId IN ({inList});";
                    changed += cmd.ExecuteNonQuery();
                }
            }

            tx.Commit();
        }
        finally
        {
            _writeLock.Release();
        }

        _logger.LogDebug("Tags {Action}: {Files} Datei(en) × {Tags} Tag(s), {Changed} Zuweisungen", assign ? "zugewiesen" : "entfernt", ids.Length, tags.Length, changed);
        PublishFilesChanged(byLibrary);
        return changed;
    }

    // ---- Hilfen --------------------------------------------------------------------------------

    /// <summary>Trimmt den Namen und prüft Länge/Leer; wirft <see cref="ArgumentException"/>.</summary>
    public static string NormalizeName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Der Tag-Name darf nicht leer sein.", nameof(name));
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new ArgumentException($"Der Tag-Name darf höchstens {MaxNameLength} Zeichen lang sein.", nameof(name));
        }

        return trimmed;
    }

    private static async Task<Tag> FindAsync(Data.LibraryContext ctx, int tagId, CancellationToken ct)
        => await ctx.Tags.FirstOrDefaultAsync(t => t.Id == tagId, ct).ConfigureAwait(false)
           ?? throw new InvalidOperationException($"Tag {tagId} existiert nicht (mehr).");

    private static async Task EnsureUniqueAsync(Data.LibraryContext ctx, string name, int? excludeId, CancellationToken ct)
    {
        // Spalte hat COLLATE NOCASE → der Vergleich läuft in SQLite ohne Groß-/Kleinschreibung.
        var exists = await ctx.Tags.AnyAsync(t => t.Name == name && (excludeId == null || t.Id != excludeId), ct).ConfigureAwait(false);
        if (exists)
        {
            throw new InvalidOperationException($"Ein Tag „{name}“ existiert bereits.");
        }
    }

    /// <summary>SaveChanges; ein Unique-Verstoß (Wettlauf zweier Schreiber) wird als dieselbe Fachmeldung gemeldet.</summary>
    private static async Task SaveAsync(Data.LibraryContext ctx, string name, CancellationToken ct)
    {
        try
        {
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            throw new InvalidOperationException($"Ein Tag „{name}“ existiert bereits.", ex);
        }
    }

    private static string BindIds(SqliteCommand cmd, int[] ids, string prefix)
    {
        var sb = new StringBuilder(ids.Length * 6);
        for (var i = 0; i < ids.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            var p = prefix + i;
            sb.Append(p);
            cmd.Parameters.AddWithValue(p, ids[i]);
        }

        return sb.ToString();
    }

    private static void Collect(Dictionary<int, List<int>> byLibrary, int libraryId, int fileId)
    {
        if (!byLibrary.TryGetValue(libraryId, out var list))
        {
            list = new List<int>();
            byLibrary[libraryId] = list;
        }

        list.Add(fileId);
    }

    private void PublishFilesChanged(Dictionary<int, List<int>> byLibrary)
    {
        foreach (var (libraryId, list) in byLibrary)
        {
            Publish(new FilesChangedMessage(libraryId, FilesChangeKind.Metadata, list));
        }
    }

    private void Publish<TMessage>(TMessage message)
        where TMessage : class
    {
        try
        {
            _messenger.Send(message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Empfänger der {Message} warf eine Ausnahme", typeof(TMessage).Name);
        }
    }

    public void Dispose() => _writeLock.Dispose();
}
