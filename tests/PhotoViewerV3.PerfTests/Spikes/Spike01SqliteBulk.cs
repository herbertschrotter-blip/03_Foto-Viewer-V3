using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using PhotoViewerV3.PerfTests.Spikes.Bulk;
using PhotoViewerV3.PerfTests.Spikes.Queries;
using PhotoViewerV3.PerfTests.Spikes.Schema;
using PhotoViewerV3.PerfTests.Spikes.SyntheticData;
using PhotoViewerV3.Services.Infrastructure;

namespace PhotoViewerV3.PerfTests.Spikes;

internal static class Spike01SqliteBulk
{
    private const int DefaultRowCount = 500_000;
    private const int DefaultFolderCount = 10_000;
    private const int FolderSeed = 20260510;
    private const int FileSeed = 20260511;
    private const string SpikeFolderName = "spike-01";
    private const string SpikeDbFileName = "library-spike.db";

    public static Task<int> RunAsync(string[] args)
    {
        var options = ParseArgs(args);

        Console.WriteLine("Spike #01 — SQLite-Bulk + FTS5");
        Console.WriteLine($"  Rows:     {options.RowCount:N0}");
        Console.WriteLine($"  Folders:  {options.FolderCount:N0}");
        Console.WriteLine($"  DB:       {options.DbPath}");
        Console.WriteLine($"  Validate: {(options.ValidateGenerator ? "ja" : "nein")}");
        Console.WriteLine();

        var sw = Stopwatch.StartNew();
        Console.WriteLine("[1] Generiere Folders…");
        var folders = SyntheticDataGenerator.GenerateFolders(options.FolderCount, FolderSeed);
        sw.Stop();
        Console.WriteLine($"    {folders.Count:N0} Folders in {sw.Elapsed.TotalMilliseconds,8:N1} ms");

        if (options.ValidateGenerator)
        {
            sw.Restart();
            Console.WriteLine("[2] Validiere File-Stream (Generator-Check, kein DB-Insert)…");
            var summary = SummarizeFileStream(folders, options.RowCount);
            sw.Stop();
            Console.WriteLine($"    {summary.Total:N0} Files in {sw.Elapsed.TotalMilliseconds,8:N1} ms");
            PrintDistribution(summary);
            PrintFolderTopN(folders, summary);
        }

        Console.WriteLine();
        Console.WriteLine("[3] Bereite Datenbank vor…");
        sw.Restart();
        SchemaBuilder.DropDatabaseFiles(options.DbPath);
        var dropMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        using var connection = SchemaBuilder.OpenAndPrepare(options.DbPath);
        var openMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        SchemaBuilder.CreateSchema(connection);
        var schemaMs = sw.Elapsed.TotalMilliseconds;

        Console.WriteLine($"    Alte DB gelöscht:   {dropMs,8:N1} ms");
        Console.WriteLine($"    Open + PRAGMAs:     {openMs,8:N1} ms");
        Console.WriteLine($"    Schema + Indizes:   {schemaMs,8:N1} ms");
        Console.WriteLine($"    Gesamt Phase 3:     {dropMs + openMs + schemaMs,8:N1} ms");

        PrintSchemaInfo(connection);

        Console.WriteLine();
        Console.WriteLine("[4] Bulk-Insert Folders…");
        var folderReport = BulkInserter.InsertFolders(connection, folders);
        Console.WriteLine($"    {folderReport.RowCount:N0} Folders in {folderReport.Duration.TotalMilliseconds,8:N1} ms");

        Console.WriteLine();
        Console.WriteLine("[5] Bulk-Insert Files (Prepared Command + single Transaction)…");
        var fileReport = BulkInserter.InsertFiles(
            connection,
            SyntheticDataGenerator.GenerateFiles(folders, options.RowCount, FileSeed),
            options.RowCount,
            onProgress: (rows, elapsed) =>
            {
                var rps = rows / elapsed.TotalSeconds;
                Console.WriteLine($"    {rows,8:N0} / {options.RowCount:N0}   ({100.0 * rows / options.RowCount,5:N1} %)   {elapsed.TotalSeconds,6:N1} s   {rps,9:N0} rows/s");
            });
        Console.WriteLine();
        Console.WriteLine($"    Versucht:   {fileReport.Attempted,10:N0}  ({fileReport.RowsPerSecond,8:N0} rows/s)");
        Console.WriteLine($"    Eingefügt:  {fileReport.Inserted,10:N0}");
        Console.WriteLine($"    Übersprungen (UNIQUE-Kollision): {fileReport.Skipped,10:N0}");
        Console.WriteLine($"    Insert:     {fileReport.InsertDuration.TotalSeconds,7:N2} s");
        Console.WriteLine($"    Commit:     {fileReport.CommitDuration.TotalMilliseconds,7:N1} ms");
        Console.WriteLine($"    Total:      {fileReport.TotalDuration.TotalSeconds,7:N2} s");

        Console.WriteLine();
        Console.WriteLine("[6] FTS5-Population (rowid+Name+Extension)…");
        var ftsReport = BulkInserter.PopulateFts(connection);
        Console.WriteLine($"    {ftsReport.RowCount:N0} Rows in FilesFts in {ftsReport.Duration.TotalSeconds:N2} s");

        Console.WriteLine();
        Console.WriteLine("[7] ANALYZE + WAL-Checkpoint (für saubere Größenmessung)…");
        var analyzeDuration = BulkInserter.Analyze(connection);
        var checkpointDuration = BulkInserter.WalCheckpoint(connection);
        Console.WriteLine($"    ANALYZE:         {analyzeDuration.TotalMilliseconds,8:N1} ms");
        Console.WriteLine($"    WAL-Checkpoint:  {checkpointDuration.TotalMilliseconds,8:N1} ms");

        Console.WriteLine();
        PrintDbSize(options.DbPath);

        var totalBulk = folderReport.Duration + fileReport.TotalDuration + ftsReport.Duration;
        Console.WriteLine();
        Console.WriteLine($"=== Bulk-Phase gesamt: {totalBulk.TotalSeconds:N2} s ({totalBulk.TotalMinutes:N2} min) ===");
        Console.WriteLine($"    Ziel SSD: < 5 min  →  {(totalBulk.TotalMinutes < 5 ? "GRÜN" : "ROT")}");

        Console.WriteLine();
        Console.WriteLine("[8] Query-Benchmarks (3 Warmup + 5 Messungen, Median/p95)…");
        RunQueryBenchmarks(connection);

        connection.Close();
        Console.WriteLine();
        Console.WriteLine("Spike #01 abgeschlossen.");
        return Task.FromResult(0);
    }

