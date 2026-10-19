using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Data.RawSql;
using PhotoViewerV3.Services.Database;

namespace PhotoViewerV3.Gallery.Data;

/// <summary>
/// Abfrage-Parameter der Galerie. <paramref name="FolderId"/> null = ganze Library;
/// <paramref name="IncludeSubfolders"/> steuert, ob der Ordnerfilter rekursiv (Pfad-Prefix) wirkt.
/// <paramref name="SearchTerm"/> ist freier Benutzertext; den FTS5-MATCH-Ausdruck baut
/// <see cref="FtsQueryBuilder.FromUserInput"/> (eine Implementierung im Repo) — Text ohne suchbaren Inhalt
/// (leer, nur Sonderzeichen wie <c>---</c>) gilt als „keine Suche“. Die Suche findet außerdem Dateien, deren
/// Tag-Name den Suchtext enthält (Tags, ADR-0021). <paramref name="TagIds"/> + <paramref name="TagMode"/> filtern
/// nach Tags (ODER / UND / ohne Tag).
/// </summary>
public sealed record GalleryQuery(
    int LibraryId,
    GalleryFilter Filter,
    string? SearchTerm,
    int? FolderId = null,
    bool IncludeSubfolders = true,
    GallerySort Sort = GallerySort.ModifiedDesc,
    IReadOnlyList<int>? TagIds = null,
    GalleryTagMode TagMode = GalleryTagMode.Any)
{
    /// <summary>Sicherer FTS5-MATCH-Ausdruck (quotierte Präfix-Phrasen) oder null ohne suchbaren Inhalt.</summary>
    public string? MatchExpression => FtsQueryBuilder.FromUserInput(SearchTerm);

    public bool HasSearch => MatchExpression is not null;
    public bool HasFolder => FolderId is not null;

    /// <summary>True, wenn ein Tag-Filter wirkt (Tags gewählt oder Modus „ohne Tag“).</summary>
    public bool HasTagFilter => TagMode == GalleryTagMode.None || TagIds is { Count: > 0 };
}

/// <summary>
/// Raw-SQLite-Lesepfad der Galerie (ADR-0004 / ADR-0010): eine Verbindung, alle Queries laufen
/// serialisiert auf dem Thread-Pool, damit der UI-Thread nie blockiert. Liefert ausschließlich
/// kleine DTOs — nie mehr als eine Page (≤ 1000 Zeilen) pro Aufruf.
/// </summary>
public sealed class GalleryPageSource : IDisposable
{
    private const string FileColumns =
        "f.Id, f.FolderId, f.Name, f.Extension, f.Size, f.ModifiedAtUtc, f.MediaType, f.IsFavorite, f.Rating, f.Missing, fo.Path, lr.Role";

    /// <summary>Rolle der Wurzel je Datei (Badge „Eingang“, ADR-0020); LEFT JOIN, weil Altzeilen ohne RootId denkbar sind.</summary>
    private const string RootJoin = "LEFT JOIN LibraryRoots lr ON lr.Id = fo.RootId";

    /// <summary>Chunk-Größe für IN-Listen (SQLite-Variablenlimit bleibt weit darunter).</summary>
    private const int InChunkSize = 500;

    private readonly ILibraryContextFactory _factory;
    private readonly ILogger<GalleryPageSource> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;

    public GalleryPageSource(ILibraryContextFactory factory, ILogger<GalleryPageSource>? logger = null)
    {
        _factory = factory;
        _logger = logger ?? NullLogger<GalleryPageSource>.Instance;
    }

    public bool IsOpen => _connection is not null;
    public string DatabasePath => _factory.DatabasePath;

    public void Open()
    {
        if (_connection is not null)
        {
            return;
        }

        _connection = _factory.OpenSqliteConnection();
    }

    /// <summary>Anzahl der Treffer für die Query (Filter, Suche, Ordner).</summary>
    public Task<int> GetTotalCountAsync(GalleryQuery query, CancellationToken ct)
        => RunAsync(conn =>
        {
            var folder = ResolveFolderClause(conn, query);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = BuildCountSql(query, folder.Clause);
            BindFilterParams(cmd, query, folder);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }, ct);

