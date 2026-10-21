using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoViewerV3.Services.Thumbnails;

namespace PhotoViewerV3.PerfTests.Spikes.Wic;

/// <summary>
/// Spike #11 (0.2.45, ADR-0043): Vorschaubild-Größe alt (lange Kante 320 px, Qualität 80) gegen kurze Kante
/// 480/400 px (lange Kante höchstens 3×, Qualität 85/82) an echten Fotos. Liest die Quellen nur (FileShare.ReadWrite),
/// schreibt die Vorschaubilder nummeriert in einen eigenen Ordner und gibt nur zusammengefasste Zahlen aus — keine
/// Dateinamen (Datenschutz). Cloud-Platzhalter (OneDrive „nur online“) werden übersprungen, damit nichts geladen wird.
/// Zeiten: je Datei erst ein Aufwärm-Lesen (Dateicache), dann alle Varianten in wechselnder Reihenfolge, gemessen wird
/// Header-Lesen + Dekodieren + Drehen + JPEG-Schreiben wie im WicImageThumbnailRenderer (ein Thread).
/// </summary>
internal static class Spike11ThumbnailSize
{
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    private sealed record Variant(string Name, int ShortEdge, int LongEdgeCap, int Quality, bool LongEdgeMode);

    public static Task<int> RunAsync(string[] args)
    {
        string? dir = null;
        string? output = null;
        var count = 200;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dir": dir = args[++i]; break;
                case "--out": output = args[++i]; break;
                case "--count": count = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
            }
        }

        if (dir is null || output is null)
        {
            Console.Error.WriteLine("Aufruf: spike11-thumb-size --dir <Fotoordner> --out <Ausgabeordner> [--count 200]");
            return Task.FromResult(1);
        }

        Variant[] variants =
        [
            new("alt: lange Kante 320, Q80", 320, 320, 80, LongEdgeMode: true),
            new("kurz 480 (max 1440), Q85", 480, 1440, 85, LongEdgeMode: false),
            new("kurz 480 (max 1440), Q82", 480, 1440, 82, LongEdgeMode: false),
            new("kurz 480 (max 1440), Q75", 480, 1440, 75, LongEdgeMode: false),
            new("kurz 480 (max 960), Q80", 480, 960, 80, LongEdgeMode: false),
            new("kurz 440 (max 1320), Q80", 440, 1320, 80, LongEdgeMode: false),
            new("kurz 400 (max 1200), Q85", 400, 1200, 85, LongEdgeMode: false),
            new("kurz 400 (max 1200), Q82", 400, 1200, 82, LongEdgeMode: false),
        ];

        // Gleichmäßige Stichprobe über den ganzen Ordner (jede n-te Datei), nur lokal vorhandene JPEGs.
        var all = Directory.EnumerateFiles(dir, "*.jp*g", SearchOption.AllDirectories)
            .Where(p => (File.GetAttributes(p) & (RecallOnDataAccess | FileAttributes.Offline)) == 0)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var step = Math.Max(1, all.Count / count);
        var sample = all.Where((_, i) => i % step == 0).Take(count).ToList();
        Console.WriteLine($"Spike #11 — {sample.Count} von {all.Count} lokalen JPEGs, Ausgabe nach {output}");

        var bytes = variants.ToDictionary(v => v, _ => new List<long>());
        var times = variants.ToDictionary(v => v, _ => new List<double>());
        var pixels = variants.ToDictionary(v => v, _ => new List<long>());
        var portrait = 0;
        var rotated = 0;
        var errors = 0;
        long sourceBytes = 0;
        for (var n = 0; n < sample.Count; n++)
        {
            var path = sample[n];
            try
            {
                sourceBytes += File.ReadAllBytes(path).LongLength; // Aufwärmen: Datei im Cache
                var order = n % 2 == 0 ? variants : variants.Reverse().ToArray();
                foreach (var v in order)
                {
                    var target = Path.Combine(output, v.ShortEdge + "-" + v.LongEdgeCap + "-" + v.Quality + (v.LongEdgeMode ? "-long" : string.Empty), n.ToString("D4", CultureInfo.InvariantCulture) + ".jpg");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var sw = Stopwatch.StartNew();
                    var (w, h, srcW, srcH, orientation) = Render(path, target, v);
                    sw.Stop();
                    times[v].Add(sw.Elapsed.TotalMilliseconds);
                    bytes[v].Add(new FileInfo(target).Length);
                    pixels[v].Add((long)w * h);
                    if (v == variants[0])
                    {
                        portrait += srcH > srcW ? 1 : 0;
                        rotated += orientation is >= 5 and <= 8 ? 1 : 0;
                    }
                }
            }
            catch (Exception ex)
            {
                errors++;
                Console.Error.WriteLine($"Datei #{n}: {ex.GetType().Name}");
            }
        }

        Console.WriteLine($"Quellen: Ø {sourceBytes / Math.Max(1, sample.Count - errors) / 1024.0 / 1024.0:N2} MB, Hochformat {portrait}, EXIF 5–8 {rotated}, Fehler {errors}");
        Console.WriteLine();
        Console.WriteLine("| Variante | Ø KB | Median KB | p95 KB | Ø ms | Median ms | p95 ms | Ø Pixel | 100k GB | 500k GB | 100k Min (4 Worker) |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var v in variants)
        {
            var b = bytes[v].Select(x => x / 1024.0).OrderBy(x => x).ToList();
            var t = times[v].OrderBy(x => x).ToList();
            var avgKb = b.Average();
            var avgMs = t.Average();
            Console.WriteLine(string.Create(CultureInfo.GetCultureInfo("de-DE"),
                $"| {v.Name} | {avgKb:N1} | {Pct(b, 0.5):N1} | {Pct(b, 0.95):N1} | {avgMs:N1} | {Pct(t, 0.5):N1} | {Pct(t, 0.95):N1} | {pixels[v].Average():N0} | {avgKb * 100_000 / 1024 / 1024:N2} | {avgKb * 500_000 / 1024 / 1024:N2} | {avgMs * 100_000 / 4 / 60_000:N1} |"));
        }

        return Task.FromResult(errors == sample.Count ? 1 : 0);
    }

    private static double Pct(List<double> sorted, double p)
        => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Floor(p * sorted.Count))];

    /// <summary>Gleiche Schritte wie WicImageThumbnailRenderer: Header, Dekodieren auf Zielgröße, Drehen, JPEG.</summary>
    private static (int W, int H, int SrcW, int SrcH, int Orientation) Render(string path, string target, Variant v)
    {
        int srcW, srcH, orientation;
        using (var header = Open(path))
        {
            var frame = BitmapDecoder.Create(header, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            srcW = frame.PixelWidth;
            srcH = frame.PixelHeight;
            orientation = ReadOrientation(frame.Metadata as BitmapMetadata);
        }

        var bitmap = new BitmapImage();
        using (var stream = Open(path))
        {
            bitmap.BeginInit();
            bitmap.StreamSource = stream;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (v.LongEdgeMode)
            {
                if (srcW >= srcH && srcW > v.ShortEdge)
                {
                    bitmap.DecodePixelWidth = v.ShortEdge;
                }
                else if (srcH > srcW && srcH > v.ShortEdge)
                {
                    bitmap.DecodePixelHeight = v.ShortEdge;
                }
            }
            else
            {
                var (tw, th) = ThumbnailSizing.Fit(srcW, srcH, v.ShortEdge, v.LongEdgeCap);
                if (tw < srcW)
                {
                    if (srcW >= srcH)
                    {
                        bitmap.DecodePixelHeight = th;
                    }
                    else
                    {
                        bitmap.DecodePixelWidth = tw;
                    }
                }
            }

            bitmap.EndInit();
        }

        bitmap.Freeze();
        BitmapSource result = bitmap;
        if (orientation is 6 or 8 or 3)
        {
            var t = new TransformedBitmap(bitmap, new RotateTransform(orientation == 6 ? 90 : orientation == 8 ? 270 : 180));
            t.Freeze();
            result = t;
        }

        var encoder = new JpegBitmapEncoder { QualityLevel = v.Quality };
        encoder.Frames.Add(BitmapFrame.Create(result));
        using (var output = File.Create(target))
        {
            encoder.Save(output);
        }

        return (result.PixelWidth, result.PixelHeight, srcW, srcH, orientation);
    }

    private static FileStream Open(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);

    private static int ReadOrientation(BitmapMetadata? metadata)
    {
        try
        {
            return metadata?.GetQuery("System.Photo.Orientation") is ushort o ? o : 1;
        }
        catch
        {
            return 1;
        }
    }
}