    private static void RunQueryBenchmarks(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        var reports = new List<QueryReport>();

        var folderSamples = QueryBenchmarks.FindFolderRange(connection, sampleCount: 3);
        Console.WriteLine($"    Heavy-Folders: {string.Join(", ", folderSamples.Select(f => $"#{f.FolderId} ({f.Count})"))}");
        Console.WriteLine();

        foreach (var (folderId, _) in folderSamples)
        {
            reports.Add(QueryBenchmarks.BenchFolderPage(connection, folderId));
        }

        foreach (var term in new[] { "Urlaub", "Geburtstag", "IMG", "Berge" })
        {
            reports.Add(QueryBenchmarks.BenchFtsName(connection, term));
        }

        reports.Add(QueryBenchmarks.BenchMediaTypeFilter(connection, mediaType: 0));
        reports.Add(QueryBenchmarks.BenchMediaTypeFilter(connection, mediaType: 1));
        reports.Add(QueryBenchmarks.BenchFavoriteFilter(connection));

        PrintQueryReports(reports);
    }

    private static void PrintQueryReports(List<QueryReport> reports)
    {
        Console.WriteLine();
        Console.WriteLine($"    {"Query",-32} {"Rows",6}  {"Median",10} {"p95",10} {"Min",10} {"Max",10}");
        Console.WriteLine($"    {new string('-', 32)} {new string('-', 6)}  {new string('-', 10)} {new string('-', 10)} {new string('-', 10)} {new string('-', 10)}");
        foreach (var r in reports)
        {
            Console.WriteLine($"    {Truncate(r.Name, 32),-32} {r.RowCount,6}  {r.MedianMs,8:N2} ms {r.P95Ms,7:N2} ms {r.MinMs,7:N2} ms {r.MaxMs,7:N2} ms");
        }

        Console.WriteLine();
        Console.WriteLine("    Zielwerte:");
        var folderPageReports = reports.Where(r => r.Name.StartsWith("Folder-Page")).ToList();
        var ftsReports = reports.Where(r => r.Name.StartsWith("FTS")).ToList();
        var mediaReports = reports.Where(r => r.Name.StartsWith("MediaType")).ToList();
        if (folderPageReports.Count > 0)
        {
            var worst = folderPageReports.Max(r => r.MedianMs);
            Console.WriteLine($"      Folder-Page  worst Median {worst:N2} ms   Ziel < 100 ms  →  {(worst < 100 ? "GRÜN" : "ROT")}");
        }
        if (ftsReports.Count > 0)
        {
            var worst = ftsReports.Max(r => r.MedianMs);
            Console.WriteLine($"      FTS-Name     worst Median {worst:N2} ms   Ziel < 200 ms  →  {(worst < 200 ? "GRÜN" : "ROT")}");
        }
        if (mediaReports.Count > 0)
        {
            var worst = mediaReports.Max(r => r.MedianMs);
            Console.WriteLine($"      MediaType    worst Median {worst:N2} ms   Ziel < 100 ms  →  {(worst < 100 ? "GRÜN" : "ROT")}");
        }

        Console.WriteLine();
        Console.WriteLine("    EXPLAIN QUERY PLAN (Index-Verifikation):");
        foreach (var r in reports)
        {
            Console.WriteLine($"      [{r.Name}]");
            Console.WriteLine($"        {r.Plan}");
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static void PrintDbSize(string dbPath)
    {
        Console.WriteLine("    Dateigrößen:");
        foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            if (File.Exists(path))
            {
                var size = new FileInfo(path).Length;
                Console.WriteLine($"      {Path.GetFileName(path),-30} {size / 1024.0 / 1024.0,8:N2} MB");
            }
        }
    }

    private static void PrintSchemaInfo(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        Console.WriteLine();
        Console.WriteLine("    PRAGMA-Verifikation:");
        PrintPragma(connection, "journal_mode");
        PrintPragma(connection, "synchronous");
        PrintPragma(connection, "busy_timeout");
        PrintPragma(connection, "temp_store");
        PrintPragma(connection, "foreign_keys");

        Console.WriteLine();
        Console.WriteLine("    Indizes (sqlite_master):");
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name, tbl_name FROM sqlite_master WHERE type='index' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            Console.WriteLine($"      {reader.GetString(0),-50} on {reader.GetString(1)}");
        }
    }

    private static void PrintPragma(Microsoft.Data.Sqlite.SqliteConnection connection, string name)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA {name};";
        var value = cmd.ExecuteScalar();
        Console.WriteLine($"      {name,-18} = {value}");
    }

