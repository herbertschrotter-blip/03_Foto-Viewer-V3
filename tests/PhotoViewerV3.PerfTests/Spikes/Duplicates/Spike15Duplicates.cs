using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Duplicates;
using PhotoViewerV3.Services.Infrastructure;
using PhotoViewerV3.Services.Media;
using PhotoViewerV3.Services.Scanning;

namespace PhotoViewerV3.PerfTests.Spikes.Duplicates;

/// <summary>
/// Spike 15 — Doppelte (ADR-0053). An einem echten Foto-Ordner (nur lesen; Ausgabe nur Zahlen, nie Datei- oder
/// Ordnernamen):
/// <list type="number">
/// <item>Vorfilter Größe: wie viele Dateien teilen ihre Größe mit einer anderen (nur sie werden gehasht);</item>
/// <item>SHA-256-Durchsatz kalt/warm;</item>
/// <item>pHash: Laden (Original bzw. simuliertes 400-px-Vorschaubild) und Rechnen je Bild; Abstand zu verkleinerten/neu
/// gespeicherten, beschnittenen und aufgehellten Kopien; Abstände fremder Fotos (Zufallspaare) und aufeinanderfolgender
/// Fotos (Serien) — Grundlage der Standard-Schwelle;</item>
/// <item>Gruppieren 100 000 / 500 000 synthetische Fingerabdrücke (Bucket-Verfahren);</item>
/// <item>SQL auf einer Wegwerf-DB mit 500 000 Dateien: Größen-Vorfilter, Gruppen exakt/ähnlich, Eingang-Zähler,
/// Galerie-Filter „nur Doppelte“.</item>
/// </list>
/// </summary>
internal static class Spike15Duplicates
{
    public static async Task<int> RunAsync(string[] args)
    {
        var root = Arg(args, "--root");
        var limit = int.TryParse(Arg(args, "--limit"), out var l) ? l : 300;
        var hashLimit = int.TryParse(Arg(args, "--hash-limit"), out var h) ? h : 1500;
        var rows = int.TryParse(Arg(args, "--rows"), out var r) ? r : 500_000;
        var work = Arg(args, "--work") ?? Path.Combine(Path.GetTempPath(), "pv3-spike15-" + Guid.NewGuid().ToString("N"));
        if (Arg(args, "--explain") is { } explainDb)
        {
            Explain(explainDb);
            return 0;
        }

        Directory.CreateDirectory(work);
        try
        {
            Console.WriteLine("Spike 15 — Doppelte");
            Console.WriteLine($"  Prozessoren: {Environment.ProcessorCount}");
            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            {
                RealPhotos(root, limit, hashLimit);
            }

            Grouping();
            await SqlAsync(work, rows).ConfigureAwait(false);
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

    // ---- (1)–(3) echte Fotos ---------------------------------------------------------------------------------------

    private static void RealPhotos(string root, int limit, int hashLimit)
    {
        var enumerator = new FolderEnumerator(new MediaFormatRegistry(), NullLogger<FolderEnumerator>.Instance);
        var files = enumerator.Enumerate(root, CancellationToken.None)
            .Select(f => (Path: Path.Combine(f.FolderPath, f.Name), f.MediaType, f.Size))
            .Where(f => f.MediaType != MediaType.Archive && PhotoViewerV3.Services.Thumbnails.ThumbnailService.IsLocallyAvailable(f.Path))
            .ToList();
        var bySize = files.GroupBy(f => f.Size).ToList();
        var colliding = bySize.Where(g => g.Count() > 1).SelectMany(g => g).ToList();
        Console.WriteLine();
        Console.WriteLine($"[1] Vorfilter Größe: {files.Count:N0} Dateien, {bySize.Count:N0} verschiedene Größen, {colliding.Count:N0} teilen ihre Größe ({100.0 * colliding.Count / Math.Max(1, files.Count):N2} %) — nur sie werden gehasht");

        // (2) SHA-256: die Größen-Kollisionen, aufgefüllt mit einer Stichprobe, damit die Messung tragfähig ist
        var sample = colliding.Concat(files.OrderBy(f => f.Path.GetHashCode(StringComparison.Ordinal))).Distinct().Take(hashLimit).ToList();
        var bytes = sample.Sum(f => f.Size);
        var sw = Stopwatch.StartNew();
        var hashes = sample.Select(f => ContentHasher.ToHex(ContentHasher.Compute(f.Path))).ToList();
        var cold = sw.Elapsed;
        sw.Restart();
        foreach (var f in sample)
        {
            ContentHasher.Compute(f.Path);
        }

        var warm = sw.Elapsed;
        var exactGroups = hashes.GroupBy(x => x).Count(g => g.Count() > 1);
        Console.WriteLine($"[2] SHA-256: {sample.Count:N0} Dateien, {bytes / 1048576.0:N0} MB — erster Lauf {cold.TotalSeconds:N1} s ({bytes / 1048576.0 / cold.TotalSeconds:N0} MB/s, {cold.TotalMilliseconds / sample.Count:N1} ms/Datei), "
                          + $"zweiter Lauf {warm.TotalSeconds:N1} s ({bytes / 1048576.0 / warm.TotalSeconds:N0} MB/s); {exactGroups:N0} Gruppen byte-gleich in der Stichprobe");
        var collisionShare = colliding.Count / (double)Math.Max(1, files.Count);
        var mbPerFile = files.Count == 0 ? 0 : files.Sum(f => f.Size) / 1048576.0 / files.Count;
        Console.WriteLine($"    Hochrechnung 500 000 Dateien (Anteil {collisionShare:P1}, Ø {mbPerFile:N1} MB): {500_000 * collisionShare * mbPerFile / (bytes / 1048576.0 / cold.TotalSeconds) / 60:N0} min Lesen beim ersten Mal");

        // (3) pHash an Bildern
        var images = files.Where(f => f.MediaType == MediaType.Image && f.Path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
        var loadOriginal = new List<double>();
        var loadThumb = new List<double>();
        var compute = new List<double>();
        var fingerprints = new List<ImageFingerprint>();
        var resized = new List<int>();
        var cropped = new List<int>();
        var brighter = new List<int>();
        foreach (var image in images)
        {
            sw.Restart();
            var original = Decode(File.ReadAllBytes(image.Path), PerceptualHash.LoadEdge);
            loadOriginal.Add(sw.Elapsed.TotalMilliseconds);
            if (original is null)
            {
                continue;
            }

            var thumbBytes = EncodeJpeg(DecodeSource(File.ReadAllBytes(image.Path), 400), 82);
            sw.Restart();
            var thumb = Decode(thumbBytes, PerceptualHash.LoadEdge)!;
            loadThumb.Add(sw.Elapsed.TotalMilliseconds);
            sw.Restart();
            var fp = Fingerprint(thumb);
            compute.Add(sw.Elapsed.TotalMilliseconds);
            fingerprints.Add(fp);

            var small = Decode(EncodeJpeg(DecodeSource(File.ReadAllBytes(image.Path), 640), 70), PerceptualHash.LoadEdge)!;
            resized.Add(PerceptualHash.Distance(fp.Hash, Fingerprint(small).Hash));
            cropped.Add(PerceptualHash.Distance(fp.Hash, Fingerprint(Crop(thumb, 0.03)).Hash));
            brighter.Add(PerceptualHash.Distance(fp.Hash, Fingerprint(Brighten(thumb, 15)).Hash));
        }

        Console.WriteLine($"[3] pHash an {fingerprints.Count:N0} JPEGs ({fingerprints.Count(f => f.LowDetail):N0} fast einfarbig)");
        Console.WriteLine($"    Laden Original (längste Kante {PerceptualHash.LoadEdge}): Median {Pct(loadOriginal, 0.5):N1} ms, p95 {Pct(loadOriginal, 0.95):N1} ms");
        Console.WriteLine($"    Laden Vorschaubild (400 px): Median {Pct(loadThumb, 0.5):N2} ms, p95 {Pct(loadThumb, 0.95):N2} ms; Rechnen Median {Pct(compute, 0.5):N3} ms");
        Console.WriteLine($"    verkleinert (640) + neu gespeichert (Q70): {Dist(resized)}");
        Console.WriteLine($"    3 % je Seite beschnitten:                 {Dist(cropped)}");
        Console.WriteLine($"    heller (+15):                             {Dist(brighter)}");

        var valid = fingerprints.Where(f => !f.LowDetail).Select(f => f.Hash).ToList();
        var random = new Random(15);
        var randomPairs = new List<int>();
        for (var i = 0; i < 20_000 && valid.Count > 1; i++)
        {
            var a = random.Next(valid.Count);
            var b = random.Next(valid.Count);
            if (a != b)
            {
                randomPairs.Add(PerceptualHash.Distance(valid[a], valid[b]));
            }
        }

        var neighbours = valid.Zip(valid.Skip(1), (a, b) => PerceptualHash.Distance(a, b)).ToList();
        Console.WriteLine($"    Zufallspaare fremder Fotos ({randomPairs.Count:N0}): {Dist(randomPairs)}; Anteil ≤ 6/8/10/12: {Share(randomPairs, 6)} / {Share(randomPairs, 8)} / {Share(randomPairs, 10)} / {Share(randomPairs, 12)}");
        Console.WriteLine($"    Nachbarn in Aufnahme-Reihenfolge ({neighbours.Count:N0}): Anteil ≤ 6/8/10/12: {Share(neighbours, 6)} / {Share(neighbours, 8)} / {Share(neighbours, 10)} / {Share(neighbours, 12)} (Serien)");
        foreach (var threshold in new[] { 6, 8, 10, 12 })
        {
            var groups = SimilarGrouping.Group(valid, threshold);
            Console.WriteLine($"    Gruppen bei r = {threshold,2}: {groups.Count:N0} Gruppen mit {groups.Sum(g => g.Length):N0} Fotos ({100.0 * groups.Sum(g => g.Length) / Math.Max(1, valid.Count):N1} %)");
        }
    }

    private static ImageFingerprint Fingerprint(BitmapSource image)
    {
        var rgb = image.Format == PixelFormats.Rgb24 ? image : new FormatConvertedBitmap(image, PixelFormats.Rgb24, null, 0);
        var pixels = new byte[rgb.PixelWidth * rgb.PixelHeight * 3];
        rgb.CopyPixels(pixels, rgb.PixelWidth * 3, 0);
        return PerceptualHash.Compute(pixels, rgb.PixelWidth, rgb.PixelHeight);
    }

    private static BitmapSource? Decode(byte[] bytes, int maxEdge)
    {
        try
        {
            return DecodeSource(bytes, maxEdge);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or IOException)
        {
            return null;
        }
    }

    /// <summary>Wie der WIC-Lader: längere Seite auf <paramref name="maxEdge"/> (JPEG dekodiert dabei verkleinert).</summary>
    private static BitmapSource DecodeSource(byte[] bytes, int maxEdge)
    {
        var header = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.StreamSource = new MemoryStream(bytes);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (header.PixelWidth >= header.PixelHeight)
        {
            bitmap.DecodePixelWidth = Math.Min(maxEdge, header.PixelWidth);
        }
        else
        {
            bitmap.DecodePixelHeight = Math.Min(maxEdge, header.PixelHeight);
        }

        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] EncodeJpeg(BitmapSource source, int quality)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapSource Crop(BitmapSource source, double fraction)
    {
        var x = (int)(source.PixelWidth * fraction);
        var y = (int)(source.PixelHeight * fraction);
        var cropped = new CroppedBitmap(source, new System.Windows.Int32Rect(x, y, source.PixelWidth - (2 * x), source.PixelHeight - (2 * y)));
        cropped.Freeze();
        return cropped;
    }

    private static BitmapSource Brighten(BitmapSource source, int delta)
    {
        var rgb = new FormatConvertedBitmap(source, PixelFormats.Rgb24, null, 0);
        var stride = rgb.PixelWidth * 3;
        var pixels = new byte[stride * rgb.PixelHeight];
        rgb.CopyPixels(pixels, stride, 0);
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)Math.Clamp(pixels[i] + delta, 0, 255);
        }

        return BitmapSource.Create(rgb.PixelWidth, rgb.PixelHeight, 96, 96, PixelFormats.Rgb24, null, pixels, stride);
    }

    // ---- (4) Gruppieren --------------------------------------------------------------------------------------------

    private static void Grouping()
    {
        Console.WriteLine();
        foreach (var count in new[] { 100_000, 500_000 })
        {
            var hashes = Clustered(count, 15);
            foreach (var r in new[] { 6, 8, 10, 12 })
            {
                var sw = Stopwatch.StartNew();
                var groups = SimilarGrouping.Group(hashes, r);
                Console.WriteLine($"[4] Gruppieren {count:N0} Fingerabdrücke, r = {r,2}: {sw.ElapsedMilliseconds:N0} ms, {groups.Count:N0} Gruppen");
            }
        }
    }

    /// <summary>Zufällige Fingerabdrücke, 10 % davon Mittelpunkt einer Gruppe aus 1–5 Kopien mit 0–6 gekippten Bits.</summary>
    private static List<ulong> Clustered(int count, int seed)
    {
        var random = new Random(seed);
        var list = new List<ulong>(count);
        while (list.Count < count)
        {
            var center = (ulong)random.NextInt64() ^ ((ulong)random.Next() << 32);
            list.Add(center);
            if (random.NextDouble() < 0.1)
            {
                var members = random.Next(1, 6);
                for (var m = 0; m < members && list.Count < count; m++)
                {
                    var h = center;
                    for (var f = random.Next(0, 7); f > 0; f--)
                    {
                        h ^= 1UL << random.Next(64);
                    }

                    list.Add(h);
                }
            }
        }

        return list;
    }

    // ---- (5) SQL auf 500 000 Dateien ------------------------------------------------------------------------------------

    private static async Task SqlAsync(string work, int rows)
    {
        var paths = new AppDataPaths(work);
        paths.EnsureDirectoriesExist();
        var factory = new LibraryContextFactory(paths, new SqlitePragmaInterceptor());
        await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync().ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        using (var connection = factory.OpenSqliteConnection())
        using (var tx = connection.BeginTransaction())
        {
            Exec(connection, tx, """
                INSERT INTO Libraries(Name, RootPath, CreatedAt, LastOpenedAt, GroupId) VALUES ('Spike', 'C:\spike', 0, 0, 0);
                INSERT INTO LibraryRoots(LibraryId, Path, Role, Name, SortOrder) VALUES (1, 'C:\spike', 1, 'spike', 0), (1, 'C:\eingang', 0, 'eingang', 1);
                """);
            for (var d = 1; d <= 1000; d++)
            {
                Exec(connection, tx, $"INSERT INTO Folders(LibraryId, RootId, Path, Name, ScannedAt) VALUES (1, {(d <= 900 ? 1 : 2)}, 'C:\\spike\\{d}', '{d}', 0);");
            }

            var random = new Random(5);
            using var file = connection.CreateCommand();
            file.Transaction = tx;
            file.CommandText = "INSERT INTO Files(LibraryId, FolderId, Name, Extension, Size, CreatedAtUtc, ModifiedAtUtc, MediaType, IsFavorite, Missing) VALUES (1, $f, $n, 'jpg', $s, 0, 1000, 0, 0, 0)";
            var pf = file.Parameters.Add("$f", SqliteType.Integer);
            var pn = file.Parameters.Add("$n", SqliteType.Text);
            var ps = file.Parameters.Add("$s", SqliteType.Integer);
            using var fp = connection.CreateCommand();
            fp.Transaction = tx;
            fp.CommandText = "INSERT INTO FileFingerprints(FileId, ContentHash, ContentSize, ContentModifiedAt, ImageHash, ImageHashVersion, ImageModifiedAt, ImageLowDetail) VALUES (last_insert_rowid(), $h, $s, 1000, $i, $v, 1000, 0)";
            var ph = fp.Parameters.Add("$h", SqliteType.Blob);
            var pi = fp.Parameters.Add("$i", SqliteType.Integer);
            fp.Parameters.Add("$s", SqliteType.Integer);
            fp.Parameters.AddWithValue("$v", PerceptualHash.Version);
            var images = Clustered(rows, 7);
            var sizes = new long[rows];
            var hashed = new bool[rows];
            for (var i = 0; i < rows; i++)
            {
                // 2 % byte-gleiche Kopien einer früheren Datei, sonst (fast) einmalige Größe; gehasht werden wie in der App
                // nur gleich große Dateien — dazu 11 % weitere (Größen-Kollisionen ohne gleichen Inhalt) → ≈ 13 % wie im
                // Handy-Katalog
                var copyOf = i > 10 && random.NextDouble() < 0.02 ? random.Next(i) : -1;
                sizes[i] = copyOf >= 0 ? sizes[copyOf] : 1_000_000 + ((long)random.Next() * 7);
                hashed[i] = copyOf >= 0 || random.NextDouble() < 0.11;
                if (copyOf >= 0)
                {
                    hashed[copyOf] = true;
                }
            }

            for (var i = 0; i < rows; i++)
            {
                pf.Value = 1 + random.Next(1000);
                pn.Value = $"f{i}.jpg";
                ps.Value = sizes[i];
                file.ExecuteNonQuery();
                var content = new byte[32];
                BitConverter.TryWriteBytes(content, sizes[i]);
                ph.Value = hashed[i] ? content : DBNull.Value;
                pi.Value = PerceptualHash.ToStored(images[i]);
                fp.Parameters["$s"].Value = sizes[i];
                fp.ExecuteNonQuery();
            }

            tx.Commit();
        }

        Console.WriteLine();
        Console.WriteLine($"[5] Wegwerf-DB mit {rows:N0} Dateien angelegt in {sw.Elapsed.TotalSeconds:N1} s");
        Time("Größen-Vorfilter (GROUP BY Size HAVING COUNT > 1)", () => Scalar(factory, "SELECT COUNT(*) FROM (SELECT Size FROM Files WHERE Missing = 0 AND Size > 0 GROUP BY Size HAVING COUNT(*) > 1)"));
        var service = new DuplicateService(factory, NullLogger<DuplicateService>.Instance);
        await TimeAsync("Gruppen exakt gleich (inkl. Mitglieder und offene Zähler)", async () => (await service.GetGroupsAsync(new DuplicateQuery([1], DuplicateMode.Exact)).ConfigureAwait(false)).Groups.Count).ConfigureAwait(false);
        await TimeAsync("Gruppen ähnlich r = 10", async () => (await service.GetGroupsAsync(new DuplicateQuery([1], DuplicateMode.Similar, 10)).ConfigureAwait(false)).Groups.Count).ConfigureAwait(false);
        await TimeAsync("Eingang schon in der Bibliothek (Zähler)", () => service.CountInboxAlreadyInLibraryAsync([1])).ConfigureAwait(false);
        Time("Galerie-Filter „nur Doppelte“ (Zählen)", () => Scalar(factory, "SELECT COUNT(*) FROM Files f WHERE f.LibraryId = 1 AND f.Missing = 0 AND " + DuplicateSql.HasExactDuplicate("f", [1])));
        Time("Galerie-Filter „nur Doppelte“ (Seite 1, 200 nach Änderungsdatum)", () => Scalar(factory, "SELECT COUNT(*) FROM (SELECT f.Id FROM Files f WHERE f.LibraryId = 1 AND f.Missing = 0 AND " + DuplicateSql.HasExactDuplicate("f", [1]) + " ORDER BY f.ModifiedAtUtc DESC, f.Id DESC LIMIT 200)"));
    }

    /// <summary>Abfragepläne der Doppelten-Abfragen gegen eine vorhandene DB (Diagnose).</summary>
    private static void Explain(string db)
    {
        using var connection = new SqliteConnection($"Data Source={db};Mode=ReadOnly");
        connection.Open();
       foreach (var (name, sql) in new[] { ("Galerie", "SELECT COUNT(*) FROM Files f WHERE f.LibraryId = 1 AND f.Missing = 0 AND " + DuplicateSql.HasExactDuplicate("f", [1])) })
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
            using var reader = cmd.ExecuteReader();
            Console.WriteLine(name + ":");
            while (reader.Read())
            {
                Console.WriteLine("  " + reader.GetString(3));
            }
        }
    }

