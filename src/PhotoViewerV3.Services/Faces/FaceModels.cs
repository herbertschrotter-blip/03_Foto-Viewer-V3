namespace PhotoViewerV3.Services.Faces;

/// <summary>
/// Ein für die Gesichtserkennung geladenes Bild (ADR-0035): RGB, 3 Bytes je Pixel, Zeile für Zeile ohne Auffüllung
/// (<c>Pixels.Length == Width * Height * 3</c>), EXIF-Orientierung bereits angewandt, längste Kante begrenzt.
/// <see cref="SourceWidth"/>/<see cref="SourceHeight"/> = Pixelmaße der Datei nach Orientierung (nur Information).
/// </summary>
public sealed class FaceImage
{
    public FaceImage(byte[] pixels, int width, int height, int sourceWidth = 0, int sourceHeight = 0)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0 || pixels.Length != width * height * 3)
        {
            throw new ArgumentException($"RGB-Puffer passt nicht zu {width}×{height}.", nameof(pixels));
        }

        Pixels = pixels;
        Width = width;
        Height = height;
        SourceWidth = sourceWidth > 0 ? sourceWidth : width;
        SourceHeight = sourceHeight > 0 ? sourceHeight : height;
    }

    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public int SourceWidth { get; }
    public int SourceHeight { get; }
}

/// <summary>Ein Punkt in Bildkoordinaten (Pixel).</summary>
public readonly record struct FacePoint(float X, float Y);

/// <summary>
/// Ein Erkennungsergebnis von SCRFD in Pixeln des analysierten Bildes: Box (x1, y1, x2, y2), Score und 5 Landmarken
/// (Auge links, Auge rechts, Nase, Mundwinkel links, Mundwinkel rechts — aus Sicht des Betrachters).
/// </summary>
public sealed record FaceDetection(float X1, float Y1, float X2, float Y2, float Score, FacePoint[] Landmarks)
{
    public float Width => X2 - X1;
    public float Height => Y2 - Y1;
    public float Area => Math.Max(0, Width) * Math.Max(0, Height);
}

/// <summary>
/// Ein fertig analysiertes Gesicht: Erkennung, L2-normierter Fingerabdruck (512 float) und Qualitätswerte (ADR-0035).
/// </summary>
public sealed record AnalyzedFace(FaceDetection Detection, float[] Embedding, FaceQualityScore Quality);

/// <summary>Qualität eines Gesichts: Einzelwerte und zusammengefasster Wert 0..1 (siehe <see cref="FaceQuality"/>).</summary>
public readonly record struct FaceQualityScore(float DetScore, float Size, float Sharpness, float Quality);

/// <summary>Ergebnis der Analyse einer Datei: Gesichter in Pixeln von <see cref="ImageWidth"/> × <see cref="ImageHeight"/>.</summary>
public sealed record FaceAnalysis(int ImageWidth, int ImageHeight, IReadOnlyList<AnalyzedFace> Faces)
{
    /// <summary>Dauer der Erkennung (Vorverarbeitung, SCRFD, Dekodierung) — für Messung und Statusleiste.</summary>
    public TimeSpan DetectTime { get; init; }

    /// <summary>Dauer von Ausrichtung, Schärfe und ArcFace für alle Gesichter des Bildes.</summary>
    public TimeSpan EmbedTime { get; init; }
}

/// <summary>Fehlercodes in <c>FaceScans.ErrorCode</c>.</summary>
public static class FaceErrorCodes
{
    /// <summary>Datei fehlt (Offline-Wurzel, gelöscht) — wird NICHT als gescannt vermerkt, später erneut versucht.</summary>
    public const string SourceMissing = "source_missing";

    /// <summary>WIC konnte die Datei nicht dekodieren (Format ohne Codec, beschädigt).</summary>
    public const string DecodeFailed = "decode_failed";

    /// <summary>Unerwarteter Fehler in Erkennung oder Fingerabdruck.</summary>
    public const string AnalyzeFailed = "analyze_failed";
}