    private static StreamSummary SummarizeFileStream(IReadOnlyList<SyntheticFolder> folders, int rowCount)
    {
        var perFolder = new int[folders.Count];
        var totalImage = 0;
        var totalVideo = 0;
        var totalFavorite = 0;
        long totalBytes = 0;
        var total = 0;

        var folderIdToIndex = new Dictionary<int, int>(folders.Count);
        for (var i = 0; i < folders.Count; i++)
        {
            folderIdToIndex[folders[i].Id] = i;
        }

        foreach (var file in SyntheticDataGenerator.GenerateFiles(folders, rowCount, FileSeed))
        {
            perFolder[folderIdToIndex[file.FolderId]]++;
            if (file.MediaType == SyntheticMediaType.Video)
            {
                totalVideo++;
            }
            else
            {
                totalImage++;
            }

            if (file.IsFavorite)
            {
                totalFavorite++;
            }

            totalBytes += file.Size;
            total++;
        }

        return new StreamSummary(total, totalImage, totalVideo, totalFavorite, totalBytes, perFolder);
    }

    private static void PrintDistribution(StreamSummary s)
    {
        var imgPct = s.Total == 0 ? 0 : 100.0 * s.Image / s.Total;
        var vidPct = s.Total == 0 ? 0 : 100.0 * s.Video / s.Total;
        var favPct = s.Total == 0 ? 0 : 100.0 * s.Favorite / s.Total;
        Console.WriteLine($"    Images:    {s.Image,10:N0} ({imgPct,5:N1} %)");
        Console.WriteLine($"    Videos:    {s.Video,10:N0} ({vidPct,5:N1} %)");
        Console.WriteLine($"    Favorites: {s.Favorite,10:N0} ({favPct,5:N1} %)");
    }

    private static void PrintFolderTopN(IReadOnlyList<SyntheticFolder> folders, StreamSummary s)
    {
        var indexed = s.PerFolderCount
            .Select((c, i) => (Folder: folders[i], Count: c))
            .OrderByDescending(t => t.Count)
            .ToList();

        var heavyCount = Math.Max(1, (int)(folders.Count * 0.20));
        var heavyFiles = indexed.Take(heavyCount).Sum(t => t.Count);
        var heavyPct = s.Total == 0 ? 0 : 100.0 * heavyFiles / s.Total;
        Console.WriteLine($"    Pareto:    Top-20 % Folders → {heavyPct:N1} % der Files");
    }

    private static SpikeOptions ParseArgs(string[] args)
    {
        var rowCount = DefaultRowCount;
        var folderCount = DefaultFolderCount;
        string? dbPath = null;
        var validateGenerator = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--rows" when i + 1 < args.Length:
                    rowCount = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--folders" when i + 1 < args.Length:
                    folderCount = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--db" when i + 1 < args.Length:
                    dbPath = args[++i];
                    break;
                case "--validate-generator":
                    validateGenerator = true;
                    break;
            }
        }

        dbPath ??= BuildDefaultDbPath();
        return new SpikeOptions(rowCount, folderCount, dbPath, validateGenerator);
    }

    private static string BuildDefaultDbPath()
    {
        var paths = new AppDataPaths();
        return Path.Combine(paths.AppDataRoot, SpikeFolderName, SpikeDbFileName);
    }

    private sealed record SpikeOptions(int RowCount, int FolderCount, string DbPath, bool ValidateGenerator);

    private sealed record StreamSummary(
        int Total,
        int Image,
        int Video,
        int Favorite,
        long TotalBytes,
        int[] PerFolderCount);
}
