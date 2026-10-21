using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Data.RawSql;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Infrastructure;
using PhotoViewerV3.Services.Library;
using PhotoViewerV3.Services.Media;
using PhotoViewerV3.Services.Metadata;
using PhotoViewerV3.Services.Scanning;

namespace PhotoViewerV3.PerfTests.Spikes.Metadata;

/// <summary>
/// Spike 12 — Aufnahmedatum (ADR-0046). Misst an einem echten Foto-Ordner (nur lesen; Ausgabe nur Zahlen, nie Datei-
/// oder Ordnernamen):
/// <list type="number">
/// <item>Fresh-Scan in eine Wegwerf-DB ohne bzw. mit dem neuen Sortier-Index (Scan alt/neu, je 3 Läufe, Median);</item>
/// <item>Lesen des Aufnahmedatums je Datei mit dem verwalteten Leser (kalt und warm) gegen die WIC-Metadatenabfrage —
/// Dauer je Datei, Herkunft, Übereinstimmung;</item>
/// <item>Nachtragen im Hintergrund end-to-end (<see cref="CaptureDateService.RunBackfillPassAsync"/>) auf der Wegwerf-DB.</item>
/// </list>
/// Die Wegwerf-DB liegt unter <c>--work</c> (Standard: %TEMP%) und wird gelöscht.
/// </summary>
internal static class Spike12CaptureDate
{
    public static async Task<int> RunAsync(string[] args)
    {
        var root = Arg(args, "--root");
        var limit = int.TryParse(Arg(args, "--limit"), out var l) ? l : 2000;
        var runs = int.TryParse(Arg(args, "--runs"), out var r) ? r : 3;
        var work = Arg(args, "--work") ?? Path.Combine(Path.GetTempPath(), "pv3-spike12-" + Guid.NewGuid().ToString("N"));
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            Console.Error.WriteLine("Usage: spike12-capture-date --root PATH [--limit 2000] [--runs 3] [--work PATH]");
            return 1;
        }

