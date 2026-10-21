using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lumora.Faces;
using Lumora.Services.Faces;
using Lumora.Services.Motifs;
using Lumora.Services.Motifs.Models;
using Lumora.Services.Motifs.Siglip;
using Lumora.Services.Quality;
using Xunit.Abstractions;

namespace Lumora.Wpf.Tests.Quality;

/// <summary>Nur auf Wunsch (<c>LUMORA_QUALITY_MEASURE=1</c>), sonst übersprungen.</summary>
public sealed class QualityMeasureFactAttribute : FactAttribute
{
    public const string Variable = "LUMORA_QUALITY_MEASURE";

    public QualityMeasureFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
        {
            Skip = "Messung nur lokal: LUMORA_QUALITY_MEASURE=1 setzen (optional LUMORA_QUALITY_SAMPLE_DIR, LUMORA_MOTIF_MODEL).";
        }
    }
}

/// <summary>
/// Messung der Foto-Güte (Spike 16, <c>docs/spikes/16-foto-guete.md</c>):
/// (1) Laufzeit je 1 000 Vorschaubilder (synthetische 533 × 400-JPEGs im Temp-Ordner, WIC-Lader wie im Hintergrund-Index,
/// Vergleich mit dem 128er-Laden des pHash); (2) mit <c>LUMORA_QUALITY_SAMPLE_DIR</c> eine Stichprobe echter Fotos (nur
/// lesen; Vorschaubild wird im Temp-Ordner erzeugt und danach gelöscht): Verteilung von Schärfe/Belichtung und ob
/// weichgezeichnete bzw. abgedunkelte Fassungen schlechter abschneiden; (3) zusätzlich mit <c>LUMORA_MOTIF_MODEL</c> die
/// Bildwirkung (Abstand cos⁺ − cos⁻) und dieselben Gegenproben. Gibt ausschließlich zusammengefasste Zahlen aus.
/// </summary>
public sealed class QualityMeasurementTests(ITestOutputHelper output)
{
    [QualityMeasureFact]
    public void Laufzeit_je_1000_Vorschaubilder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lumora-quality-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var files = new List<string>();
            for (var i = 0; i < 1000; i++)
            {
                var path = Path.Combine(dir, $"t{i:0000}.jpg");
                WriteJpeg(Synthetic(i, 533, 400), 533, 400, path);
                files.Add(path);
            }

            var loader = new WicFaceImageLoader();
            Run(loader, files.Take(20).ToList(), ImageQuality.LoadEdge); // Aufwärmen
            var quality = Run(loader, files, ImageQuality.LoadEdge);
            var phash = Run(loader, files, 128, analyze: false);
            output.WriteLine($"1 000 Vorschaubilder (533 × 400 JPEG): Laden ≤ {ImageQuality.LoadEdge} + Güte {quality.Total:N0} ms ({quality.Total / 1000:N2} ms/Bild, davon Rechnen {quality.Analyze / 1000:N2} ms) · zum Vergleich Laden ≤ 128 (pHash) {phash.Total:N0} ms");
            output.WriteLine($"Hochrechnung 500 000 (ein Kern): {quality.Total * 500 / 60_000:N1} min");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [QualityMeasureFact]
    public void Stichprobe_echter_Fotos()
    {
        var root = Environment.GetEnvironmentVariable("LUMORA_QUALITY_SAMPLE_DIR");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            output.WriteLine("LUMORA_QUALITY_SAMPLE_DIR nicht gesetzt — übersprungen.");
            return;
        }

