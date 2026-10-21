namespace PhotoViewerV3.Services.Faces;

/// <summary>
/// Qualität eines Gesichts für das spätere Lernen (ADR-0035): Erkennungs-Score, Größe und Schärfe einzeln, dazu ein
/// zusammengefasster Wert 0..1. Einzelwerte werden gespeichert, damit Welle 2 die Gewichtung ändern kann, ohne neu zu
/// indizieren.
/// <list type="bullet">
/// <item>Score-Anteil: (Score − 0.5) / 0.4, geklemmt — 0.5 ist die Erkennungsschwelle, ab 0.9 voll.</item>
/// <item>Größen-Anteil: (Seitenlänge − 24 px) / (112 − 24), geklemmt — ab 112 px (ArcFace-Eingabe) keine Hochskalierung mehr.</item>
/// <item>Schärfe-Anteil: 1 − e^(−Laplace-Varianz / 150) des ausgerichteten 112er-Graubilds.</item>
/// <item>Qualität = 0.25 · Score + 0.40 · Größe + 0.35 · Schärfe.</item>
/// </list>
/// </summary>
public static class FaceQuality
{
    public const float MinUsefulSize = 24f;
    public const float FullSize = 112f;
    public const float SharpnessScale = 150f;
    public const float ScoreWeight = 0.25f;
    public const float SizeWeight = 0.40f;
    public const float SharpnessWeight = 0.35f;

    /// <summary>Seitenlänge der Box: Wurzel aus Breite × Höhe (Pixel des analysierten Bildes).</summary>
    public static float FaceSize(FaceDetection detection)
        => MathF.Sqrt(Math.Max(0f, detection.Width) * Math.Max(0f, detection.Height));

    public static FaceQualityScore Score(float detScore, float size, float sharpness)
    {
        var s = Clamp01((detScore - 0.5f) / 0.4f);
        var z = Clamp01((size - MinUsefulSize) / (FullSize - MinUsefulSize));
        var h = 1f - MathF.Exp(-Math.Max(0f, sharpness) / SharpnessScale);
        var q = Clamp01(ScoreWeight * s + SizeWeight * z + SharpnessWeight * h);
        return new FaceQualityScore(detScore, size, sharpness, q);
    }

    /// <summary>
    /// Laplace-Varianz (4er-Nachbarschaft, ohne Randpixel) eines RGB-Ausschnitts (Breite × Höhe × 3, Graustufen
    /// 0.299 R + 0.587 G + 0.114 B). Unscharfe Ausschnitte liegen meist unter 50, scharfe über 200.
    /// </summary>
    public static float LaplacianVariance(byte[] rgb, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgb);
        if (rgb.Length != width * height * 3 || width < 3 || height < 3)
        {
            throw new ArgumentException("RGB-Ausschnitt passt nicht zu den Maßen (mindestens 3 × 3).", nameof(rgb));
        }

        var gray = new float[width * height];
        for (var i = 0; i < gray.Length; i++)
        {
            gray[i] = 0.299f * rgb[i * 3] + 0.587f * rgb[i * 3 + 1] + 0.114f * rgb[i * 3 + 2];
        }

        double sum = 0, sumSq = 0;
        var n = 0;
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var c = y * width + x;
                var lap = gray[c - 1] + gray[c + 1] + gray[c - width] + gray[c + width] - 4 * gray[c];
                sum += lap;
                sumSq += lap * lap;
                n++;
            }
        }

        var mean = sum / n;
        return (float)Math.Max(0, sumSq / n - mean * mean);
    }

    private static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
