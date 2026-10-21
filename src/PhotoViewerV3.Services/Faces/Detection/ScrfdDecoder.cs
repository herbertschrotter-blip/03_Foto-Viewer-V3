namespace PhotoViewerV3.Services.Faces.Detection;

/// <summary>Art einer SCRFD-Ausgabe: Score (1 Wert je Anker), Box-Abstände (4) oder Landmarken-Abstände (10).</summary>
public enum ScrfdOutputKind
{
    Score = 1,
    Box = 4,
    Landmarks = 10,
}

/// <summary>Zuordnung der neun SCRFD-Ausgaben zu Stride (8/16/32) und Art.</summary>
public sealed class ScrfdOutputLayout
{
    private readonly Dictionary<(int Stride, ScrfdOutputKind Kind), int> _indices;

    private ScrfdOutputLayout(Dictionary<(int, ScrfdOutputKind), int> indices) => _indices = indices;

    public int IndexOf(int stride, ScrfdOutputKind kind) => _indices[(stride, kind)];

    /// <summary>
    /// Ordnet die Ausgaben über ihre Formen zu (nicht über Namen, die je Export anders heißen): letzte Dimension 1/4/10 =
    /// Art, Anzahl der Zeilen = (Eingabe / Stride)² × <see cref="ScrfdDecoder.AnchorsPerPosition"/>. Formen dürfen eine
    /// führende Batch-Dimension 1 haben ([1, n, k] oder [n, k]). Wirft, wenn die Ausgaben nicht zu det_10g passen.
    /// </summary>
    public static ScrfdOutputLayout Resolve(IReadOnlyList<int[]> shapes, int inputSize = ScrfdPreprocessor.InputSize)
    {
        var indices = new Dictionary<(int, ScrfdOutputKind), int>();
        for (var i = 0; i < shapes.Count; i++)
        {
            var shape = shapes[i];
            if (shape.Length < 2)
            {
                throw new InvalidDataException($"SCRFD-Ausgabe {i} hat eine unerwartete Form [{string.Join(",", shape)}].");
            }

            var k = shape[^1];
            var n = shape[^2];
            if (!Enum.IsDefined(typeof(ScrfdOutputKind), k))
            {
                throw new InvalidDataException($"SCRFD-Ausgabe {i}: letzte Dimension {k} ist weder 1, 4 noch 10.");
            }

            var stride = ScrfdDecoder.Strides.FirstOrDefault(s => AnchorCount(inputSize, s) == n);
            if (stride == 0)
            {
                throw new InvalidDataException($"SCRFD-Ausgabe {i}: {n} Anker passen zu keinem Stride bei Eingabe {inputSize}.");
            }

            if (!indices.TryAdd((stride, (ScrfdOutputKind)k), i))
            {
                throw new InvalidDataException($"SCRFD-Ausgabe für Stride {stride}/{(ScrfdOutputKind)k} doppelt.");
            }
        }

        foreach (var stride in ScrfdDecoder.Strides)
        {
            foreach (var kind in Enum.GetValues<ScrfdOutputKind>())
            {
                if (!indices.ContainsKey((stride, kind)))
                {
                    throw new InvalidDataException($"SCRFD-Ausgabe für Stride {stride}/{kind} fehlt (Modell mit 5 Landmarken erwartet).");
                }
            }
        }

        return new ScrfdOutputLayout(indices);
    }

    public static int AnchorCount(int inputSize, int stride)
        => (inputSize / stride) * (inputSize / stride) * ScrfdDecoder.AnchorsPerPosition;
}

/// <summary>
/// Dekodierung der SCRFD-Ausgaben wie InsightFace (<c>SCRFD.forward</c>/<c>detect</c>, ADR-0035): drei Strides
/// 8/16/32 mit je 2 Ankern pro Gitterposition, Ankermitte = (Spalte × Stride, Zeile × Stride) in Eingabepixeln, Box =
/// Mitte ∓ Abstände × Stride (links, oben, rechts, unten), Landmarke = Mitte + Abstand × Stride. Danach Score-Schwelle,
/// Rückrechnung in Bildpixel (÷ Letterbox-Skalierung) und NMS (IoU-Formel mit „+1“ wie InsightFace).
/// </summary>
public static class ScrfdDecoder
{
    public const int AnchorsPerPosition = 2;
    public const float DefaultScoreThreshold = 0.5f;
    public const float DefaultNmsThreshold = 0.4f;

    public static readonly int[] Strides = [8, 16, 32];

