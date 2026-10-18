using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Data.RawSql;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Infrastructure;
using PhotoViewerV3.Services.Media;
using PhotoViewerV3.Services.Scanning;

namespace PhotoViewerV3.PerfTests.Spikes.V01Scan;

/// <summary>
/// CLI-End-to-End-Test der V0.1-Schichten Domain + Data + Services:
/// Migration → Library erzeugen → Scan → DB-Schreibe → FTS-Indexierung.
/// </summary>
internal static class SpikeV01Scan
{
    public static async Task<int> RunAsync(string[] args)
    {
        var rootPath = ParseArg(args, "--root");
        var libraryName = ParseArg(args, "--name") ?? "TestLibrary";
        var clear = args.Contains("--clear");

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            Console.Error.WriteLine("Usage: spike-v01-scan --root PATH [--name NAME] [--clear]");
            Console.Error.WriteLine("  --root PATH    Foto-Ordner zum Scannen (Pflicht)");
            Console.Error.WriteLine("  --name NAME    Library-Name (Default 'TestLibrary')");
            Console.Error.WriteLine("  --clear        Vorher Files/Folders/Libraries leeren (ScanMode.Fresh-Sicherheit)");
            return 1;
        }

        if (!Directory.Exists(rootPath))
        {
            Console.Error.WriteLine($"RootPath nicht erreichbar: {rootPath}");
            return 2;
        }

        Console.WriteLine("Spike V01 — End-to-End Scan");
        Console.WriteLine($"  Root:    {rootPath}");
        Console.WriteLine($"  Library: {libraryName}");
        Console.WriteLine($"  Clear:   {clear}");
        Console.WriteLine();