        var count = int.TryParse(Environment.GetEnvironmentVariable("LUMORA_QUALITY_SAMPLE"), out var n) && n > 0 ? n : 300;
        var all = Directory.EnumerateFiles(root, "*.jpg", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var random = new Random(42);
        var sample = all.OrderBy(_ => random.Next()).Take(count).ToList();
        var dir = Path.Combine(Path.GetTempPath(), "lumora-quality-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var loader = new WicFaceImageLoader();
        var originals = new List<FaceImage>();
        try
        {
            for (var i = 0; i < sample.Count; i++)
            {
                try
                {
                    var thumb = Path.Combine(dir, $"s{i:0000}.jpg");
                    MakeThumbnail(sample[i], thumb);
                    originals.Add(loader.Load(thumb, ImageQuality.LoadEdge));
                }
                catch (Exception)
                {
                    // nicht lesbar — zählt nicht
                }
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        var blurred = originals.Select(i => Blur(i, 2)).ToList();
        var darkened = originals.Select(i => Scale(i, 0.3)).ToList();
        var q = originals.Select(i => ImageQuality.Analyze(i.Pixels, i.Width, i.Height)).ToList();
        var qb = blurred.Select(i => ImageQuality.Analyze(i.Pixels, i.Width, i.Height)).ToList();
        var qd = darkened.Select(i => ImageQuality.Analyze(i.Pixels, i.Width, i.Height)).ToList();
        output.WriteLine($"Stichprobe: {originals.Count} von {sample.Count} Fotos gelesen (Bestand {all.Count:N0} JPEG)");
        output.WriteLine($"Laplace-Varianz roh: {Stats(q.Select(x => x.LaplaceVariance).ToList(), "N0")}");
        output.WriteLine($"Schärfe 0..1: {Stats(q.Select(x => x.Sharpness).ToList(), "0.00")} · weichgezeichnet (Kasten r = 2, 2×): {Stats(qb.Select(x => x.Sharpness).ToList(), "0.00")}");
        output.WriteLine($"Belichtung 0..1: {Stats(q.Select(x => x.Exposure).ToList(), "0.00")} · abgedunkelt (× 0,3): {Stats(qd.Select(x => x.Exposure).ToList(), "0.00")}");
        output.WriteLine($"Mittlere Helligkeit: {Stats(q.Select(x => x.MeanLuma).ToList(), "0.00")} · ausgefressen > 5 %: {q.Count(x => x.Clipped > 0.05)} · abgesoffen > 10 %: {q.Count(x => x.Crushed > 0.10)}");
        output.WriteLine($"Gegenprobe: weichgezeichnet unschärfer bei {Share(q, qb, x => x.Sharpness)} · abgedunkelt schlechter belichtet bei {Share(q, qd, x => x.Exposure)}");

        var model = Environment.GetEnvironmentVariable(IMotifModelProvider.OverrideVariable);
        if (string.IsNullOrWhiteSpace(model) || !Directory.Exists(model))
        {
            output.WriteLine("LUMORA_MOTIF_MODEL nicht gesetzt — Bildwirkung übersprungen.");
            return;
        }

        var factory = new OnnxSiglipEncoderFactory(new MotifModelProvider(new Services.Infrastructure.AppDataPaths(Path.GetTempPath()), modelFolder: model));
        using var text = factory.CreateTextEncoder();
        AestheticModel Prompts(IReadOnlyList<string> positive, IReadOnlyList<string> negative)
        {
            var vectors = text.EncodeTexts(positive.Concat(negative).ToList());
            return new AestheticModel(
                factory.ModelVersion,
                MotifVectors.MeanNormalized(vectors.Take(positive.Count).ToList()),
                MotifVectors.MeanNormalized(vectors.Skip(positive.Count).ToList()));
        }

        var aesthetic = Prompts(AestheticPrompts.Positive, AestheticPrompts.Negative);
        using var encoder = factory.CreateImageEncoder(useGpu: true);
        var sw = Stopwatch.StartNew();
        var vo = originals.Select(encoder.EncodeImage).ToList();
        var encodeMs = sw.Elapsed.TotalMilliseconds / Math.Max(1, originals.Count);
        var vb = blurred.Select(encoder.EncodeImage).ToList();
        var vd = darkened.Select(encoder.EncodeImage).ToList();
        var m = vo.Select(v => aesthetic.Margin(v)).ToList();
        var mb = vb.Select(v => aesthetic.Margin(v)).ToList();
        var md = vd.Select(v => aesthetic.Margin(v)).ToList();
        output.WriteLine($"Bildmodell {encoder.ExecutionProvider}: {encodeMs:N1} ms/Bild (nur Messung — im Betrieb liegen die Vektoren schon vor)");

        // Vergleich der Prompt-Sätze (gleiche Bild-Vektoren)
        var sets = new (string Name, AestheticModel Model)[]
        {
            ("gewählt (AestheticPrompts)", aesthetic),
            ("vier je Seite, mit Zufallsfoto", Prompts(
                ["a beautiful, sharp, well-exposed photo", "an excellent photograph with great composition and lovely light", "ein schönes, scharfes, gut belichtetes Foto", "ein gelungenes Urlaubsfoto mit schönen Farben"],
                ["a blurry, dark, failed photo", "a bad photo, out of focus and poorly lit", "ein verschwommenes, dunkles, missglücktes Foto", "an accidental photo of the floor, a pocket or a finger over the lens"])),
            ("nur englisch", Prompts(
                ["a beautiful, sharp, well-exposed photo", "an excellent photograph with great composition and lovely light"],
                ["a blurry, dark, failed photo", "a bad photo, out of focus and poorly lit"])),
            ("nur deutsch", Prompts(
                ["ein schönes, scharfes, gut belichtetes Foto", "ein gelungenes Urlaubsfoto mit schönen Farben"],
                ["ein verschwommenes, dunkles, missglücktes Foto"])),
            ("einfach", Prompts(["a good photo"], ["a bad photo"])),
            ("Paare de+en", Prompts(
                ["a beautiful, sharp, well-exposed photo", "ein schönes, scharfes, gut belichtetes Foto"],
                ["a blurry, dark, failed photo", "ein verschwommenes, dunkles, missglücktes Foto"])),
            ("Paare de+en + unscharf/verwackelt", Prompts(
                ["a beautiful, sharp, well-exposed photo", "ein schönes, scharfes, gut belichtetes Foto", "ein gelungenes Urlaubsfoto mit schönen Farben"],
                ["a blurry, dark, failed photo", "ein verschwommenes, dunkles, missglücktes Foto", "ein unscharfes, verwackeltes, zu dunkles Foto"])),
        };
        foreach (var (name, set) in sets)
        {
            var so = vo.Select(v => set.Margin(v)).ToList();
            output.WriteLine($"  Prompt-Satz {name}: weich niedriger {Share(so, vb.Select(v => set.Margin(v)).ToList())} · dunkel niedriger {Share(so, vd.Select(v => set.Margin(v)).ToList())} · Spearman ↔ Schärfe {Spearman(so, q.Select(x => x.Sharpness).ToList()):0.00} · Abstand {Stats(so, "0.0000")}");
        }

        output.WriteLine($"Abstand cos⁺ − cos⁻: {Stats(m, "0.0000")} · weichgezeichnet {Stats(mb, "0.0000")} · abgedunkelt {Stats(md, "0.0000")}");
        output.WriteLine($"Bildwirkung 0..1 (Mitte {AestheticPrompts.Center:0.000}, Steilheit {AestheticPrompts.Slope:N0}): {Stats(m.Select(Sigmoid).ToList(), "0.00")}");
        output.WriteLine($"Gegenprobe Bildwirkung: weichgezeichnet niedriger bei {Share(m, mb)} · abgedunkelt niedriger bei {Share(m, md)}");
        output.WriteLine($"Rangkorrelation (Spearman) Bildwirkung ↔ Schärfe {Spearman(m, q.Select(x => x.Sharpness).ToList()):0.00} · ↔ Belichtung {Spearman(m, q.Select(x => x.Exposure).ToList()):0.00}");
    }

    private static double Sigmoid(double margin) => 1.0 / (1.0 + Math.Exp(-(margin - AestheticPrompts.Center) * AestheticPrompts.Slope));

    private (double Total, double Analyze) Run(IFaceImageLoader loader, IReadOnlyList<string> files, int edge, bool analyze = true)
    {
        var total = Stopwatch.StartNew();
        var analyzeMs = 0.0;
        foreach (var file in files)
        {
            var image = loader.Load(file, edge);
            if (analyze)
            {
                var sw = Stopwatch.StartNew();
                ImageQuality.Analyze(image.Pixels, image.Width, image.Height);
                analyzeMs += sw.Elapsed.TotalMilliseconds;
            }
        }

        return (total.Elapsed.TotalMilliseconds, analyzeMs);
    }

    private static string Share<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, Func<T, double> value)
        => Share(a.Select(value).ToList(), b.Select(value).ToList());

    private static string Share(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        var lower = a.Zip(b).Count(x => x.Second < x.First);
        return $"{lower} von {a.Count} ({100.0 * lower / Math.Max(1, a.Count):N0} %)";
    }

    private static double Spearman(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        static double[] Rank(IReadOnlyList<double> v)
        {
            var order = Enumerable.Range(0, v.Count).OrderBy(i => v[i]).ToArray();
            var r = new double[v.Count];
            for (var k = 0; k < order.Length; k++)
            {
                r[order[k]] = k;
            }

            return r;
        }

        var ra = Rank(a);
        var rb = Rank(b);
        double ma = ra.Average(), mb = rb.Average(), cov = 0, va = 0, vb = 0;
        for (var i = 0; i < ra.Length; i++)
        {
            cov += (ra[i] - ma) * (rb[i] - mb);
            va += (ra[i] - ma) * (ra[i] - ma);
            vb += (rb[i] - mb) * (rb[i] - mb);
        }

        return va <= 0 || vb <= 0 ? 0 : cov / Math.Sqrt(va * vb);
    }

    private static string Stats(IReadOnlyList<double> values, string format)
    {
        if (values.Count == 0)
        {
            return "—";
        }

        var sorted = values.Order().ToList();
        double P(double q) => sorted[Math.Clamp((int)Math.Round(q * (sorted.Count - 1)), 0, sorted.Count - 1)];
        return $"p10 {P(0.1).ToString(format)} · p50 {P(0.5).ToString(format)} · p90 {P(0.9).ToString(format)}";
    }

    /// <summary>Vorschaubild wie im Katalog: kurze Kante 400, JPEG.</summary>
    private static void MakeThumbnail(string source, string target)
    {
        var probe = BitmapFrame.Create(new Uri(source), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(source);
        if (probe.PixelWidth <= probe.PixelHeight)
        {
            image.DecodePixelWidth = Math.Min(400, probe.PixelWidth);
        }
        else
        {
            image.DecodePixelHeight = Math.Min(400, probe.PixelHeight);
        }

        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(target);
        encoder.Save(stream);
    }

    private static void WriteJpeg(byte[] rgb, int width, int height, string path)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, rgb, width * 3);
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static byte[] Synthetic(int seed, int width, int height)
    {
        var random = new Random(seed);
        var pixels = new byte[width * height * 3];
        random.NextBytes(pixels);
        var shapes = Enumerable.Range(0, 8).Select(_ => (X: random.Next(width), Y: random.Next(height), R: random.Next(20, 120), C: (byte)random.Next(256))).ToArray();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var p = ((y * width) + x) * 3;
                var v = (byte)((x + y) * 255 / (width + height));
                foreach (var s in shapes)
                {
                    if (((x - s.X) * (x - s.X)) + ((y - s.Y) * (y - s.Y)) < s.R * s.R)
                    {
                        v = s.C;
                    }
                }

                pixels[p] = (byte)((pixels[p] / 16) + v * 15 / 16);
                pixels[p + 1] = (byte)((pixels[p + 1] / 16) + v * 15 / 16);
                pixels[p + 2] = (byte)((pixels[p + 2] / 16) + v * 15 / 16);
            }
        }

        return pixels;
    }

