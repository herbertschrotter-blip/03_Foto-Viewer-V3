using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Services.Catalog;
using PhotoViewerV3.Services.Database;

namespace PhotoViewerV3.Services.Albums;

/// <summary>
/// Ordner-Verknüpfungen über Raw-SQL (ADR-0054). Schreiben über <see cref="CatalogFileTargets"/> (wartet asynchron auf
/// die Schreibsperre, FileIds nach „Neu aufbauen“ umgesetzt, ADR-0030), eigener Single-Writer-Lock. Die Bibliotheks-Regel
/// (Ordner und Datei derselben Bibliothek) prüft die Datenbank im INSERT selbst.
/// </summary>
public sealed class FolderLinkService : IFolderLinkService, IDisposable
{
    private const int ChunkSize = 500;

    private readonly ILibraryContextFactory _factory;
    private readonly ILogger<FolderLinkService> _logger;
    private readonly IMessenger _messenger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public FolderLinkService(ILibraryContextFactory factory, ILogger<FolderLinkService> logger, IMessenger? messenger = null)
    {
        _factory = factory;
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    public async Task<FolderLinkResult> LinkAsync(int folderId, IReadOnlyList<int> fileIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        var ids = fileIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new FolderLinkResult(0, 0, 0, 0);
        }

        var libraryId = 0;
        var linkedIds = new List<int>();
        FolderLinkResult result;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            result = await CatalogFileTargets.WriteAsync(_factory, ids, _logger, (connection, tx, files) =>
            {
                var folderLibrary = ReadFolderLibrary(connection, tx, folderId)
                    ?? throw new InvalidOperationException("Der Ordner ist nicht (mehr) im Katalog.");
                libraryId = folderLibrary;
                var unknown = ids.Length - files.Count;
                var other = files.Count(f => f.LibraryId != folderLibrary);
                var candidates = files.Where(f => f.LibraryId == folderLibrary).Select(f => f.FileId).ToArray();
                var linked = 0;
                foreach (var chunk in candidates.Chunk(ChunkSize))
                {
                    ct.ThrowIfCancellationRequested();
                    using var select = connection.CreateCommand();
                    select.Transaction = tx;
                    select.Parameters.AddWithValue("$d", folderId);
                    // Nicht verknüpfen, was physisch in dem Ordner liegt oder schon verknüpft ist.
                    select.CommandText = $"""
                        SELECT f.Id FROM Files f
                        WHERE f.Id IN ({CatalogFileTargets.BindIds(select, chunk, "$f")}) AND f.FolderId <> $d
                          AND NOT EXISTS (SELECT 1 FROM FolderLinks l WHERE l.FolderId = $d AND l.FileId = f.Id);
                        """;
                    var fresh = new List<int>();
                    using (var reader = select.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            fresh.Add(reader.GetInt32(0));
                        }
                    }

                    if (fresh.Count == 0)
                    {
                        continue;
                    }

                    // Ein INSERT je Block; die Bibliotheks-Regel prüft die Datenbank selbst (Ordner und Datei derselben Bibliothek).
                    using var insert = connection.CreateCommand();
                    insert.Transaction = tx;
                    insert.Parameters.AddWithValue("$d", folderId);
                    insert.Parameters.AddWithValue("$now", now);
                    insert.CommandText = $"""
                        INSERT OR IGNORE INTO FolderLinks (FolderId, FileId, AddedAtUtc)
                        SELECT $d, f.Id, $now FROM Files f JOIN Folders d ON d.Id = $d
                        WHERE f.Id IN ({CatalogFileTargets.BindIds(insert, fresh, "$f")}) AND f.LibraryId = d.LibraryId AND f.FolderId <> d.Id;
                        """;
                    linked += insert.ExecuteNonQuery();
                    linkedIds.AddRange(fresh); // gleiche Bibliothek (Kandidaten) und vorab gefiltert — jede Zeile wurde eingefügt
                }

                return new FolderLinkResult(linked, candidates.Length - linked, other, unknown);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        _logger.LogInformation(
            "Ordner {FolderId}: {Linked} verknüpft, {Already} schon dort, {Other} aus anderer Bibliothek, {Unknown} unbekannt",
            folderId, result.Linked, result.AlreadyThere, result.OtherLibrary, result.Unknown);
        if (linkedIds.Count > 0)
        {
            Publish(new FolderLinksChangedMessage(libraryId, folderId, linkedIds));
        }

        return result;
    }

    public async Task<int> UnlinkAsync(int folderId, IReadOnlyList<int> fileIds, bool includeSubfolders = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        var ids = fileIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return 0;
        }

        var libraryId = 0;
        int removed;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            removed = await CatalogFileTargets.WriteAsync(_factory, ids, _logger, (connection, tx, files) =>
            {
                libraryId = ReadFolderLibrary(connection, tx, folderId) ?? 0;
                var parentPath = includeSubfolders ? ReadFolderPath(connection, tx, folderId) : null;
                var count = 0;
                foreach (var chunk in files.Select(f => f.FileId).Chunk(ChunkSize))
                {
                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.Parameters.AddWithValue("$d", folderId);
                    var inList = CatalogFileTargets.BindIds(cmd, chunk, "$f");
                    if (parentPath is not null)
                    {
                        // Ordner und alle Unterordner derselben Bibliothek (Pfad-Präfix wie der Galerie-Ordnerfilter).
                        cmd.Parameters.AddWithValue("$lib", libraryId);
                        cmd.Parameters.AddWithValue("$prefix", EscapeLike(parentPath.TrimEnd('\\', '/') + Path.DirectorySeparatorChar) + "%");
                        cmd.CommandText = $"""
                            DELETE FROM FolderLinks
                            WHERE FileId IN ({inList})
                              AND FolderId IN (SELECT s.Id FROM Folders s WHERE s.LibraryId = $lib AND (s.Id = $d OR s.Path LIKE $prefix ESCAPE '\'));
                            """;
                    }
                    else
                    {
                        cmd.CommandText = $"DELETE FROM FolderLinks WHERE FolderId = $d AND FileId IN ({inList});";
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

        _logger.LogInformation("Ordner {FolderId}: {Count} Verknüpfung(en) entfernt (Fotos bleiben)", folderId, removed);
        if (removed > 0)
        {
            Publish(new FolderLinksChangedMessage(libraryId, folderId, ids));
        }

        return removed;
    }

    public Task<IReadOnlyList<(int FolderId, string Path)>> GetLinkedFoldersAsync(int fileId, CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var connection = _factory.OpenSqliteConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT d.Id, d.Path FROM FolderLinks l JOIN Folders d ON d.Id = l.FolderId WHERE l.FileId = $f ORDER BY d.Path;";
            cmd.Parameters.AddWithValue("$f", fileId);
            var list = new List<(int, string)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add((reader.GetInt32(0), reader.GetString(1)));
            }

            return (IReadOnlyList<(int FolderId, string Path)>)list;
        }, ct);

    private static int? ReadFolderLibrary(SqliteConnection connection, SqliteTransaction tx, int folderId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT LibraryId FROM Folders WHERE Id = $d;";
        cmd.Parameters.AddWithValue("$d", folderId);
        return cmd.ExecuteScalar() is long id ? (int)id : null;
    }

    private static string? ReadFolderPath(SqliteConnection connection, SqliteTransaction tx, int folderId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT Path FROM Folders WHERE Id = $d;";
        cmd.Parameters.AddWithValue("$d", folderId);
        return cmd.ExecuteScalar() as string;
    }

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

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
