using System.Diagnostics;
using System.Globalization;
using System.IO;
using PhotoViewerV3.Faces;
using PhotoViewerV3.Services.Faces;
using PhotoViewerV3.Services.Faces.Indexing;
using PhotoViewerV3.Services.Faces.Onnx;
using Xunit.Abstractions;

namespace PhotoViewerV3.Wpf.Tests.Faces;

/// <summary>
/// Läuft nur mit Modellen (<c>PHOTOVIEWERV3_FACE_MODELS</c>) UND einem Stichproben-Ordner (<c>PV3_FACE_SAMPLE_DIR</c>);
/// sonst übersprungen. Für Messungen an echten Fotos: gibt ausschließlich zusammengefasste Zahlen aus — keine
/// Dateinamen, Pfade, Bilder oder Ausschnitte.
/// </summary>
public sealed class FaceSampleFactAttribute : FactAttribute
{
    public const string SampleVariable = "PV3_FACE_SAMPLE_DIR";

    public FaceSampleFactAttribute()
    {
        var models = Environment.GetEnvironmentVariable("PHOTOVIEWERV3_FACE_MODELS");
        var sample = Environment.GetEnvironmentVariable(SampleVariable);
        if (string.IsNullOrWhiteSpace(models) || !Directory.Exists(models) || string.IsNullOrWhiteSpace(sample) || !Directory.Exists(sample))
        {
            Skip = "Messung nur lokal: PHOTOVIEWERV3_FACE_MODELS und PV3_FACE_SAMPLE_DIR setzen.";
        }
    }
}

