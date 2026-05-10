using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace PhotoViewerV3.PerfTests.Spikes.Queries;

internal static class QueryBenchmarks
{
    private const int LibraryId = 1;
    private const int DefaultWarmup = 3;
    private const int DefaultIterations = 5;
    private const int DefaultPageSize = 500;

    public static QueryReport BenchFolderPage(SqliteConnection connection, int folderId, int pageSize = DefaultPageSize)
    {
        const string sql = """
            SELECT Id, Name, Extension, Size, ModifiedAtUtc, MediaType
            FROM Files
            WHERE FolderId = $folderId
            ORDER BY Name
            LIMIT $pageSize;
            """;

        return Measure($"Folder-Page (Folder #{folderId})", connection, sql, cmd =>
        {
            cmd.Parameters.AddWithValue("$folderId", folderId);
            cmd.Parameters.AddWithValue("$pageSize", pageSize);
        });
    }

    public static QueryReport BenchFtsName(SqliteConnection connection, string matchTerm, int pageSize = DefaultPageSize)
    {
        // FTS5-MATCH mit JOIN auf Files für reale Use-Case (User sieht Files-Spalten)
        const string sql = """
            SELECT f.Id, f.Name, f.Extension, f.Size, f.ModifiedAtUtc
            FROM FilesFts ft
            JOIN Files f ON f.Id = ft.rowid
            WHERE FilesFts MATCH $term
            LIMIT $pageSize;
            """;

        return Measure($"FTS-Name MATCH '{matchTerm}'", connection, sql, cmd =>
        {
            cmd.Parameters.AddWithValue("$term", matchTerm);
            cmd.Parameters.AddWithValue("$pageSize", pageSize);
        });
    }

    public static QueryReport BenchMediaTypeFilter(SqliteConnection connection, int mediaType, int pageSize = DefaultPageSize)
    {
        const string sql = """
            SELECT Id, Name, Extension, Size, ModifiedAtUtc
            FROM Files
            WHERE LibraryId = $libId AND MediaType = $mediaType
            ORDER BY ModifiedAtUtc DESC
            LIMIT $pageSize;
            """;

        var label = mediaType == 0 ? "Image" : "Video";
        return Measure($"MediaType-Filter ({label})", connection, sql, cmd =>
        {
            cmd.Parameters.AddWithValue("$libId", LibraryId);
            cmd.Parameters.AddWithValue("$mediaType", mediaType);
            cmd.Parameters.AddWithValue("$pageSize", pageSize);
        });
    }

    public static QueryReport BenchFavoriteFilter(SqliteConnection connection, int pageSize = DefaultPageSize)
    {
        const string sql = """
            SELECT Id, Name, Extension, Size, ModifiedAtUtc
            FROM Files
            WHERE LibraryId = $libId AND IsFavorite = 1
            ORDER BY ModifiedAtUtc DESC
            LIMIT $pageSize;
            """;

        return Measure("Favorit-Filter", connection, sql, cmd =>
        {
            cmd.Parameters.AddWithValue("$libId", LibraryId);
            cmd.Parameters.AddWithValue("$pageSize", pageSize);
        });
    }

    public static int FindHeaviestFolder(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT FolderId
            FROM Files
            GROUP BY FolderId
            ORDER BY COUNT(*) DESC
            LIMIT 1;
            """;
        var result = cmd.ExecuteScalar();
        return Convert.ToInt32(result);
    }

    public static IReadOnlyList<(int FolderId, int Count)> FindFolderRange(SqliteConnection connection, int sampleCount = 5)
    {
        var results = new List<(int FolderId, int Count)>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT FolderId, COUNT(*) AS c
            FROM Files
            GROUP BY FolderId
            ORDER BY c DESC
            LIMIT {sampleCount};
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((reader.GetInt32(0), reader.GetInt32(1)));
        }

        return results;
    }

    public static string ExplainQueryPlan(SqliteConnection connection, string sql, Action<SqliteCommand> bindParams)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        bindParams(cmd);
        using var reader = cmd.ExecuteReader();
        var lines = new List<string>();
        while (reader.Read())
        {
            // Sqlite-Spalten: id, parent, notused, detail
            var detail = reader.GetString(3);
            lines.Add(detail);
        }

        return string.Join(" | ", lines);
    }

    private static QueryReport Measure(
        string name,
        SqliteConnection connection,
        string sql,
        Action<SqliteCommand> bindParams,
        int warmup = DefaultWarmup,
        int iterations = DefaultIterations)
    {
        var plan = ExplainQueryPlan(connection, sql, bindParams);

        // Warmup
        for (var i = 0; i < warmup; i++)
        {
            ExecuteAndCountRows(connection, sql, bindParams);
        }

        var times = new double[iterations];
        var rowsLast = 0;
        for (var i = 0; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            rowsLast = ExecuteAndCountRows(connection, sql, bindParams);
            sw.Stop();
            times[i] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(times);
        var median = times[iterations / 2];
        var p95 = times[(int)Math.Ceiling((iterations - 1) * 0.95)];
        var min = times[0];
        var max = times[iterations - 1];

        return new QueryReport(name, rowsLast, median, p95, min, max, plan);
    }

    private static int ExecuteAndCountRows(SqliteConnection connection, string sql, Action<SqliteCommand> bindParams)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        bindParams(cmd);
        using var reader = cmd.ExecuteReader();
        var rows = 0;
        while (reader.Read())
        {
            rows++;
            // Materialisieren der ersten paar Spalten, damit der Reader nicht trivial-shortcuttet
            _ = reader.GetInt32(0);
        }

        return rows;
    }
}

internal sealed record QueryReport(
    string Name,
    int RowCount,
    double MedianMs,
    double P95Ms,
    double MinMs,
    double MaxMs,
    string Plan);