    /// <summary>
    /// Lädt eine Page. Liegt die Page näher am Ende als am Anfang der Trefferliste, wird intern
    /// umgekehrt sortiert, vom Ende her mit kleinem OFFSET gelesen und clientseitig gedreht
    /// (Reverse-Trick aus Spike #02, Folge-Erkenntnis 1 — ⏭ bleibt auch bei 500k unter 150 ms).
    /// <paramref name="totalCount"/> ist der zuletzt gezählte Gesamtwert (0 = unbekannt → kein Reverse).
    /// </summary>
    public Task<IReadOnlyList<GalleryFileDto>> LoadPageAsync(
        GalleryQuery query,
        int pageIndex,
        int pageSize,
        int totalCount,
        CancellationToken ct)
        => RunAsync<IReadOnlyList<GalleryFileDto>>(conn =>
        {
            var offsetFromStart = (long)pageIndex * pageSize;
            var remaining = Math.Max(0L, totalCount - offsetFromStart);
            var limitFromEnd = (int)Math.Min(pageSize, remaining);
            var offsetFromEnd = totalCount - offsetFromStart - limitFromEnd;
            var useReverse = totalCount > 0 && limitFromEnd > 0 && offsetFromEnd < offsetFromStart;

            var folder = ResolveFolderClause(conn, query);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = BuildPageSql(query, folder.Clause, reverse: useReverse);
            BindFilterParams(cmd, query, folder);
            cmd.Parameters.AddWithValue("$limit", useReverse ? limitFromEnd : pageSize);
            cmd.Parameters.AddWithValue("$offset", useReverse ? offsetFromEnd : offsetFromStart);

            var sw = Stopwatch.StartNew();
            var rows = new List<GalleryFileDto>(pageSize);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                rows.Add(ReadRow(reader));
            }

            if (useReverse)
            {
                rows.Reverse();
            }

            sw.Stop();
            _logger.LogDebug(
                "Page-SQL: Idx={Idx} Size={Size} Rows={Rows} Reverse={Reverse} Offset={Offset} Sort={Sort} Folder={Folder} Rek={Rek} Suche={Search} {Ms:N1} ms",
                pageIndex, pageSize, rows.Count, useReverse, useReverse ? offsetFromEnd : offsetFromStart,
                query.Sort, query.FolderId?.ToString() ?? "-", query.IncludeSubfolders, query.SearchTerm ?? "-",
                sw.Elapsed.TotalMilliseconds);
            return rows;
        }, ct);

    /// <summary>Stammdaten eines Ordners oder null, wenn er nicht (mehr) existiert.</summary>
    public Task<GalleryFolderInfo?> GetFolderInfoAsync(int folderId, CancellationToken ct)
        => RunAsync(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, Name, Path, ParentId FROM Folders WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", folderId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new GalleryFolderInfo(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3));
        }, ct);

    /// <summary>
    /// Nächste Unterordner eines Ordners mit rekursiver Dateianzahl (Container-State, PLAN Kap. 16).
    /// Pfad-basiert statt rein über ParentId, weil der Scanner nur Ordner mit Dateien anlegt und
    /// ParentId-Ketten damit Lücken haben können; ein Unterordner ohne eigene Zeile erscheint dann
    /// über seinen nächsten erfassten Nachfahren (Name = relativer Pfad). Zwei Index-Queries
    /// (Files GROUP BY FolderId, Folders per Prefix) und Aggregation im Speicher — kein LIKE-Scan
    /// über 500k Files-Zeilen pro Unterordner.
    /// </summary>
    public Task<IReadOnlyList<GallerySubfolderDto>> GetSubfoldersAsync(int libraryId, int folderId, CancellationToken ct)
        => RunAsync<IReadOnlyList<GallerySubfolderDto>>(conn =>
        {
            string? parentPath;
            using (var pathCmd = conn.CreateCommand())
            {
                pathCmd.CommandText = "SELECT Path FROM Folders WHERE Id = $id;";
                pathCmd.Parameters.AddWithValue("$id", folderId);
                parentPath = pathCmd.ExecuteScalar() as string;
            }

            if (parentPath is null)
            {
                return Array.Empty<GallerySubfolderDto>();
            }

            var parentTrimmed = TrimSeparators(parentPath);
            var prefix = parentTrimmed + Path.DirectorySeparatorChar;

            var descendants = new List<(int Id, string Path)>();
            using (var folderCmd = conn.CreateCommand())
            {
                folderCmd.CommandText = """
                    SELECT Id, Path FROM Folders
                    WHERE LibraryId = $libId AND Path LIKE $prefix ESCAPE '\';
                    """;
                folderCmd.Parameters.AddWithValue("$libId", libraryId);
                folderCmd.Parameters.AddWithValue("$prefix", EscapeLike(prefix) + "%");
                using var reader = folderCmd.ExecuteReader();
                while (reader.Read())
                {
                    descendants.Add((reader.GetInt32(0), reader.GetString(1)));
                }
            }

            if (descendants.Count == 0)
            {
                return Array.Empty<GallerySubfolderDto>();
            }

            ct.ThrowIfCancellationRequested();

            var directCounts = new Dictionary<int, int>();
            using (var countCmd = conn.CreateCommand())
            {
                countCmd.CommandText = "SELECT FolderId, COUNT(*) FROM Files WHERE LibraryId = $libId GROUP BY FolderId;";
                countCmd.Parameters.AddWithValue("$libId", libraryId);
                using var reader = countCmd.ExecuteReader();
                while (reader.Read())
                {
                    directCounts[reader.GetInt32(0)] = reader.GetInt32(1);
                }
            }

            var known = new HashSet<string>(descendants.Select(d => TrimSeparators(d.Path)), StringComparer.OrdinalIgnoreCase);
            var result = new List<GallerySubfolderDto>();
            foreach (var (id, path) in descendants)
            {
                var trimmed = TrimSeparators(path);

                // Direkter Unterordner = zwischen ihm und dem Elternordner liegt kein weiterer erfasster Ordner.
                var isDirect = true;
                var ancestor = Path.GetDirectoryName(trimmed);
                while (!string.IsNullOrEmpty(ancestor) && !ancestor.Equals(parentTrimmed, StringComparison.OrdinalIgnoreCase))
                {
                    if (known.Contains(ancestor))
                    {
                        isDirect = false;
                        break;
                    }

                    ancestor = Path.GetDirectoryName(ancestor);
                }

                if (!isDirect)
                {
                    continue;
                }

                var childPrefix = trimmed + Path.DirectorySeparatorChar;
                var count = 0;
                foreach (var (otherId, otherPath) in descendants)
                {
                    if (otherId == id
                        || otherPath.StartsWith(childPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        count += directCounts.GetValueOrDefault(otherId);
                    }
                }

                var displayName = trimmed.Length > prefix.Length ? trimmed[prefix.Length..] : trimmed;
                result.Add(new GallerySubfolderDto(id, displayName, path, count));
            }

            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return result;
        }, ct);

    private static string TrimSeparators(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>Favorit/Bewertung/Missing für konkrete Dateien (nach FilesChangedMessage Kind=Metadata).</summary>
    public Task<IReadOnlyList<GalleryMetadataDto>> GetMetadataAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct)
        => RunAsync<IReadOnlyList<GalleryMetadataDto>>(conn =>
        {
            var result = new List<GalleryMetadataDto>(fileIds.Count);
            foreach (var chunk in fileIds.Chunk(InChunkSize))
            {
                ct.ThrowIfCancellationRequested();
                using var cmd = conn.CreateCommand();
                var sb = new StringBuilder("SELECT Id, IsFavorite, Rating, Missing FROM Files WHERE Id IN (");
                for (var i = 0; i < chunk.Length; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    var name = "$p" + i;
                    sb.Append(name);
                    cmd.Parameters.AddWithValue(name, chunk[i]);
                }

                sb.Append(");");
                cmd.CommandText = sb.ToString();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new GalleryMetadataDto(
                        reader.GetInt32(0),
                        reader.GetInt32(1) == 1,
                        reader.IsDBNull(2) ? null : (byte)reader.GetInt32(2),
                        reader.GetInt32(3) == 1));
                }
            }

            return result;
        }, ct);

    // ---- SQL-Aufbau -------------------------------------------------------------------------

    private readonly record struct FolderClause(string Clause, int? FolderId, string? Path, string? Prefix);

    /// <summary>
    /// Ordnerfilter: direkt über FolderId; rekursiv über den Ordnerpfad als Prefix auf Folders
    /// (<c>Path = $folderPath OR Path LIKE $folderPrefix ESCAPE '\'</c>). Der Prefix-Vergleich
    /// läuft als IN-Subquery über die (kleine) Folders-Tabelle statt pro Files-Zeile.
    /// </summary>
    private static FolderClause ResolveFolderClause(SqliteConnection conn, GalleryQuery query)
    {
        if (query.FolderId is not { } folderId)
        {
            return new FolderClause(string.Empty, null, null, null);
        }

        if (!query.IncludeSubfolders)
        {
            return new FolderClause("AND f.FolderId = $folderId", folderId, null, null);
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Path FROM Folders WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", folderId);
        var path = cmd.ExecuteScalar() as string;
        if (path is null)
        {
            // Ordner existiert nicht mehr → direkte FolderId-Bedingung liefert 0 Treffer (kein Absturz).
            return new FolderClause("AND f.FolderId = $folderId", folderId, null, null);
        }

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefix = EscapeLike(trimmed + Path.DirectorySeparatorChar) + "%";
        const string clause = """
            AND f.FolderId IN (
                SELECT sf.Id FROM Folders sf
                WHERE sf.LibraryId = $libId
                  AND (sf.Path = $folderPath OR sf.Path LIKE $folderPrefix ESCAPE '\'))
            """;
        return new FolderClause(clause, folderId, path, prefix);
    }

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string BuildPageSql(GalleryQuery query, string folderClause, bool reverse)
    {
        var orderBy = BuildOrderBy(query.Sort, reverse);
        return $"""
            SELECT {FileColumns}
            FROM {BuildFilesSource(query)}
            JOIN Folders fo ON fo.Id = f.FolderId
            {RootJoin}
            WHERE f.LibraryId = $libId
              {BuildFilterClause(query.Filter)}
              {BuildTagClause(query)}
              {folderClause}
            ORDER BY {orderBy}
            LIMIT $limit OFFSET $offset;
            """;
    }

    private static string BuildCountSql(GalleryQuery query, string folderClause)
    {
        return $"""
            SELECT COUNT(*)
            FROM {BuildFilesSource(query)}
            WHERE f.LibraryId = $libId
              {BuildFilterClause(query.Filter)}
              {BuildTagClause(query)}
              {folderClause};
            """;
    }

    /// <summary>
    /// Quelle der Files-Zeilen. Mit Suche treiben die Treffer die Abfrage wie vor 0.2.6 (damals
    /// <c>FROM FilesFts JOIN Files</c>): FTS5-Treffer (Name/Endung, Präfix-Phrasen) vereinigt mit den Dateien, deren
    /// Tag-Name den Suchtext enthält (NOCASE-Spalte, LIKE, ADR-0021), einmal materialisiert und per
    /// <c>CROSS JOIN</c> (feste Join-Reihenfolge) über die Rowid an Files gebunden — kein Durchlauf aller Dateien der
    /// Library. Ohne Suche einfach <c>Files f</c>.
    /// </summary>
    private static string BuildFilesSource(GalleryQuery query) => query.HasSearch
        ? """
          (SELECT rowid AS Id FROM FilesFts WHERE FilesFts MATCH $term
           UNION
           SELECT ft.FileId FROM FileTags ft JOIN Tags t ON t.Id = ft.TagId WHERE t.Name LIKE $tagLike ESCAPE '\') hit
          CROSS JOIN Files f ON f.Id = hit.Id
          """
        : "Files f";

    /// <summary>Tag-Filter: ODER (mindestens eines), UND (alle gewählten) oder „ohne Tag“ (ADR-0021).</summary>
    private static string BuildTagClause(GalleryQuery query)
    {
        if (query.TagMode == GalleryTagMode.None)
        {
            return "AND NOT EXISTS (SELECT 1 FROM FileTags ft WHERE ft.FileId = f.Id)";
        }

        if (query.TagIds is not { Count: > 0 } tagIds)
        {
            return string.Empty;
        }

        var inList = string.Join(",", Enumerable.Range(0, tagIds.Count).Select(i => "$tag" + i));
        return query.TagMode == GalleryTagMode.All
            ? $"AND f.Id IN (SELECT ft.FileId FROM FileTags ft WHERE ft.TagId IN ({inList}) GROUP BY ft.FileId HAVING COUNT(DISTINCT ft.TagId) = $tagCount)"
            : $"AND f.Id IN (SELECT ft.FileId FROM FileTags ft WHERE ft.TagId IN ({inList}))";
    }

    /// <summary>LIKE-Muster „%text%“ für die Tag-Namenssuche (Sonderzeichen von LIKE escaped).</summary>
    internal static string BuildTagLikePattern(string searchTerm) => "%" + EscapeLike(searchTerm.Trim()) + "%";

    /// <summary>Sortierspalte + Richtung; Id als stabile Zweitsortierung in derselben Richtung.</summary>
    private static string BuildOrderBy(GallerySort sort, bool reverse)
    {
        var (column, descending) = sort switch
        {
            GallerySort.ModifiedAsc => ("f.ModifiedAtUtc", false),
            GallerySort.NameAsc => ("f.Name COLLATE NOCASE", false),
            GallerySort.NameDesc => ("f.Name COLLATE NOCASE", true),
            GallerySort.SizeDesc => ("f.Size", true),
            GallerySort.SizeAsc => ("f.Size", false),
            _ => ("f.ModifiedAtUtc", true),
        };

        if (reverse)
        {
            descending = !descending;
        }

        var direction = descending ? "DESC" : "ASC";
        return $"{column} {direction}, f.Id {direction}";
    }

    /// <summary>
    /// Status-Filter (ADR-0020) über die (kleine) Folders-Tabelle: Ordner, deren Wurzel die Rolle Eingang (0) bzw.
    /// Archiv (1) hat — als IN-Subquery, damit die Count-Query ohne Join auskommt.
    /// </summary>
    private const string InboxClause =
        "AND f.FolderId IN (SELECT d.Id FROM Folders d JOIN LibraryRoots r ON r.Id = d.RootId WHERE d.LibraryId = $libId AND r.Role = 0)";

    private const string ArchiveClause =
        "AND f.FolderId IN (SELECT d.Id FROM Folders d JOIN LibraryRoots r ON r.Id = d.RootId WHERE d.LibraryId = $libId AND r.Role = 1)";

    private static string BuildFilterClause(GalleryFilter filter) => filter switch
    {
        GalleryFilter.Images => "AND f.MediaType = 0",
        GalleryFilter.Videos => "AND f.MediaType = 1",
        GalleryFilter.Favorites => "AND f.IsFavorite = 1",
        GalleryFilter.Archives => "AND f.MediaType = 2",
        GalleryFilter.NotArchived => InboxClause,
        GalleryFilter.Archived => ArchiveClause,
        _ => string.Empty,
    };

    private static void BindFilterParams(SqliteCommand cmd, GalleryQuery query, FolderClause folder)
    {
        cmd.Parameters.AddWithValue("$libId", query.LibraryId);
        if (query.MatchExpression is { } matchExpression)
        {
            cmd.Parameters.AddWithValue("$term", matchExpression);
            cmd.Parameters.AddWithValue("$tagLike", BuildTagLikePattern(query.SearchTerm!));
        }

        if (query.TagMode != GalleryTagMode.None && query.TagIds is { Count: > 0 } tagIds)
        {
            for (var i = 0; i < tagIds.Count; i++)
            {
                cmd.Parameters.AddWithValue("$tag" + i, tagIds[i]);
            }

            if (query.TagMode == GalleryTagMode.All)
            {
                cmd.Parameters.AddWithValue("$tagCount", tagIds.Distinct().Count());
            }
        }

        if (folder.FolderId is { } folderId)
        {
            if (folder.Path is null)
            {
                cmd.Parameters.AddWithValue("$folderId", folderId);
            }
            else
            {
                cmd.Parameters.AddWithValue("$folderPath", folder.Path);
                cmd.Parameters.AddWithValue("$folderPrefix", folder.Prefix!);
            }
        }
    }

    private static GalleryFileDto ReadRow(SqliteDataReader reader) => new(
        Id: reader.GetInt32(0),
        FolderId: reader.GetInt32(1),
        Name: reader.GetString(2),
        Extension: reader.GetString(3),
        Size: reader.GetInt64(4),
        ModifiedAtUnix: reader.GetInt64(5),
        MediaType: reader.GetInt32(6),
        IsFavorite: reader.GetInt32(7) == 1,
        Rating: reader.IsDBNull(8) ? null : (byte)reader.GetInt32(8),
        Missing: reader.GetInt32(9) == 1,
        FolderPath: reader.GetString(10),
        RootRole: reader.IsDBNull(11) ? null : reader.GetInt32(11));

    // ---- Ausführung -------------------------------------------------------------------------

    /// <summary>Serialisiert alle Zugriffe auf die eine Verbindung und hält sie vom UI-Thread fern.</summary>
    private async Task<T> RunAsync<T>(Func<SqliteConnection, T> work, CancellationToken ct)
    {
        var conn = EnsureOpen();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => work(conn), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private SqliteConnection EnsureOpen()
    {
        if (_connection is null)
        {
            throw new InvalidOperationException("GalleryPageSource ist nicht geöffnet. Open() vor dem ersten Query aufrufen.");
        }

        return _connection;
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
        _gate.Dispose();
    }
}
