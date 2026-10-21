using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoViewerV3.Faces;
using PhotoViewerV3.Services.Faces;
using PhotoViewerV3.Services.Faces.Onnx;
using PhotoViewerV3.Services.Motifs;
using PhotoViewerV3.Services.Motifs.Models;
using PhotoViewerV3.Services.Motifs.Siglip;
using Xunit.Abstractions;

namespace PhotoViewerV3.Wpf.Tests.Motifs;

/// <summary>Nur mit Modell UND Stichprobe (<c>PV3_MOTIF_SAMPLE_DIR</c>), sonst übersprungen.</summary>
public sealed class MotifSampleFactAttribute : FactAttribute
{
    public const string SampleVariable = "PV3_MOTIF_SAMPLE_DIR";

    public MotifSampleFactAttribute()
    {
        var model = Environment.GetEnvironmentVariable(IMotifModelProvider.OverrideVariable);
        var sample = Environment.GetEnvironmentVariable(SampleVariable);
        if (string.IsNullOrWhiteSpace(model) || !Directory.Exists(model) || string.IsNullOrWhiteSpace(sample) || !Directory.Exists(sample))
        {
            Skip = "Messung nur lokal: PHOTOVIEWERV3_MOTIF_MODEL und PV3_MOTIF_SAMPLE_DIR setzen.";
        }
    }
}

