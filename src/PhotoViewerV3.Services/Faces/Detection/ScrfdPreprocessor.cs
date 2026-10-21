namespace PhotoViewerV3.Services.Faces.Detection;

/// <summary>
/// Letterbox der SCRFD-Eingabe wie InsightFace (<c>SCRFD.detect</c>): das Bild wird mit erhaltenem Seitenverhältnis so
/// skaliert, dass die längere Seite <see cref="ScrfdPreprocessor.InputSize"/> füllt, und OBEN LINKS in ein schwarzes
/// Quadrat gelegt. <see cref="Scale"/> = neue Höhe / Bildhöhe (InsightFace <c>det_scale</c>) — Ausgabekoordinaten
/// geteilt durch <see cref="Scale"/> ergeben Bildpixel.
/// </summary>
public readonly record struct Letterbox(int NewWidth, int NewHeight, float Scale);

/// <summary>
/// Vorverarbeitung für SCRFD det_10g (ADR-0035): Letterbox auf 640 × 640, bilineare Skalierung (Pixelmitten wie
/// OpenCV <c>INTER_LINEAR</c>), Kanalreihenfolge RGB, Normierung <c>(x − 127.5) / 128</c>, Tensor NCHW float32.
/// Der aufgefüllte Rand ist schwarz (Pixelwert 0 → −0.996), wie das Nullbild in InsightFace.
/// </summary>
public static class ScrfdPreprocessor
{
    public const int InputSize = 640;
    public const float Mean = 127.5f;
    public const float Std = 128f;

    /// <summary>Letterbox-Maße wie InsightFace (ganzzahlig abgeschnitten, Skalierung aus der Höhe).</summary>
    public static Letterbox ComputeLetterbox(int width, int height, int inputSize = InputSize)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Bildmaße müssen positiv sein.");
        }

        var imRatio = (double)height / width;
        int newWidth, newHeight;
        if (imRatio > 1.0)
        {
            newHeight = inputSize;
            newWidth = Math.Max(1, (int)(newHeight / imRatio));
        }
        else
        {
            newWidth = inputSize;
            newHeight = Math.Max(1, (int)(newWidth * imRatio));
        }

        return new Letterbox(newWidth, newHeight, (float)newHeight / height);
    }

    /// <summary>
    /// Füllt <paramref name="tensor"/> (Länge 3 × inputSize²) mit dem letterboxten, normierten Bild und liefert die
    /// Letterbox. Der Tensor wird vollständig überschrieben.
    /// </summary>
    public static Letterbox Fill(FaceImage image, Span<float> tensor, int inputSize = InputSize)
    {
        ArgumentNullException.ThrowIfNull(image);
        var plane = inputSize * inputSize;
        if (tensor.Length != 3 * plane)
        {
            throw new ArgumentException($"Tensor muss 3 × {inputSize}² Werte haben.", nameof(tensor));
        }

        var box = ComputeLetterbox(image.Width, image.Height, inputSize);
        tensor.Fill((0f - Mean) / Std);

        var src = image.Pixels;
        var srcW = image.Width;
        var srcH = image.Height;
        var sx = (float)srcW / box.NewWidth;
        var sy = (float)srcH / box.NewHeight;

        // Spalten-Stützstellen einmal berechnen (für jede Zeile gleich).
        var x0s = new int[box.NewWidth];
        var x1s = new int[box.NewWidth];
        var fxs = new float[box.NewWidth];
        for (var dx = 0; dx < box.NewWidth; dx++)
        {
            Sample(dx, sx, srcW, out x0s[dx], out x1s[dx], out fxs[dx]);
        }

        for (var dy = 0; dy < box.NewHeight; dy++)
        {
            Sample(dy, sy, srcH, out var y0, out var y1, out var fy);
            var row0 = y0 * srcW * 3;
            var row1 = y1 * srcW * 3;
            var dstRow = dy * inputSize;
            for (var dx = 0; dx < box.NewWidth; dx++)
            {
                var a = row0 + x0s[dx] * 3;
                var b = row0 + x1s[dx] * 3;
                var c = row1 + x0s[dx] * 3;
                var d = row1 + x1s[dx] * 3;
                var fx = fxs[dx];
                var w00 = (1 - fx) * (1 - fy);
                var w01 = fx * (1 - fy);
                var w10 = (1 - fx) * fy;
                var w11 = fx * fy;
                var o = dstRow + dx;
                for (var ch = 0; ch < 3; ch++)
                {
                    var v = src[a + ch] * w00 + src[b + ch] * w01 + src[c + ch] * w10 + src[d + ch] * w11;
                    tensor[ch * plane + o] = (v - Mean) / Std;
                }
            }
        }

        return box;
    }

    /// <summary>Stützstellen einer Zielkoordinate (Pixelmitte, Rand geklemmt) — wie OpenCV INTER_LINEAR.</summary>
    public static void Sample(int dst, float scale, int srcSize, out int i0, out int i1, out float frac)
    {
        var s = (dst + 0.5f) * scale - 0.5f;
        if (s < 0)
        {
            s = 0;
        }

        i0 = Math.Min((int)s, srcSize - 1);
        i1 = Math.Min(i0 + 1, srcSize - 1);
        frac = s - i0;
        if (i0 == srcSize - 1)
        {
            frac = 0;
        }
    }
}
