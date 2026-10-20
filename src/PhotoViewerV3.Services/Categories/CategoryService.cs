using System.Text;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Services.Catalog;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Messaging;

namespace PhotoViewerV3.Services.Categories;

/// <summary>
/// Kategorien und Werte über EF Core (CRUD), Zuweisungen über Raw-SQL in Chunks (wie <c>TagService</c>, ADR-0027).
/// Eigener Single-Writer-Lock; Nachrichten nach dem Schreiben, Ausnahmen von Empfängern werden geloggt.
/// </summary>
public sealed class CategoryService : ICategoryService, IDisposable
{
    public const int ChunkSize = 500;
    public const int MaxNameLength = 64;

    private readonly ILibraryContextFactory _factory;
    private readonly ILogger<CategoryService> _logger;
    private readonly IMessenger _messenger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public CategoryService(ILibraryContextFactory factory, ILogger<CategoryService> logger, IMessenger? messenger = null)
    {
        _factory = factory;
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    // ---- Lesen ---------------------------------------------------------------------------------

    public async Task<IReadOnlyList<CategoryInfo>> GetAllAsync(CancellationToken ct = default)
    {
        await using var ctx = _factory.Create();
        return await LoadAllAsync(ctx, ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<CategoryInfo>> LoadAllAsync(Data.LibraryContext ctx, CancellationToken ct)
    {
        var categories = await ctx.Categories.AsNoTracking()
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .ToListAsync(ct).ConfigureAwait(false);
        var values = await ctx.CategoryValues.AsNoTracking()
            .OrderBy(v => v.SortOrder).ThenBy(v => v.Name)
            .ToListAsync(ct).ConfigureAwait(false);
        var byCategory = values.GroupBy(v => v.CategoryId).ToDictionary(g => g.Key, g => g.ToList());
        return categories
            .Select(c => new CategoryInfo(
                c.Id,
                c.Name,
                c.SortOrder,
                byCategory.TryGetValue(c.Id, out var list)
                    ? list.Select(v => new CategoryValueInfo(v.Id, v.CategoryId, v.Name, v.SortOrder)).ToList()
                    : []))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<int, int>> GetFileCountsAsync(CancellationToken ct = default)
    {
        await using var ctx = _factory.Create();
        var rows = await ctx.FileCategoryValues.AsNoTracking()
            .GroupBy(x => x.ValueId)
            .Select(g => new { ValueId = g.Key, Count = g.Count() })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.ToDictionary(r => r.ValueId, r => r.Count);
    }

    public async Task<IReadOnlyDictionary<int, IReadOnlyDictionary<int, int>>> GetValuesForFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        var result = new Dictionary<int, IReadOnlyDictionary<int, int>>();
        if (fileIds.Count == 0)
        {
            return result;
        }

        await using var ctx = _factory.Create();
        foreach (var chunk in fileIds.Distinct().Chunk(ChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            var rows = await ctx.FileCategoryValues.AsNoTracking()
                .Where(x => chunk.Contains(x.FileId))
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var group in rows.GroupBy(r => r.FileId))
            {
                result[group.Key] = group.ToDictionary(r => r.CategoryId, r => r.ValueId);
            }
        }

        return result;
    }

    // ---- Kategorien ----------------------------------------------------------------------------

    public async Task<CategoryInfo> CreateAsync(string name, IReadOnlyList<string>? values = null, CancellationToken ct = default)
    {
        var normalized = NormalizeName(name, "Kategorie");
        var valueNames = NormalizeValueList(values);
        CategoryInfo created;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            if (await ctx.Categories.AnyAsync(c => c.Name == normalized, ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"Eine Kategorie „{normalized}“ existiert bereits.");
            }

            var maxOrder = await ctx.Categories.Select(c => (int?)c.SortOrder).MaxAsync(ct).ConfigureAwait(false) ?? -1;
            var category = new Category { Name = normalized, SortOrder = maxOrder + 1, CreatedAtUtc = DateTime.UtcNow };
            ctx.Categories.Add(category);
            await SaveAsync(ctx, $"Eine Kategorie „{normalized}“ existiert bereits.", ct).ConfigureAwait(false);

            var order = 0;
            foreach (var valueName in valueNames)
            {
                ctx.CategoryValues.Add(new CategoryValue { CategoryId = category.Id, Name = valueName, SortOrder = order++ });
            }

            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            created = (await LoadAllAsync(ctx, ct).ConfigureAwait(false)).First(c => c.Id == category.Id);
            _logger.LogInformation("Kategorie angelegt: {Name} (Id {Id}) mit {Values} Werten", normalized, category.Id, valueNames.Count);
        }
        finally
        {
            _writeLock.Release();
        }

        Publish(new CategoriesChangedMessage(CategoriesChangeKind.Created, created.Id));
        return created;
    }

    public async Task RenameAsync(int categoryId, string newName, CancellationToken ct = default)
    {
        var normalized = NormalizeName(newName, "Kategorie");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            var category = await FindCategoryAsync(ctx, categoryId, ct).ConfigureAwait(false);
            if (string.Equals(category.Name, normalized, StringComparison.Ordinal))
            {
                return;
            }

            if (await ctx.Categories.AnyAsync(c => c.Name == normalized && c.Id != categoryId, ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"Eine Kategorie „{normalized}“ existiert bereits.");
            }

            category.Name = normalized;
            await SaveAsync(ctx, $"Eine Kategorie „{normalized}“ existiert bereits.", ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        Publish(new CategoriesChangedMessage(CategoriesChangeKind.Renamed, categoryId));
    }

    public async Task ReorderAsync(IReadOnlyList<int> categoryIdsInOrder, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(categoryIdsInOrder);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            var categories = await ctx.Categories.OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct).ConfigureAwait(false);
            ApplyOrder(categories, c => c.Id, (c, o) => c.SortOrder = o, categoryIdsInOrder);
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        Publish(new CategoriesChangedMessage(CategoriesChangeKind.Reordered, null));
    }

    public async Task<int> DeleteAsync(int categoryId, CancellationToken ct = default)
    {
        Dictionary<int, List<int>> byLibrary;
        int removed;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            var category = await FindCategoryAsync(ctx, categoryId, ct).ConfigureAwait(false);
            byLibrary = await AffectedFilesAsync(ctx, ctx.FileCategoryValues.Where(x => x.CategoryId == categoryId), ct).ConfigureAwait(false);

            // Ausdrücklich löschen (unabhängig von der FK-Erzwingung), dann Werte und Kategorie.
            removed = await ctx.FileCategoryValues.Where(x => x.CategoryId == categoryId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await ctx.CategoryValues.Where(v => v.CategoryId == categoryId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            ctx.Categories.Remove(category);
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Kategorie gelöscht: {Name} (Id {Id}), {Count} Zuweisungen entfernt", category.Name, categoryId, removed);
        }
        finally
        {
            _writeLock.Release();
        }

        Publish(new CategoriesChangedMessage(CategoriesChangeKind.Deleted, categoryId));
        PublishFilesChanged(byLibrary);
        return removed;
    }

    // ---- Werte ---------------------------------------------------------------------------------

    public async Task<CategoryValueInfo> AddValueAsync(int categoryId, string name, CancellationToken ct = default)
    {
        var normalized = NormalizeName(name, "Wert");
        CategoryValueInfo info;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            var category = await FindCategoryAsync(ctx, categoryId, ct).ConfigureAwait(false);
            if (await ctx.CategoryValues.AnyAsync(v => v.CategoryId == categoryId && v.Name == normalized, ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"Die Kategorie „{category.Name}“ hat bereits einen Wert „{normalized}“.");
            }

            var maxOrder = await ctx.CategoryValues.Where(v => v.CategoryId == categoryId).Select(v => (int?)v.SortOrder).MaxAsync(ct).ConfigureAwait(false) ?? -1;
            var value = new CategoryValue { CategoryId = categoryId, Name = normalized, SortOrder = maxOrder + 1 };
            ctx.CategoryValues.Add(value);
            await SaveAsync(ctx, $"Die Kategorie „{category.Name}“ hat bereits einen Wert „{normalized}“.", ct).ConfigureAwait(false);
            info = new CategoryValueInfo(value.Id, categoryId, value.Name, value.SortOrder);
        }
        finally
        {
            _writeLock.Release();
        }

        Publish(new CategoriesChangedMessage(CategoriesChangeKind.ValuesChanged, categoryId));
        return info;
    }

    public async Task RenameValueAsync(int valueId, string newName, CancellationToken ct = default)
    {
        var normalized = NormalizeName(newName, "Wert");
        int categoryId;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            var value = await FindValueAsync(ctx, valueId, ct).ConfigureAwait(false);
            categoryId = value.CategoryId;
            if (string.Equals(value.Name, normalized, StringComparison.Ordinal))
            {
                return;
            }

            if (await ctx.CategoryValues.AnyAsync(v => v.CategoryId == categoryId && v.Name == normalized && v.Id != valueId, ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"Diese Kategorie hat bereits einen Wert „{normalized}“.");
            }

            value.Name = normalized;
            await SaveAsync(ctx, $"Diese Kategorie hat bereits einen Wert „{normalized}“.", ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        Publish(new CategoriesChangedMessage(CategoriesChangeKind.ValuesChanged, categoryId));
    }

    public async Task ReorderValuesAsync(int categoryId, IReadOnlyList<int> valueIdsInOrder, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(valueIdsInOrder);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            var values = await ctx.CategoryValues.Where(v => v.CategoryId == categoryId)
                .OrderBy(v => v.SortOrder).ThenBy(v => v.Name)
                .ToListAsync(ct).ConfigureAwait(false);
            ApplyOrder(values, v => v.Id, (v, o) => v.SortOrder = o, valueIdsInOrder);
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        Publish(new CategoriesChangedMessage(CategoriesChangeKind.ValuesChanged, categoryId));
    }

    public async Task<int> DeleteValueAsync(int valueId, CancellationToken ct = default)
    {
        Dictionary<int, List<int>> byLibrary;
        int removed;
        int categoryId;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            var value = await FindValueAsync(ctx, valueId, ct).ConfigureAwait(false);
            categoryId = value.CategoryId;
            byLibrary = await AffectedFilesAsync(ctx, ctx.FileCategoryValues.Where(x => x.ValueId == valueId), ct).ConfigureAwait(false);
            removed = await ctx.FileCategoryValues.Where(x => x.ValueId == valueId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            ctx.CategoryValues.Remove(value);
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Kategorie-Wert gelöscht: {Name} (Id {Id}), {Count} Zuweisungen entfernt", value.Name, valueId, removed);
        }
        finally
        {
            _writeLock.Release();
        }

        Publish(new CategoriesChangedMessage(CategoriesChangeKind.ValuesChanged, categoryId));
        PublishFilesChanged(byLibrary);
        return removed;
    }

    // ---- Zuweisungen ---------------------------------------------------------------------------

    public async Task<int> SetValueAsync(IReadOnlyList<int> fileIds, int categoryId, int? valueId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        var ids = fileIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return 0;
        }

        var byLibrary = new Dictionary<int, List<int>>();
        int changed;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Wartet asynchron auf eine belegte Schreibsperre, Ziele nach einem Neuaufbau umgesetzt (ADR-0030).
            changed = await CatalogFileTargets.WriteAsync(_factory, ids, _logger, (connection, tx, files) =>
            {
                EnsureValueBelongsToCategory(connection, tx, categoryId, valueId);
                foreach (var file in files)
                {
                    Collect(byLibrary, file.LibraryId, file.FileId);
                }

                var count = 0;
                foreach (var chunk in files.Select(f => f.FileId).Chunk(ChunkSize))
                {
                    ct.ThrowIfCancellationRequested();
                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.Parameters.AddWithValue("$c", categoryId);
                    var inList = BindIds(cmd, chunk, "$f");
                    if (valueId is { } value)
                    {
                        // Nur echte Änderungen zählen: gleicher Wert bleibt unberührt (WHERE im UPSERT).
                        cmd.Parameters.AddWithValue("$v", value);
                        cmd.CommandText =
                            $"INSERT INTO FileCategoryValues(FileId, CategoryId, ValueId) SELECT f.Id, $c, $v FROM Files f WHERE f.Id IN ({inList}) " +
                            "ON CONFLICT(FileId, CategoryId) DO UPDATE SET ValueId = excluded.ValueId WHERE ValueId <> excluded.ValueId;";
                    }
                    else
                    {
                        cmd.CommandText = $"DELETE FROM FileCategoryValues WHERE CategoryId = $c AND FileId IN ({inList});";
                    }

                    count += cmd.ExecuteNonQuery();
                }

                return count;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        _logger.LogDebug("Kategorie {Category}: {Files} Datei(en) → Wert {Value}, {Changed} geändert", categoryId, ids.Length, valueId, changed);
        PublishFilesChanged(byLibrary);
        return changed;
    }

    private static void EnsureValueBelongsToCategory(SqliteConnection connection, SqliteTransaction tx, int categoryId, int? valueId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$c", categoryId);
        if (valueId is null)
        {
            cmd.CommandText = "SELECT COUNT(*) FROM Categories WHERE Id = $c;";
            if (Convert.ToInt64(cmd.ExecuteScalar()) == 0)
            {
                throw new InvalidOperationException($"Kategorie {categoryId} existiert nicht (mehr).");
            }

            return;
        }

        cmd.Parameters.AddWithValue("$v", valueId.Value);
        cmd.CommandText = "SELECT COUNT(*) FROM CategoryValues WHERE Id = $v AND CategoryId = $c;";
        if (Convert.ToInt64(cmd.ExecuteScalar()) == 0)
        {
            throw new InvalidOperationException($"Der Wert {valueId} gehört nicht (mehr) zur Kategorie {categoryId}.");
        }
    }

    // ---- Hilfen --------------------------------------------------------------------------------

    /// <summary>Trimmt den Namen und prüft Länge/Leer; wirft <see cref="ArgumentException"/>.</summary>
    public static string NormalizeName(string? name, string what = "Name")
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new ArgumentException($"{what}: der Name darf nicht leer sein.", nameof(name));
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new ArgumentException($"{what}: der Name darf höchstens {MaxNameLength} Zeichen lang sein.", nameof(name));
        }

        return trimmed;
    }

    private static List<string> NormalizeValueList(IReadOnlyList<string>? values)
    {
        var result = new List<string>();
        foreach (var raw in values ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var name = NormalizeName(raw, "Wert");
            if (!result.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(name);
            }
        }

        return result;
    }

    private static void ApplyOrder<T>(List<T> items, Func<T, int> id, Action<T, int> setOrder, IReadOnlyList<int> idsInOrder)
    {
        var byId = items.ToDictionary(id);
        var placed = new HashSet<int>();
        var order = 0;
        foreach (var itemId in idsInOrder)
        {
            if (byId.TryGetValue(itemId, out var item) && placed.Add(itemId))
            {
                setOrder(item, order++);
            }
        }

        foreach (var item in items)
        {
            if (!placed.Contains(id(item)))
            {
                setOrder(item, order++);
            }
        }
    }

    private static async Task<Category> FindCategoryAsync(Data.LibraryContext ctx, int categoryId, CancellationToken ct)
        => await ctx.Categories.FirstOrDefaultAsync(c => c.Id == categoryId, ct).ConfigureAwait(false)
           ?? throw new InvalidOperationException($"Kategorie {categoryId} existiert nicht (mehr).");

    private static async Task<CategoryValue> FindValueAsync(Data.LibraryContext ctx, int valueId, CancellationToken ct)
        => await ctx.CategoryValues.FirstOrDefaultAsync(v => v.Id == valueId, ct).ConfigureAwait(false)
           ?? throw new InvalidOperationException($"Kategorie-Wert {valueId} existiert nicht (mehr).");

    private static async Task<Dictionary<int, List<int>>> AffectedFilesAsync(
        Data.LibraryContext ctx, IQueryable<FileCategoryValue> assignments, CancellationToken ct)
    {
        var rows = await (
                from a in assignments.AsNoTracking()
                join f in ctx.Files.AsNoTracking() on a.FileId equals f.Id
                select new { f.Id, f.LibraryId })
            .ToListAsync(ct).ConfigureAwait(false);
        var byLibrary = new Dictionary<int, List<int>>();
        foreach (var row in rows)
        {
            Collect(byLibrary, row.LibraryId, row.Id);
        }

        return byLibrary;
    }

    /// <summary>SaveChanges; ein Unique-Verstoß (Wettlauf zweier Schreiber) wird als Fachmeldung gemeldet.</summary>
    private static async Task SaveAsync(Data.LibraryContext ctx, string duplicateMessage, CancellationToken ct)
    {
        try
        {
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            throw new InvalidOperationException(duplicateMessage, ex);
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