/// <summary>
/// Messung der Motiv-Erkennung (Spike 14, <c>docs/spikes/14-motive.md</c>): <c>PV3_MOTIF_SAMPLE</c> zufällige JPEGs
/// (Standard 200, fester Seed) aus <c>PV3_MOTIF_SAMPLE_DIR</c> (nur lesen). Gemessen: ms je Foto für Laden (WIC, längste
/// Kante 448), Vorschaubild-Laden (400er-JPEG im Speicher), Bildmodell DirectML und CPU, Text-Vektoren der
/// Standard-Begriffe; Verteilung der Wahrscheinlichkeiten und Treffer je Schwelle; Gegenprobe mit schwachen
/// Kennzeichen (Bildschirmfotos am Dateinamen, Gesichter über buffalo_l, falls <c>PHOTOVIEWERV3_FACE_MODELS</c>).
/// Gibt ausschließlich zusammengefasste Zahlen aus — keine Dateinamen, Pfade oder Bilder.
/// </summary>
public sealed class MotifMeasurementTests(ITestOutputHelper output)
{
    [MotifSampleFact]
    public void Stichprobe_messen()
    {
        var folder = Environment.GetEnvironmentVariable(IMotifModelProvider.OverrideVariable)!;
        var root = Environment.GetEnvironmentVariable(MotifSampleFactAttribute.SampleVariable)!;
        var count = int.TryParse(Environment.GetEnvironmentVariable("PV3_MOTIF_SAMPLE"), out var n) && n > 0 ? n : 200;
        var all = Directory.EnumerateFiles(root, "*.jpg", SearchOption.AllDirectories)
            .Where(p => !Path.GetFileName(p).StartsWith("Screenshot", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        var shots = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(p => Path.GetFileName(p).StartsWith("Screenshot", StringComparison.OrdinalIgnoreCase)
                        && (p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        var random = new Random(42);
        var sample = all.OrderBy(_ => random.Next()).Take(count).ToList();
        var shotSample = shots.OrderBy(_ => random.Next()).Take(20).ToList();
        output.WriteLine($"Bestand: {all.Count:N0} Fotos (ohne Bildschirmfotos), {shots.Count:N0} Bildschirmfotos · Stichprobe {sample.Count} + {shotSample.Count}");

        var loader = new WicFaceImageLoader();
        var models = new MotifModelProvider(new Services.Infrastructure.AppDataPaths(Path.GetTempPath()), modelFolder: folder);
        var factory = new OnnxSiglipEncoderFactory(models);

        // 1) Laden
        var images = new List<FaceImage?>();
        var loadMs = new List<double>();
        var thumbMs = new List<double>();
        foreach (var file in sample.Concat(shotSample))
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var image = loader.Load(file, 448);
                loadMs.Add(sw.Elapsed.TotalMilliseconds);
                images.Add(image);
                thumbMs.Add(MeasureThumbnailDecode(file));
            }
            catch (Exception)
            {
                images.Add(null);
            }
        }

        output.WriteLine($"Laden WIC (≤ 448 px): {Stats(loadMs)} · Vorschaubild (400 px, aus dem Speicher): {Stats(thumbMs)} · Fehler {images.Count(i => i is null)}");

        // 2) Text-Vektoren
        var textClock = Stopwatch.StartNew();
        using var textEncoder = factory.CreateTextEncoder();
        var textLoad = textClock.Elapsed.TotalMilliseconds;
        textClock.Restart();
        var terms = MotifDefaults.Terms;
        var termVectors = terms.Select(t => MotifVectors.MeanNormalized(textEncoder.EncodeTexts([t.Name, t.Prompt]).ToList())).ToList();
        output.WriteLine($"Sprachmodell laden {textLoad:N0} ms · {terms.Count} Begriffe × 2 Texte: {textClock.Elapsed.TotalMilliseconds:N0} ms ({textClock.Elapsed.TotalMilliseconds / (terms.Count * 2):N1} ms/Text)");

        // 3) Bildmodell GPU und CPU
        float[]?[] vectors = new float[images.Count][];
        foreach (var useGpu in new[] { true, false })
        {
            var loadClock = Stopwatch.StartNew();
            using var encoder = factory.CreateImageEncoder(useGpu);
            var loadTime = loadClock.Elapsed.TotalMilliseconds;
            var ms = new List<double>();
            var limit = useGpu ? images.Count : Math.Min(images.Count, 60);
            for (var i = 0; i < limit; i++)
            {
                if (images[i] is not { } image)
                {
                    continue;
                }

                var sw = Stopwatch.StartNew();
                var vector = encoder.EncodeImage(image);
                ms.Add(sw.Elapsed.TotalMilliseconds);
                if (useGpu)
                {
                    vectors[i] = vector;
                }
                else if (vectors[i] is { } gpuVector)
                {
                    var agreement = MotifVectors.Dot(vector, gpuVector);
                    if (agreement < 0.999f)
                    {
                        output.WriteLine($"  Abweichung CPU/GPU: Kosinus {agreement:0.0000}");
                    }
                }
            }

            output.WriteLine($"Bildmodell {encoder.ExecutionProvider}: Laden {loadTime:N0} ms · je Foto {Stats(ms.Skip(1).ToList())}");
        }

        // 3b) Durchsatz wie im Hintergrund-Index: Original laden (3 vorab, parallel) + Grafikkarte nacheinander
        using (var gpu = factory.CreateImageEncoder(useGpu: true))
        {
            var clock = Stopwatch.StartNew();
            var queue = new Queue<Task<FaceImage?>>();
            var next = 0;
            Task<FaceImage?> Load(string file) => Task.Run(() =>
            {
                try
                {
                    return (FaceImage?)loader.Load(file, MotifDefaultsValues.LoadMaxEdge);
                }
                catch (Exception)
                {
                    return null;
                }
            });
            while (next < sample.Count && queue.Count < MotifDefaultsValues.Prefetch)
            {
                queue.Enqueue(Load(sample[next++]));
            }

            var done = 0;
            while (queue.Count > 0)
            {
                var image = queue.Dequeue().GetAwaiter().GetResult();
                if (next < sample.Count)
                {
                    queue.Enqueue(Load(sample[next++]));
                }

                if (image is not null)
                {
                    gpu.EncodeImage(image);
                    done++;
                }
            }

            output.WriteLine($"Durchsatz DirectML mit {MotifDefaultsValues.Prefetch} vorab (Originale): {clock.Elapsed.TotalMilliseconds / Math.Max(1, done):N1} ms/Foto ({done} Fotos, {clock.Elapsed.TotalSeconds:N1} s)");
        }

        // 4) Verteilung und Treffer
        var scoring = factory.Scoring;
        var photoCount = sample.Count;
        var probs = new List<float[]>();
        for (var i = 0; i < photoCount; i++)
        {
            if (vectors[i] is { } v)
            {
                probs.Add(termVectors.Select(t => scoring.Probability(MotifVectors.Dot(t, v))).ToArray());
            }
        }

        var maxima = probs.Select(p => (double)p.Max()).ToList();
        output.WriteLine($"Höchste Wahrscheinlichkeit je Foto: {Stats(maxima, "0.000")}");
        foreach (var threshold in new[] { 0.005, 0.01, 0.02, 0.03, 0.05, 0.1, 0.2 })
        {
            var hits = probs.Select(p => p.Count(x => x >= threshold)).ToList();
            output.WriteLine($"Schwelle {threshold:0.000}: {hits.Count(h => h > 0)} von {probs.Count} Fotos mit Motiv · Ø {hits.Average():0.00} Motive/Foto · max {hits.Max()}");
        }

        foreach (var threshold in new[] { 0.01, 0.03 })
        {
            var perTerm = terms.Select((t, k) => (t.Name, Count: probs.Count(p => p[k] >= threshold))).Where(x => x.Count > 0).OrderByDescending(x => x.Count).ToList();
            output.WriteLine($"Begriffe bei {threshold:0.00}: " + string.Join(", ", perTerm.Select(x => $"{x.Name} {x.Count}")));
        }

        // 5) Gegenprobe Bildschirmfotos
        var shotIndex = terms.Select((t, k) => (t, k)).First(x => x.t.Name == "Bildschirmfoto").k;
        var docIndex = terms.Select((t, k) => (t, k)).First(x => x.t.Name == "Dokument").k;
        var shotProbs = new List<float[]>();
        for (var i = photoCount; i < images.Count; i++)
        {
            if (vectors[i] is { } v)
            {
                shotProbs.Add(termVectors.Select(t => scoring.Probability(MotifVectors.Dot(t, v))).ToArray());
            }
        }

        foreach (var threshold in new[] { 0.01, 0.03, 0.05 })
        {
            output.WriteLine($"Bildschirmfotos ({shotProbs.Count}) bei {threshold:0.00}: „Bildschirmfoto“ {shotProbs.Count(p => p[shotIndex] >= threshold)}, „Bildschirmfoto“ oder „Dokument“ {shotProbs.Count(p => p[shotIndex] >= threshold || p[docIndex] >= threshold)} · Fotos fälschlich „Bildschirmfoto“ {probs.Count(p => p[shotIndex] >= threshold)} von {probs.Count}");
        }

        // 6) Gegenprobe Gesichter (buffalo_l)
        var faceModels = Environment.GetEnvironmentVariable("PHOTOVIEWERV3_FACE_MODELS");
        if (string.IsNullOrWhiteSpace(faceModels) || !Directory.Exists(faceModels))
        {
            return;
        }

        using var faces = OnnxFaceAnalyzer.Load(Path.Combine(faceModels, "det_10g.onnx"), Path.Combine(faceModels, "w600k_r50.onnx"), useGpu: true);
        var faceCounts = new List<int>();
        for (var i = 0; i < photoCount; i++)
        {
            if (vectors[i] is null)
            {
                continue;
            }

            var image = loader.Load(sample[i], 1280);
            faceCounts.Add(faces.Analyze(image).Faces.Count(f => f.Quality.Size >= 40));
        }

        string[] peopleTerms = ["Gruppe", "Selfie", "Porträt", "Kinder", "Baby", "Party", "Hochzeit"];
        var peopleIdx = peopleTerms.Select(name => terms.Select((t, k) => (t, k)).First(x => x.t.Name == name).k).ToArray();
        var groupIdx = peopleIdx[0];
        foreach (var threshold in new[] { 0.01, 0.03 })
        {
            var noFace = faceCounts.Select((c, i) => (c, i)).Where(x => x.c == 0).ToList();
            var many = faceCounts.Select((c, i) => (c, i)).Where(x => x.c >= 3).ToList();
            output.WriteLine(
                $"Gesichter bei {threshold:0.00}: Fotos ohne Gesicht (≥ 40 px) {noFace.Count}, davon mit Personen-Motiv {noFace.Count(x => peopleIdx.Any(k => probs[x.i][k] >= threshold))} · " +
                $"Fotos mit ≥ 3 Gesichtern {many.Count}, davon „Gruppe“ {many.Count(x => probs[x.i][groupIdx] >= threshold)}, irgendein Personen-Motiv {many.Count(x => peopleIdx.Any(k => probs[x.i][k] >= threshold))} · " +
                $"„Gruppe“ bei < 2 Gesichtern {faceCounts.Select((c, i) => (c, i)).Count(x => x.c < 2 && probs[x.i][groupIdx] >= threshold)}");
        }
    }

    /// <summary>Wie ein Vorschaubild aus dem Katalog: 400er-Kante als JPEG im Speicher, dann mit WIC dekodiert.</summary>
    private static double MeasureThumbnailDecode(string file)
    {
        var source = new BitmapImage();
        source.BeginInit();
        source.UriSource = new Uri(file);
        source.DecodePixelWidth = 400;
        source.CacheOption = BitmapCacheOption.OnLoad;
        source.EndInit();
        source.Freeze();
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var memory = new MemoryStream();
        encoder.Save(memory);
        memory.Position = 0;
        var sw = Stopwatch.StartNew();
        var decoded = new BitmapImage();
        decoded.BeginInit();
        decoded.StreamSource = memory;
        decoded.CacheOption = BitmapCacheOption.OnLoad;
        decoded.EndInit();
        var rgb = new FormatConvertedBitmap(decoded, PixelFormats.Rgb24, null, 0);
        var pixels = new byte[rgb.PixelWidth * rgb.PixelHeight * 3];
        rgb.CopyPixels(pixels, rgb.PixelWidth * 3, 0);
        return sw.Elapsed.TotalMilliseconds;
    }

    private static string Stats(IReadOnlyList<double> values, string format = "N1")
    {
        if (values.Count == 0)
        {
            return "—";
        }

        var sorted = values.Order().ToList();
        double P(double q) => sorted[Math.Clamp((int)Math.Round(q * (sorted.Count - 1)), 0, sorted.Count - 1)];
        return $"Ø {values.Average().ToString(format)} · p50 {P(0.5).ToString(format)} · p90 {P(0.9).ToString(format)} · max {sorted[^1].ToString(format)} (n = {values.Count})";
    }
}