    private static long Scalar(LibraryContextFactory factory, string sql)
    {
        using var connection = factory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Exec(SqliteConnection connection, SqliteTransaction tx, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void Time(string what, Func<long> action)
    {
        var times = new List<double>();
        long result = 0;
        for (var i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew();
            result = action();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }

        Console.WriteLine($"    {what}: Median {Pct(times, 0.5):N0} ms → {result:N0}");
    }

    private static async Task TimeAsync(string what, Func<Task<int>> action)
    {
        var times = new List<double>();
        var result = 0;
        for (var i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew();
            result = await action().ConfigureAwait(false);
            times.Add(sw.Elapsed.TotalMilliseconds);
        }

        Console.WriteLine($"    {what}: Median {Pct(times, 0.5):N0} ms → {result:N0}");
    }

    private static string Dist(IReadOnlyList<int> values)
        => values.Count == 0 ? "—" : $"Median {Pct(values.Select(v => (double)v).ToList(), 0.5):N0}, p95 {Pct(values.Select(v => (double)v).ToList(), 0.95):N0}, max {values.Max()}, min {values.Min()}";

    private static string Share(IReadOnlyList<int> values, int threshold)
        => values.Count == 0 ? "—" : $"{100.0 * values.Count(v => v <= threshold) / values.Count:N2} %";

    private static double Pct(List<double> values, double p)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(v => v).ToList();
        return sorted[Math.Min(sorted.Count - 1, (int)(p * sorted.Count))];
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