    private static FaceImage Scale(FaceImage image, double factor)
        => new(image.Pixels.Select(p => (byte)Math.Clamp((int)Math.Round(p * factor), 0, 255)).ToArray(), image.Width, image.Height);

    private static FaceImage Blur(FaceImage image, int radius)
    {
        var current = image.Pixels;
        for (var pass = 0; pass < 2; pass++)
        {
            current = BoxBlur(current, image.Width, image.Height, radius);
        }

        return new FaceImage(current, image.Width, image.Height);
    }

    private static byte[] BoxBlur(byte[] source, int width, int height, int radius)
    {
        var temp = new byte[source.Length];
        var target = new byte[source.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                for (var c = 0; c < 3; c++)
                {
                    int sum = 0, count = 0;
                    for (var d = -radius; d <= radius; d++)
                    {
                        sum += source[(((y * width) + Math.Clamp(x + d, 0, width - 1)) * 3) + c];
                        count++;
                    }

                    temp[(((y * width) + x) * 3) + c] = (byte)(sum / count);
                }
            }
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                for (var c = 0; c < 3; c++)
                {
                    int sum = 0, count = 0;
                    for (var d = -radius; d <= radius; d++)
                    {
                        sum += temp[(((Math.Clamp(y + d, 0, height - 1) * width) + x) * 3) + c];
                        count++;
                    }

                    target[(((y * width) + x) * 3) + c] = (byte)(sum / count);
                }
            }
        }

        return target;
    }
}