/// <summary>
/// Messung der Gesichtserkennung (Spike 09, <c>docs/spikes/09-gesichtserkennung.md</c>): Stichprobe aus
/// <c>PV3_FACE_SAMPLE_DIR</c> (nur lesen) — <c>PV3_FACE_SAMPLE_PAIRS</c> zufällige Fotos (Standard 500, fester Seed)
/// plus jeweils das nach Dateinamen folgende Foto desselben Ordners (Handy-Serien: oft dieselben Personen). Gemessen:
/// ms je Foto für Laden (WIC, längste Kante 1280), Erkennen (SCRFD) und Fingerabdruck (ArcFace) — CPU und DirectML —,
/// Durchsatz mit Vorab-Laden, Gesichter je Foto, Qualitätsverteilung, Kosinus-Ähnlichkeit (verschiedene Personen =
/// Gesichter im selben Foto; wahrscheinlich dieselbe Person = einziges Gesicht zweier Serienfotos an ähnlicher Stelle).
/// </summary>
public sealed class FaceRecognitionMeasurementTests(ITestOutputHelper output)
{
    [FaceSampleFact]
    public void Stichprobe_messen()
    {
        var models = Environment.GetEnvironmentVariable("PHOTOVIEWERV3_FACE_MODELS")!;
        var root = Environment.GetEnvironmentVariable(FaceSampleFactAttribute.SampleVariable)!;
        var pairs = int.TryParse(Environment.GetEnvironmentVariable("PV3_FACE_SAMPLE_PAIRS"), out var p) && p > 0 ? p : 500;
        var sample = BuildSample(root, pairs);
        output.WriteLine($"Stichprobe: {sample.Count} Fotos ({pairs} Serienpaare angefragt)");

        var loader = new WicFaceImageLoader();
        var detector = Path.Combine(models, "det_10g.onnx");
        var recognizer = Path.Combine(models, "w600k_r50.onnx");

        // 1) Laden (einmal gemessen, Ergebnisse nicht aufgehoben — RAM)
        var loadMs = new List<double>();
        var loadErrors = 0;
        foreach (var file in sample)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                loader.Load(file, FaceIndexDefaults.LoadMaxEdge);
                loadMs.Add(sw.Elapsed.TotalMilliseconds);
            }
            catch (Exception)
            {
                loadErrors++;
            }
        }

        output.WriteLine($"Laden WIC (≤ {FaceIndexDefaults.LoadMaxEdge} px): {Stats(loadMs)} · Fehler {loadErrors}");

        PhotoResult?[]? gpuResults = null;
        foreach (var useGpu in new[] { false, true })
        {
            using var analyzer = OnnxFaceAnalyzer.Load(detector, recognizer, useGpu);
            var label = analyzer.ExecutionProvider;
            var detect = new List<double>();
            var embed = new List<double>();
            var embedPerFace = new List<double>();
            var results = new PhotoResult?[sample.Count];
            var warm = loader.Load(sample[0], FaceIndexDefaults.LoadMaxEdge);
            analyzer.Analyze(warm); // Aufwärmen (DirectML kompiliert beim ersten Lauf)

            for (var i = 0; i < sample.Count; i++)
            {
                FaceImage image;
                try
                {
                    image = loader.Load(sample[i], FaceIndexDefaults.LoadMaxEdge);
                }
                catch (Exception)
                {
                    continue;
                }

                var analysis = analyzer.Analyze(image);
                detect.Add(analysis.DetectTime.TotalMilliseconds);
                embed.Add(analysis.EmbedTime.TotalMilliseconds);
                if (analysis.Faces.Count > 0)
                {
                    embedPerFace.Add(analysis.EmbedTime.TotalMilliseconds / analysis.Faces.Count);
                }

                results[i] = new PhotoResult(analysis.ImageWidth, analysis.ImageHeight, analysis.Faces);
            }

            output.WriteLine($"[{label}] Erkennen: {Stats(detect)}");
            output.WriteLine($"[{label}] Fingerabdruck je Foto: {Stats(embed)} · je Gesicht: {Stats(embedPerFace)}");

            // Durchsatz wie im Index: zwei Bilder werden vorab geladen, Analyse nacheinander.
            foreach (var prefetch in new[] { 1, 2, 3, 4 })
            {
                var throughput = MeasurePipelined(loader, analyzer, sample.Skip(prefetch * 150).Take(150).ToList(), prefetch);
                output.WriteLine($"[{label}] Durchsatz mit {prefetch} Bild(ern) vorab geladen: {throughput:N1} ms/Foto");
            }
            if (useGpu)
            {
                gpuResults = results;
            }
            else
            {
                Report(output, results);
            }
        }

        if (gpuResults is not null)
        {
            output.WriteLine("Kontrolle DirectML gegen CPU:");
            Report(output, gpuResults);
        }
    }

    private static double MeasurePipelined(WicFaceImageLoader loader, IFaceAnalyzer analyzer, List<string> files, int prefetch)
    {
        var sw = Stopwatch.StartNew();
        var queue = new Queue<Task<FaceImage?>>();
        var next = 0;
        while (next < files.Count && queue.Count < prefetch)
        {
            queue.Enqueue(LoadAsync(loader, files[next++]));
        }

        var count = 0;
        while (queue.Count > 0)
        {
            var image = queue.Dequeue().GetAwaiter().GetResult();
            if (next < files.Count)
            {
                queue.Enqueue(LoadAsync(loader, files[next++]));
            }

            if (image is not null)
            {
                analyzer.Analyze(image);
                count++;
            }
        }

        return sw.Elapsed.TotalMilliseconds / Math.Max(1, count);
    }

    private static Task<FaceImage?> LoadAsync(WicFaceImageLoader loader, string file)
        => Task.Run<FaceImage?>(() =>
        {
            try
            {
                return loader.Load(file, FaceIndexDefaults.LoadMaxEdge);
            }
            catch (Exception)
            {
                return null;
            }
        });

    private static void Report(ITestOutputHelper output, PhotoResult?[] results)
    {
        var analysed = results.Where(r => r is not null).Select(r => r!).ToList();
        var faceCounts = analysed.Select(r => (double)r.Faces.Count).ToList();
        var withFaces = analysed.Count(r => r.Faces.Count > 0);
        output.WriteLine($"  Fotos analysiert {analysed.Count} · mit Gesichtern {withFaces} ({100.0 * withFaces / Math.Max(1, analysed.Count):N1} %) · Gesichter gesamt {faceCounts.Sum():N0} · je Foto Ø {faceCounts.DefaultIfEmpty(0).Average():N2}, max {faceCounts.DefaultIfEmpty(0).Max():N0}");
        var hist = analysed.GroupBy(r => Math.Min(r.Faces.Count, 5)).OrderBy(g => g.Key).Select(g => $"{(g.Key == 5 ? "5+" : g.Key.ToString(CultureInfo.InvariantCulture))}: {g.Count()}");
        output.WriteLine($"  Gesichter je Foto: {string.Join(" · ", hist)}");

        var faces = analysed.SelectMany(r => r.Faces).ToList();
        output.WriteLine($"  Score: {Stats(faces.Select(f => (double)f.Quality.DetScore).ToList())}");
        output.WriteLine($"  Größe px: {Stats(faces.Select(f => (double)f.Quality.Size).ToList())}");
        output.WriteLine($"  Schärfe (Laplace-Var.): {Stats(faces.Select(f => (double)f.Quality.Sharpness).ToList())}");
        output.WriteLine($"  Qualität: {Stats(faces.Select(f => (double)f.Quality.Quality).ToList())}");
        var qHist = faces.GroupBy(f => Math.Min(9, (int)(f.Quality.Quality * 10))).OrderBy(g => g.Key).Select(g => $"{g.Key / 10.0:N1}–{(g.Key + 1) / 10.0:N1}: {g.Count()}");
        output.WriteLine($"  Qualität verteilt: {string.Join(" · ", qHist)}");
        var norms = faces.Select(f => Math.Sqrt(f.Embedding.Sum(v => (double)v * v))).ToList();
        output.WriteLine($"  Norm der Fingerabdrücke: min {norms.DefaultIfEmpty(0).Min():N4}, max {norms.DefaultIfEmpty(0).Max():N4}");

        // Verschiedene Personen: Gesichter im selben Foto (nur Gesichter mit Qualität ≥ 0.3, sonst dominieren Unschärfen)
        var different = new List<double>();
        foreach (var r in analysed)
        {
            var good = r.Faces.Where(f => f.Quality.Quality >= 0.3f).ToList();
            for (var a = 0; a < good.Count; a++)
            {
                for (var b = a + 1; b < good.Count; b++)
                {
                    different.Add(FaceEmbeddingCodec.Cosine(good[a].Embedding, good[b].Embedding));
                }
            }
        }

        output.WriteLine($"  Kosinus gleiches Foto (verschiedene Personen, n={different.Count}): {Stats(different)} · Anteil > 0.4: {Share(different, 0.4):N1} % · Anteil < 0.2: {Share(different, -1, 0.2):N1} %");

        // Wahrscheinlich dieselbe Person: Serienpaar (Index 2k, 2k+1), beide mit genau einem Gesicht, ähnliche Stelle.
        var same = new List<double>();
        for (var i = 0; i + 1 < results.Length; i += 2)
        {
            if (results[i] is { Faces.Count: 1 } a && results[i + 1] is { Faces.Count: 1 } b)
            {
                var boxA = FaceIndexStore.ToRelative(a.Faces[0].Detection, a.Width, a.Height);
                var boxB = FaceIndexStore.ToRelative(b.Faces[0].Detection, b.Width, b.Height);
                if (RelativeBox.IoU(boxA, boxB) >= 0.3f)
                {
                    same.Add(FaceEmbeddingCodec.Cosine(a.Faces[0].Embedding, b.Faces[0].Embedding));
                }
            }
        }

        output.WriteLine($"  Kosinus Serienpaar (wahrscheinlich dieselbe Person, n={same.Count}): {Stats(same)} · Anteil > 0.4: {Share(same, 0.4):N1} %");
    }

    /// <summary>Zufällige Anker (fester Seed) und ihr Nachfolger nach Dateiname im selben Ordner.</summary>
    private static List<string> BuildSample(string root, int pairs)
    {
        var byFolder = Directory.EnumerateFiles(root, "*.jpg", SearchOption.AllDirectories)
            .GroupBy(f => Path.GetDirectoryName(f)!, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g.OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).Select((f, i) => (Folder: g.Key, Index: i, File: f, Count: g.Count())))
            .Where(x => x.Index + 1 < x.Count)
            .ToList();
        var folderFiles = byFolder.GroupBy(x => x.Folder).ToDictionary(g => g.Key, g => g.OrderBy(x => x.Index).Select(x => x.File).ToList());
        var random = new Random(4711);
        var anchors = byFolder.OrderBy(_ => random.Next()).Take(pairs).ToList();
        var sample = new List<string>(anchors.Count * 2);
        foreach (var a in anchors)
        {
            var list = folderFiles[a.Folder];
            var pos = list.IndexOf(a.File);
            sample.Add(a.File);
            sample.Add(pos + 1 < list.Count ? list[pos + 1] : Directory.EnumerateFiles(a.Folder, "*.jpg").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Last());
        }

        return sample;
    }

    private static string Stats(List<double> values)
    {
        if (values.Count == 0)
        {
            return "—";
        }

        var sorted = values.OrderBy(v => v).ToList();
        double Pct(double q) => sorted[(int)Math.Min(sorted.Count - 1, Math.Round(q * (sorted.Count - 1)))];
        return string.Create(CultureInfo.InvariantCulture,
            $"n={sorted.Count} Ø {sorted.Average():0.###} · p10 {Pct(0.1):0.###} · p50 {Pct(0.5):0.###} · p90 {Pct(0.9):0.###} · max {sorted[^1]:0.###}");
    }

    private static double Share(List<double> values, double above, double below = double.MaxValue)
        => values.Count == 0 ? 0 : 100.0 * values.Count(v => v > above && v < below) / values.Count;

    private sealed record PhotoResult(int Width, int Height, IReadOnlyList<AnalyzedFace> Faces);
}