        Directory.CreateDirectory(work);
        try
        {
            Console.WriteLine("Spike 12 — Aufnahmedatum");
            Console.WriteLine($"  Prozessoren: {Environment.ProcessorCount}, Läufe: {runs}, Stichprobe Leser: {limit}");
            Console.WriteLine();

            // (1) Scan alt/neu
            var withoutIndex = new List<double>();
            var withIndex = new List<double>();
            ScanResult? last = null;
            for (var run = 0; run < runs; run++)
            {
                foreach (var dropIndex in new[] { true, false })
                {
                    var (seconds, result) = await ScanOnceAsync(root, Path.Combine(work, $"scan-{run}-{dropIndex}"), dropIndex);
                    (dropIndex ? withoutIndex : withIndex).Add(seconds);
                    last = result;
                }
            }

            Console.WriteLine($"[1] Fresh-Scan: {last!.FilesInserted:N0} Dateien, {last.FoldersInserted:N0} Ordner");
            Console.WriteLine($"    ohne Sortier-Index (alt): Median {Median(withoutIndex):N2} s  ({string.Join(" / ", withoutIndex.Select(s => s.ToString("N2")))})");
            Console.WriteLine($"    mit  Sortier-Index (neu): Median {Median(withIndex):N2} s  ({string.Join(" / ", withIndex.Select(s => s.ToString("N2")))})");
            Console.WriteLine($"    Mehraufwand: {(Median(withIndex) / Median(withoutIndex) - 1) * 100:+0.0;-0.0} %");
            Console.WriteLine();

            // (2) Leser je Datei
            var enumerator = new FolderEnumerator(new MediaFormatRegistry(), NullLogger<FolderEnumerator>.Instance);
            var files = enumerator.Enumerate(root, CancellationToken.None)
                .Select(f => (Path: Path.Combine(f.FolderPath, f.Name), f.MediaType, f.Extension))
                .Where(f => f.MediaType != MediaType.Archive && PhotoViewerV3.Services.Thumbnails.ThumbnailService.IsLocallyAvailable(f.Path))
                .OrderBy(f => f.Path.GetHashCode(StringComparison.Ordinal))
                .Take(limit)
                .ToList();
            var reader = new CaptureDateReader();
            var cold = MeasureReader(files, reader, out var sources);
            var warm = MeasureReader(files, reader, out _);
            Console.WriteLine($"[2] Verwalteter Leser, {files.Count:N0} Dateien ({files.Count(f => f.MediaType == MediaType.Video):N0} Videos)");
            Console.WriteLine($"    kalt: Median {Percentile(cold, 0.5):N3} ms, p95 {Percentile(cold, 0.95):N3} ms, Summe {cold.Sum() / 1000:N1} s");
            Console.WriteLine($"    warm: Median {Percentile(warm, 0.5):N3} ms, p95 {Percentile(warm, 0.95):N3} ms");
            foreach (var group in sources.GroupBy(s => s).OrderBy(g => g.Key))
            {
                Console.WriteLine($"    Herkunft {group.Key}: {group.Count():N0}");
            }

            var images = files.Where(f => f.MediaType == MediaType.Image).ToList();
            var (wicMs, agree, wicOnly, managedOnly, compared) = MeasureWic(images);
            Console.WriteLine($"    WIC (BitmapDecoder + Metadaten-Abfrage, nur Bilder, {compared:N0}): Median {Percentile(wicMs, 0.5):N3} ms, p95 {Percentile(wicMs, 0.95):N3} ms");
            Console.WriteLine($"    Übereinstimmung verwaltet/WIC: {agree:N0} gleich, nur WIC {wicOnly:N0}, nur verwaltet {managedOnly:N0}");
            Console.WriteLine();

            // (3) Nachtragen end-to-end auf der zuletzt gescannten DB
            var dbDir = Path.Combine(work, "backfill");
            var (_, scan) = await ScanOnceAsync(root, dbDir, dropIndex: false);
            var factory = new LibraryContextFactory(new AppDataPaths(dbDir), new SqlitePragmaInterceptor());
            using var service = new CaptureDateService(factory, reader, NullLogger<CaptureDateService>.Instance, null, new WeakReferenceMessenger()) { StartDelay = Timeout.InfiniteTimeSpan };
            var sw = Stopwatch.StartNew();
            var report = await service.RunBackfillPassAsync();
            sw.Stop();
            Console.WriteLine($"[3] Nachtragen: {report.Read:N0} gelesen ({report.WithDate:N0} mit Datum), {report.Skipped:N0} übersprungen in {sw.Elapsed.TotalSeconds:N1} s = {report.Read / Math.Max(0.001, sw.Elapsed.TotalSeconds):N0} Dateien/s");
            Console.WriteLine($"    Hochgerechnet: 100 000 Dateien ≈ {100_000 / Math.Max(1, report.Read / Math.Max(0.001, sw.Elapsed.TotalSeconds)) / 60:N1} min, 500 000 ≈ {500_000 / Math.Max(1, report.Read / Math.Max(0.001, sw.Elapsed.TotalSeconds)) / 60:N1} min");
            Console.WriteLine($"    Zum Vergleich der Scan selbst: {Median(withIndex):N2} s für {scan.FilesInserted:N0} Dateien — Lesen im Scan wäre ≈ {(sw.Elapsed.TotalSeconds / Math.Max(0.001, Median(withIndex))) * 100:N0} % zusätzlich");
            SqliteConnection.ClearAllPools();
            return 0;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<(double Seconds, ScanResult Result)> ScanOnceAsync(string root, string appData, bool dropIndex)
    {
        Directory.CreateDirectory(appData);
        var paths = new AppDataPaths(appData);
        paths.EnsureDirectoriesExist();
        var factory = new LibraryContextFactory(paths, new SqlitePragmaInterceptor());
        await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        if (dropIndex)
        {
            using var connection = factory.OpenSqliteConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = CaptureDateSortSql.DropIndexSql;
            cmd.ExecuteNonQuery();
        }

        int libraryId;
        await using (var ctx = factory.Create())
        {
            var library = new Library { Name = "Spike12", RootPath = root, CreatedAt = DateTime.UtcNow };
            ctx.Libraries.Add(library);
            await ctx.SaveChangesAsync();
            libraryId = library.Id;
            ctx.LibraryRoots.Add(new LibraryRoot { LibraryId = libraryId, Path = root, Role = LibraryRootRole.Archive, Name = "root", SortOrder = 0 });
            await ctx.SaveChangesAsync();
        }

        var scanService = new ScanService(
            factory,
            new FolderEnumerator(new MediaFormatRegistry(), NullLogger<FolderEnumerator>.Instance),
            new BulkScanWriter(),
            new FtsIndexer(),
            new LibraryService(factory, paths, NullLogger<LibraryService>.Instance),
            NullLogger<ScanService>.Instance,
            new WeakReferenceMessenger());
        var sw = Stopwatch.StartNew();
        var result = await scanService.ScanAsync(libraryId, ScanMode.Fresh, null, CancellationToken.None);
        sw.Stop();
        SqliteConnection.ClearAllPools();
        return (sw.Elapsed.TotalSeconds, result);
    }

    private static List<double> MeasureReader(IReadOnlyList<(string Path, MediaType MediaType, string Extension)> files, ICaptureDateReader reader, out List<string> sources)
    {
        var times = new List<double>(files.Count);
        sources = new List<string>(files.Count);
        foreach (var file in files)
        {
            var sw = Stopwatch.StartNew();
            var value = reader.Read(file.Path);
            times.Add(sw.Elapsed.TotalMilliseconds);
            sources.Add(value is { } v ? v.Source.ToString() : "Unknown");
        }

        return times;
    }

    private static (List<double> Ms, int Agree, int WicOnly, int ManagedOnly, int Compared) MeasureWic(IReadOnlyList<(string Path, MediaType MediaType, string Extension)> images)
    {
        var times = new List<double>(images.Count);
        int agree = 0, wicOnly = 0, managedOnly = 0, compared = 0;
        foreach (var image in images)
        {
            var sw = Stopwatch.StartNew();
            string? wicText = null;
            try
            {
                using var stream = new FileStream(image.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
                if (frame.Metadata is BitmapMetadata metadata)
                {
                    foreach (var query in new[] { "/app1/ifd/exif/{ushort=36867}", "/ifd/exif/{ushort=36867}", "/xmp/exif:DateTimeOriginal" })
                    {
                        try
                        {
                            if (metadata.GetQuery(query) is string text)
                            {
                                wicText = text;
                                break;
                            }
                        }
                        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
                        {
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or InvalidOperationException or FileFormatException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                continue; // kein WIC-Codec (z. B. HEIC ohne Erweiterung)
            }

            times.Add(sw.Elapsed.TotalMilliseconds);
            compared++;
            var wic = ExifTiffReader.ParseExifDateTime(wicText);
            var managed = MediaDateReader.Read(image.Path) is { Source: CaptureDateSource.Exif } v ? v.Local : (DateTime?)null;
            if (wic is not null && managed is not null)
            {
                if (wic == managed)
                {
                    agree++;
                }
            }
            else if (wic is not null)
            {
                wicOnly++;
            }
            else if (managed is not null)
            {
                managedOnly++;
            }
        }

        return (times, agree, wicOnly, managedOnly, compared);
    }

    private static double Median(List<double> values) => Percentile(values, 0.5);

    private static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(v => v).ToList();
        return sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(p * sorted.Count))];
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
