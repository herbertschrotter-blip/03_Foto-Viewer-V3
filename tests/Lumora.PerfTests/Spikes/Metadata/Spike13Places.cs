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
using Lumora.Data.RawSql;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Database;
using Lumora.Services.Infrastructure;
using Lumora.Services.Library;
using Lumora.Services.Media;
using Lumora.Services.Metadata;
using Lumora.Services.Places;
using Lumora.Services.Scanning;

namespace Lumora.PerfTests.Spikes.Metadata;

/// <summary>
/// Spike 13 — Orte (ADR-0050). Misst an einem echten Foto-Ordner (nur lesen; Ausgabe nur Zahlen, nie Datei-, Ordner- oder
/// Ortsnamen):
/// <list type="number">
/// <item>Zusatzkosten je Datei: GPS-Leser (inkl. Prüfung auf XMP-Begleitdatei) kalt/warm neben dem Aufnahmedatum-Leser,
/// Übereinstimmung mit der WIC-Metadatenabfrage;</item>
/// <item>Ortsnamen: Laden der Ressource, <see cref="ReverseGeocoder.Nearest"/> (Zufallspunkte und die echten Koordinaten),
/// Suche;</item>
/// <item>Nachtragen end-to-end auf einer Wegwerf-DB;</item>
/// <item>Schreiben: verlustfreies EXIF-Schreiben in synthetische JPEGs (4 MB) — Temp-Datei + Ersetzen, Rückgängig.</item>
/// </list>
/// </summary>
internal static class Spike13Places
{
    public static async Task<int> RunAsync(string[] args)
    {
        var root = Arg(args, "--root");
        var limit = int.TryParse(Arg(args, "--limit"), out var l) ? l : 2000;
        var work = Arg(args, "--work") ?? Path.Combine(Path.GetTempPath(), "lumora-spike13-" + Guid.NewGuid().ToString("N"));
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            Console.Error.WriteLine("Usage: spike13-places --root PATH [--limit 2000] [--work PATH]");
            return 1;
        }