        // Manuelle DI (PerfTests ist Console ohne HostBuilder)
        var paths = new AppDataPaths();
        paths.EnsureDirectoriesExist();
        var interceptor = new SqlitePragmaInterceptor();
        var factory = new LibraryContextFactory(paths, interceptor);
        var initializer = new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance);

        // 1) Migration anwenden
        Console.WriteLine("[1] Initialisiere DB…");
        var init = await initializer.InitializeAsync().ConfigureAwait(false);
        Console.WriteLine($"    DB: {init.DatabasePath}");
        Console.WriteLine($"    Neu migriert: {init.NewlyMigrated}, Stale-Recovered: {init.StaleThumbnailsRecovered}");
        Console.WriteLine();

        // 2) Library anlegen / finden (optional erst leeren)
        if (clear)
        {
            Console.WriteLine("[2a] DB leeren…");
            ClearAllLibraryData(factory);
            Console.WriteLine("     OK.");
        }

        Console.WriteLine("[2] Library anlegen oder finden…");
        int libraryId;
        await using (var ctx = factory.Create())
        {
            var existing = await ctx.Libraries
                .FirstOrDefaultAsync(l => l.RootPath == rootPath)
                .ConfigureAwait(false);
            if (existing is null)
            {
                var lib = new Library
                {
                    Name = libraryName,
                    RootPath = rootPath,
                    CreatedAt = DateTime.UtcNow,
                };
                ctx.Libraries.Add(lib);
                await ctx.SaveChangesAsync().ConfigureAwait(false);
                libraryId = lib.Id;
                Console.WriteLine($"    Library angelegt: Id={libraryId}");
            }
            else
            {
                libraryId = existing.Id;
                Console.WriteLine($"    Library existiert: Id={libraryId} Name='{existing.Name}'");
            }
        }
        Console.WriteLine();

        // 3) Scan
        Console.WriteLine("[3] Scan starten…");
        var enumerator = new FolderEnumerator(new MediaFormatRegistry(), NullLogger<FolderEnumerator>.Instance);
        var writer = new BulkScanWriter();
        var ftsIndexer = new FtsIndexer();
        var service = new ScanService(factory, enumerator, writer, ftsIndexer, NullLogger<ScanService>.Instance);

        var progress = new Progress<ScanProgress>(p =>
        {
            var sample = p.CurrentPath is null ? "(done)" : Truncate(p.CurrentPath, 60);
            Console.WriteLine($"    {p.FoldersDiscovered,6:N0} folders · {p.FilesDiscovered,8:N0} files · {p.TotalBytesDiscovered / 1024.0 / 1024.0,8:N1} MB · {sample}");
        });

        ScanResult result;
        try
        {
            result = await service.ScanAsync(libraryId, ScanMode.Fresh, progress, CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("nicht leer"))
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("ScanMode.Fresh: Library hat schon Daten. Erneut mit --clear ausführen.");
            return 3;
        }

        Console.WriteLine();
        Console.WriteLine("=== Scan-Ergebnis ===");
        Console.WriteLine($"  Folders inserted:  {result.FoldersInserted:N0}");
        Console.WriteLine($"  Files inserted:    {result.FilesInserted:N0}");
        Console.WriteLine($"  FTS rows:          {result.FtsRowsInserted:N0}");
        Console.WriteLine($"  Total bytes:       {result.TotalBytes / 1024.0 / 1024.0 / 1024.0:N2} GB");
        Console.WriteLine($"  Enumeration:       {result.EnumerationDuration.TotalSeconds,7:N2} s");
        Console.WriteLine($"  Persistence:       {result.PersistenceDuration.TotalSeconds,7:N2} s");
        Console.WriteLine($"  FTS-Indexing:      {result.FtsDuration.TotalSeconds,7:N2} s");
        Console.WriteLine($"  Total:             {result.TotalDuration.TotalSeconds,7:N2} s");
        if (result.TotalDuration.TotalSeconds > 0)
        {
            Console.WriteLine($"  Throughput:        {result.FilesInserted / result.TotalDuration.TotalSeconds,7:N0} files/s");
        }

        // 4) Smoke-Queries
        Console.WriteLine();
        Console.WriteLine("[4] Smoke-Queries…");
        await using (var ctx2 = factory.Create())
        {
            var top5Folders = await ctx2.Folders
                .Where(f => f.LibraryId == libraryId)
                .OrderBy(f => f.Path)
                .Take(5)
                .ToListAsync().ConfigureAwait(false);
            Console.WriteLine("    Erste 5 Folders:");
            foreach (var f in top5Folders)
            {
                Console.WriteLine($"      #{f.Id,-5} {Truncate(f.Path, 70)}");
            }

            var newestFiles = await ctx2.Files
                .Where(f => f.LibraryId == libraryId)
                .OrderByDescending(f => f.ModifiedAtUtc)
                .Take(5)
                .ToListAsync().ConfigureAwait(false);
            Console.WriteLine("    5 jüngste Files (nach ModifiedAtUtc):");
            foreach (var f in newestFiles)
            {
                Console.WriteLine($"      #{f.Id,-6} {Truncate(f.Name, 50),-50} {f.Size / 1024.0,9:N1} KB  {f.ModifiedAtUtc:yyyy-MM-dd}");
            }

            // FTS-Smoke
            var ftsHitCount = await ctx2.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM FilesFts WHERE FilesFts MATCH 'IMG OR DSC'").FirstAsync().ConfigureAwait(false);
            Console.WriteLine($"    FTS MATCH 'IMG OR DSC': {ftsHitCount:N0} Treffer");
        }

        Console.WriteLine();
        Console.WriteLine("Spike V01 abgeschlossen.");
        return 0;
    }

    private static void ClearAllLibraryData(LibraryContextFactory factory)
    {
        using var conn = factory.OpenSqliteConnection();
        using var cmd = conn.CreateCommand();
        // FTS5-spezifisch: 'delete-all' Pragma
        cmd.CommandText = """
            DELETE FROM Files;
            DELETE FROM Folders;
            DELETE FROM Libraries;
            INSERT INTO FilesFts(FilesFts) VALUES('delete-all');
            """;
        cmd.ExecuteNonQuery();
    }

    private static string? ParseArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : "…" + s[^(max - 1)..];
}