    /// <summary>Kandidaten eines Strides oberhalb der Schwelle, in EINGABE-Pixeln (vor der Rückrechnung).</summary>
    public static void DecodeStride(
        ReadOnlySpan<float> scores,
        ReadOnlySpan<float> boxes,
        ReadOnlySpan<float> landmarks,
        int stride,
        int inputSize,
        float scoreThreshold,
        List<FaceDetection> into)
    {
        var width = inputSize / stride;
        var count = ScrfdOutputLayout.AnchorCount(inputSize, stride);
        if (scores.Length < count || boxes.Length < count * 4 || landmarks.Length < count * 10)
        {
            throw new ArgumentException($"SCRFD-Ausgaben für Stride {stride} zu kurz.");
        }

        for (var i = 0; i < count; i++)
        {
            var score = scores[i];
            if (score < scoreThreshold)
            {
                continue;
            }

            var position = i / AnchorsPerPosition;
            var cx = (float)(position % width * stride);
            var cy = (float)(position / width * stride);
            var b = boxes.Slice(i * 4, 4);
            var k = landmarks.Slice(i * 10, 10);
            var points = new FacePoint[5];
            for (var p = 0; p < 5; p++)
            {
                points[p] = new FacePoint(cx + k[p * 2] * stride, cy + k[p * 2 + 1] * stride);
            }

            into.Add(new FaceDetection(
                cx - b[0] * stride,
                cy - b[1] * stride,
                cx + b[2] * stride,
                cy + b[3] * stride,
                score,
                points));
        }
    }

    /// <summary>Rechnet Eingabepixel in Bildpixel um (÷ <paramref name="scale"/>).</summary>
    public static FaceDetection ToImage(FaceDetection d, float scale)
    {
        var points = new FacePoint[d.Landmarks.Length];
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = new FacePoint(d.Landmarks[i].X / scale, d.Landmarks[i].Y / scale);
        }

        return new FaceDetection(d.X1 / scale, d.Y1 / scale, d.X2 / scale, d.Y2 / scale, d.Score, points);
    }

    /// <summary>
    /// Non-Maximum-Suppression wie InsightFace: nach Score absteigend, verwirft Kandidaten mit IoU &gt;
    /// <paramref name="threshold"/> zu einem behaltenen. Flächen mit „+1“ (Pixel-inklusive Konvention).
    /// </summary>
    public static List<FaceDetection> Nms(IReadOnlyList<FaceDetection> candidates, float threshold = DefaultNmsThreshold)
    {
        var order = candidates.OrderByDescending(c => c.Score).ToList();
        var kept = new List<FaceDetection>(order.Count);
        var suppressed = new bool[order.Count];
        for (var i = 0; i < order.Count; i++)
        {
            if (suppressed[i])
            {
                continue;
            }

            var a = order[i];
            kept.Add(a);
            for (var j = i + 1; j < order.Count; j++)
            {
                if (!suppressed[j] && IoU(a, order[j]) > threshold)
                {
                    suppressed[j] = true;
                }
            }
        }

        return kept;
    }

    /// <summary>IoU mit „+1“-Konvention (InsightFace <c>nms</c>).</summary>
    public static float IoU(FaceDetection a, FaceDetection b)
    {
        var areaA = (a.X2 - a.X1 + 1) * (a.Y2 - a.Y1 + 1);
        var areaB = (b.X2 - b.X1 + 1) * (b.Y2 - b.Y1 + 1);
        var w = Math.Max(0f, Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1) + 1);
        var h = Math.Max(0f, Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1) + 1);
        var inter = w * h;
        var union = areaA + areaB - inter;
        return union <= 0 ? 0f : inter / union;
    }

    /// <summary>
    /// Ganze Dekodierung: drei Strides → Schwelle → Bildpixel → NMS. <paramref name="outputs"/> = flache Ausgaben in
    /// Modellreihenfolge, <paramref name="layout"/> ordnet sie zu.
    /// </summary>
    public static List<FaceDetection> Decode(
        IReadOnlyList<float[]> outputs,
        ScrfdOutputLayout layout,
        Letterbox letterbox,
        float scoreThreshold = DefaultScoreThreshold,
        float nmsThreshold = DefaultNmsThreshold,
        int inputSize = ScrfdPreprocessor.InputSize)
    {
        var candidates = new List<FaceDetection>();
        foreach (var stride in Strides)
        {
            DecodeStride(
                outputs[layout.IndexOf(stride, ScrfdOutputKind.Score)],
                outputs[layout.IndexOf(stride, ScrfdOutputKind.Box)],
                outputs[layout.IndexOf(stride, ScrfdOutputKind.Landmarks)],
                stride,
                inputSize,
                scoreThreshold,
                candidates);
        }

        var inImage = candidates.Select(c => ToImage(c, letterbox.Scale)).ToList();
        return Nms(inImage, nmsThreshold);
    }
}