        Directory.CreateDirectory(work);
        try
        {
            Console.WriteLine("Spike 13 — Orte");
            Console.WriteLine($"  Prozessoren: {Environment.ProcessorCount}, Stichprobe Leser: {limit}");
            Console.WriteLine();

            // (1) Leser je Datei
            var enumerator = new FolderEnumerator(new MediaFormatRegistry(), NullLogger<FolderEnumerator>.Instance);
            var files = enumerator.Enumerate(root, CancellationToken.None)
                .Select(f => (Path: Path.Combine(f.FolderPath, f.Name), f.MediaType))
                .Where(f => f.MediaType != MediaType.Archive && Lumora.Services.Thumbnails.ThumbnailService.IsLocallyAvailable(f.Path))
                .OrderBy(f => f.Path.GetHashCode(StringComparison.Ordinal))
                .Take(limit)
                .ToList();
            var gps = new GeoLocationReader();
            var dates = new CaptureDateReader();
            var coldGps = Measure(files, p => gps.Read(p), out var readings);
            var warmDate = Measure(files, p => dates.Read(p), out _);
            var warmGps = Measure(files, p => gps.Read(p), out _);
            var warmGpsNoSidecar = Measure(files, p => MediaGpsReader.Read(p), out _);
            var withGps = readings.Count(r => r is not null);
            Console.WriteLine($"[1] GPS-Leser, {files.Count:N0} Dateien ({files.Count(f => f.MediaType == MediaType.Video):N0} Videos), {withGps:N0} mit Koordinaten ({readings.Count(r => r?.Source == GeoSource.Video):N0} aus Videos)");
            Console.WriteLine($"    GPS kalt (zuerst gelesen): Median {Percentile(coldGps, 0.5):N3} ms, p95 {Percentile(coldGps, 0.95):N3} ms, Summe {coldGps.Sum() / 1000:N1} s");
            Console.WriteLine($"    Aufnahmedatum warm:        Median {Percentile(warmDate, 0.5):N3} ms, p95 {Percentile(warmDate, 0.95):N3} ms");
            Console.WriteLine($"    GPS warm inkl. XMP-Prüfung: Median {Percentile(warmGps, 0.5):N3} ms, p95 {Percentile(warmGps, 0.95):N3} ms");
            Console.WriteLine($"    GPS warm ohne XMP-Prüfung: Median {Percentile(warmGpsNoSidecar, 0.5):N3} ms");

            var images = files.Where(f => f.MediaType == MediaType.Image).ToList();
            var (agree, wicOnly, managedOnly, compared) = CompareWic(images);
            Console.WriteLine($"    Übereinstimmung mit WIC (nur Bilder, {compared:N0}): {agree:N0} gleich (±1e-6°), nur WIC {wicOnly:N0}, nur verwaltet {managedOnly:N0}");
            Console.WriteLine();

            // (2) Ortsnamen
            var sw = Stopwatch.StartNew();
            var geocoder = new ReverseGeocoder();
            var count = geocoder.Count;
            sw.Stop();
            Console.WriteLine($"[2] Ortsnamen: {count:N0} Orte geladen in {sw.Elapsed.TotalMilliseconds:N0} ms, Arbeitsspeicher ≈ {GC.GetTotalMemory(true) / 1024 / 1024:N0} MB gesamt");
            var random = new Random(4711);
            var points = Enumerable.Range(0, 100_000).Select(_ => ((random.NextDouble() * 120) - 55, (random.NextDouble() * 360) - 180)).ToList();
            sw.Restart();
            var found = points.Count(p => geocoder.Nearest(p.Item1, p.Item2) is not null);
            sw.Stop();
            Console.WriteLine($"    Nearest, 100 000 Zufallspunkte (auch Meer): {sw.Elapsed.TotalMilliseconds * 1000 / points.Count:N2} µs je Aufruf, {found:N0} mit Ort");
            var real = readings.Where(r => r is not null).Select(r => r!.Value.Coordinate).ToList();
            if (real.Count > 0)
            {
                sw.Restart();
                for (var rep = 0; rep < 20; rep++)
                {
                    foreach (var c in real)
                    {
                        _ = geocoder.Nearest(c.Latitude, c.Longitude);
                    }
                }

                sw.Stop();
                var distinct = real.Select(c => geocoder.Nearest(c.Latitude, c.Longitude)?.PlaceId).Where(id => id is not null).Distinct().Count();
                Console.WriteLine($"    Nearest, echte Koordinaten ({real.Count:N0} × 20): {sw.Elapsed.TotalMilliseconds * 1000 / (real.Count * 20):N2} µs je Aufruf, {distinct:N0} verschiedene Orte");
            }

            sw.Restart();
            _ = geocoder.Search("a", 10); // Suchschlüssel aufbauen
            var firstSearch = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            foreach (var text in new[] { "Wien", "Paris, Frankreich", "San", "Muenchen", "gr" })
            {
                _ = geocoder.Search(text, 12);
            }

            Console.WriteLine($"    Suche: erste {firstSearch:N0} ms (Schlüssel aufbauen), danach {sw.Elapsed.TotalMilliseconds / 5:N1} ms je Suche");
            Console.WriteLine();

            // (3) Nachtragen end-to-end
            var dbDir = Path.Combine(work, "backfill");
            var (scanSeconds, scan, factory) = await ScanOnceAsync(root, dbDir);
            using (var service = new GeoLocationService(factory, gps, geocoder, NullLogger<GeoLocationService>.Instance, messenger: new WeakReferenceMessenger()) { StartDelay = Timeout.InfiniteTimeSpan })
            {
                sw.Restart();
                var report = await service.RunBackfillPassAsync();
                sw.Stop();
                var perSecond = report.Read / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                Console.WriteLine($"[3] Nachtragen: {report.Read:N0} gelesen ({report.WithLocation:N0} mit Koordinaten), {report.Skipped:N0} übersprungen in {sw.Elapsed.TotalSeconds:N1} s = {perSecond:N0} Dateien/s");
                Console.WriteLine($"    Scan selbst: {scanSeconds:N2} s für {scan.FilesInserted:N0} Dateien; hochgerechnet 100 000 Dateien ≈ {100_000 / Math.Max(1, perSecond) / 60:N1} min");
            }

            SqliteConnection.ClearAllPools();
            Console.WriteLine();

            // (4) Schreiben (synthetisch)
            var writeDir = Path.Combine(work, "write");
            Directory.CreateDirectory(writeDir);
            var jpegs = Enumerable.Range(0, 50).Select(i =>
            {
                var path = Path.Combine(writeDir, $"s{i}.jpg");
                File.WriteAllBytes(path, SyntheticJpeg(4 * 1024 * 1024, i));
                return path;
            }).ToList();
            var writeMs = new List<double>();
            var restoreMs = new List<double>();
            var identical = 0;
            foreach (var path in jpegs)
            {
                var before = File.ReadAllBytes(path);
                var t = Stopwatch.StartNew();
                var write = GeoFileWriter.Write(path, 0, new GeoCoordinate(47.0707, 15.4395), keepModifiedTime: true);
                writeMs.Add(t.Elapsed.TotalMilliseconds);
                t.Restart();
                _ = GeoFileWriter.Restore(write);
                restoreMs.Add(t.Elapsed.TotalMilliseconds);
                identical += File.ReadAllBytes(path).AsSpan().SequenceEqual(before) ? 1 : 0;
            }

            Console.WriteLine($"[4] Schreiben (50 × 4 MB JPEG, Temp-Datei + Ersetzen): Median {Percentile(writeMs, 0.5):N1} ms, p95 {Percentile(writeMs, 0.95):N1} ms");
            Console.WriteLine($"    Rückgängig: Median {Percentile(restoreMs, 0.5):N1} ms, Byte-gleich {identical} / {jpegs.Count}");
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

    private static byte[] SyntheticJpeg(int size, int seed)
    {
        var random = new Random(seed);
        var data = new byte[size];
        random.NextBytes(data);
        using var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10]);
        ms.Write("JFIF\0"u8);
        ms.Write(new byte[9]);
        var exif = new byte[] { (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 1, 0, 0x0F, 0x01, 2, 0, 6, 0, 0, 0, 26, 0, 0, 0, 0, 0, 0, 0 }.Concat("Spike\0"u8.ToArray()).ToArray();
        ms.Write([0xFF, 0xE1, (byte)((exif.Length + 8) >> 8), (byte)((exif.Length + 8) & 0xFF)]);
        ms.Write("Exif\0\0"u8);
        ms.Write(exif);
        ms.Write([0xFF, 0xDA, 0x00, 0x08, 1, 2, 3, 4, 5, 6]);
        ms.Write(data);
        ms.Write([0xFF, 0xD9]);
        return ms.ToArray();
    }

