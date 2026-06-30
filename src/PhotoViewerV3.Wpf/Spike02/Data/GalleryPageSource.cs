using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PhotoViewerV3.Services.Database;

namespace PhotoViewerV3.Spike02.Data;

public sealed record GalleryQuery(int LibraryId, GalleryFilter Filter, string? SearchTerm)
{
    public bool HasSearch => !string.IsNullOrWhiteSpace(SearchTerm);
}

public sealed class GalleryPageSource : IDisposable
{
    private readonly ILibraryContextFactory _factory;
    private SqliteConnection? _connection;

    public GalleryPageSource(ILibraryContextFactory factory)
    {
        _factory = factory;
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

    public int GetTotalCount(GalleryQuery query)
    {
        var conn = EnsureOpen();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = BuildCountSql(query);
        BindFilterParams(cmd, query);
        var result = cmd.ExecuteScalar();
        return Convert.ToInt32(result);
    }

    public async Task<IReadOnlyList<GalleryFileDto>> LoadPageAsync(
        GalleryQuery query,
        int pageIndex,
        int pageSize,
        CancellationToken ct)
    {
        var conn = EnsureOpen();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = BuildPageSql(query);
        BindFilterParams(cmd, query);
        cmd.Parameters.AddWithValue("$pageSize", pageSize);
        cmd.Parameters.AddWithValue("$offset", pageIndex * pageSize);

        var rows = new List<GalleryFileDto>(pageSize);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new GalleryFileDto(
                Id: reader.GetInt32(0),
                FolderId: reader.GetInt32(1),
                Name: reader.GetString(2),
                Extension: reader.GetString(3),
                Size: reader.GetInt64(4),
                ModifiedAtUnix: reader.GetInt64(5),
                MediaType: reader.GetInt32(6),
                IsFavorite: reader.GetInt32(7) == 1));
        }

        return rows;
    }

    private static string BuildPageSql(GalleryQuery query)
    {
        if (query.HasSearch)
        {
            return $"""
                SELECT f.Id, f.FolderId, f.Name, f.Extension, f.Size, f.ModifiedAtUtc, f.MediaType, f.IsFavorite
                FROM FilesFts ft
                JOIN Files f ON f.Id = ft.rowid
                WHERE FilesFts MATCH $term
                  AND f.LibraryId = $libId
                  {BuildFilterClause(query.Filter, "f.")}
                ORDER BY f.ModifiedAtUtc DESC, f.Id DESC
                LIMIT $pageSize OFFSET $offset;
                """;
        }

        return $"""
            SELECT Id, FolderId, Name, Extension, Size, ModifiedAtUtc, MediaType, IsFavorite
            FROM Files
            WHERE LibraryId = $libId
              {BuildFilterClause(query.Filter, "")}
            ORDER BY ModifiedAtUtc DESC, Id DESC
            LIMIT $pageSize OFFSET $offset;
            """;
    }

    private static string BuildCountSql(GalleryQuery query)
    {
        if (query.HasSearch)
        {
            return $"""
                SELECT COUNT(*)
                FROM FilesFts ft
                JOIN Files f ON f.Id = ft.rowid
                WHERE FilesFts MATCH $term
                  AND f.LibraryId = $libId
                  {BuildFilterClause(query.Filter, "f.")};
                """;
        }

        return $"""
            SELECT COUNT(*)
            FROM Files
            WHERE LibraryId = $libId
              {BuildFilterClause(query.Filter, "")};
            """;
    }

    private static string BuildFilterClause(GalleryFilter filter, string prefix) => filter switch
    {
        GalleryFilter.Images => $"AND {prefix}MediaType = 0",
        GalleryFilter.Videos => $"AND {prefix}MediaType = 1",
        GalleryFilter.Favorites => $"AND {prefix}IsFavorite = 1",
        _ => string.Empty,
    };

    private static void BindFilterParams(SqliteCommand cmd, GalleryQuery query)
    {
        cmd.Parameters.AddWithValue("$libId", query.LibraryId);
        if (query.HasSearch)
        {
            cmd.Parameters.AddWithValue("$term", query.SearchTerm!);
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
    }
}