    private static List<double> Measure(IReadOnlyList<(string Path, MediaType MediaType)> files, Func<string, object?> read, out List<GeoReading?> readings)
    {
        var times = new List<double>(files.Count);
        readings = new List<GeoReading?>(files.Count);
        foreach (var file in files)
        {
            var sw = Stopwatch.StartNew();
            var value = read(file.Path);
            times.Add(sw.Elapsed.TotalMilliseconds);
            readings.Add(value as GeoReading?);
        }

        return times;
    }

    private static (int Agree, int WicOnly, int ManagedOnly, int Compared) CompareWic(IReadOnlyList<(string Path, MediaType MediaType)> images)
    {
        int agree = 0, wicOnly = 0, managedOnly = 0, compared = 0;
        foreach (var image in images)
        {
            double? wicLat = null;
            try
            {
                using var stream = new FileStream(image.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
                if (frame.Metadata is BitmapMetadata metadata)
                {
                    foreach (var prefix in new[] { "/app1/ifd/gps", "/ifd/gps" })
                    {
                        try
                        {
                            if (metadata.GetQuery(prefix + "/{ushort=2}") is ulong[] { Length: 3 } r && metadata.GetQuery(prefix + "/{ushort=1}") is string reference)
                            {
                                static double R(ulong v) => (v >> 32) == 0 ? double.NaN : (uint)(v & 0xFFFFFFFF) / (double)(uint)(v >> 32);
                                var value = R(r[0]) + (R(r[1]) / 60) + (R(r[2]) / 3600);
                                wicLat = reference.StartsWith('S') ? -value : value;
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
                continue;
            }

            compared++;
            if (wicLat is { } w && !double.IsFinite(w))
            {
                wicLat = null;
            }

            var managed = MediaGpsReader.Read(image.Path)?.Coordinate.Latitude;
            if (wicLat is not null && managed is not null)
            {
                agree += Math.Abs(wicLat.Value - managed.Value) < 1e-6 ? 1 : 0;
            }
            else if (wicLat is not null && wicLat != 0)
            {
                wicOnly++;
            }
            else if (managed is not null)
            {
                managedOnly++;
            }
        }

        return (agree, wicOnly, managedOnly, compared);
    }

    private static async Task<(double Seconds, ScanResult Result, LibraryContextFactory Factory)> ScanOnceAsync(string root, string appData)
    {
        Directory.CreateDirectory(appData);
        var paths = new AppDataPaths(appData);
        paths.EnsureDirectoriesExist();
        var factory = new LibraryContextFactory(paths, new SqlitePragmaInterceptor());
        await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        int libraryId;
        await using (var ctx = factory.Create())
        {
            var library = new Library { Name = "Spike13", RootPath = root, CreatedAt = DateTime.UtcNow };
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
        return (sw.Elapsed.TotalSeconds, result, factory);
    }

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
