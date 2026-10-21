using Lumora.Services.Faces;

namespace Lumora.Services.Motifs.Siglip;

/// <summary>
/// Bild-Vorverarbeitung für das SigLIP-2-Bildmodell exakt nach <c>preprocessor_config.json</c> (ADR-0052):
/// <c>SiglipImageProcessor</c> mit <c>do_resize</c> auf 224 × 224 (ohne Seitenverhältnis, ohne Zuschnitt),
/// <c>resample = 2</c> (PIL BILINEAR), <c>do_rescale</c> mit 1/255 und <c>do_normalize</c> mit Mittelwert 0,5 und
/// Streuung 0,5 je Kanal → Werte in [-1, 1], Tensor NCHW (RGB).
/// <para>
/// Die Skalierung bildet Pillows <c>ImagingResample</c> nach (Resample.c): Dreiecksfilter mit Träger 1 × Skalierung
/// beim Verkleinern (Antialiasing), Gewichte in Festkomma mit 22 Bit, erst waagrecht in ein 8-Bit-Zwischenbild, dann
/// senkrecht, Rundung und Klemmen wie dort. Eingang ist das per WIC schon verkleinerte Bild (längste Kante ≥ 2 × 224
/// bzw. das Vorschaubild) — der Unterschied zum Skalieren aus der vollen Auflösung bleibt unterhalb einer Graustufe
/// je Pixel im Mittel und ändert die Ähnlichkeiten kaum (Prüfung in docs/spikes/14-motive.md).
/// </para>
/// </summary>
public static class SiglipPreprocessor
{
    public const int Size = 224;
    public const float Mean = 0.5f;
    public const float Std = 0.5f;
    public const float RescaleFactor = 1f / 255f;

    private const int PrecisionBits = 32 - 8 - 2;

    /// <summary>Anzahl float-Werte eines Bildes im Tensor (3 × 224 × 224).</summary>
    public const int TensorLength = 3 * Size * Size;

    /// <summary>Skaliert auf 224 × 224 (PIL BILINEAR) und schreibt die normierten Werte NCHW in <paramref name="tensor"/>.</summary>
    public static void Fill(FaceImage image, Span<float> tensor)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (tensor.Length != TensorLength)
        {
            throw new ArgumentException($"Tensor braucht {TensorLength} Werte.", nameof(tensor));
        }

        var resized = ResizeBilinear(image.Pixels, image.Width, image.Height, Size, Size);
        const int plane = Size * Size;
        for (var i = 0; i < plane; i++)
        {
            tensor[i] = Normalize(resized[i * 3]);
            tensor[plane + i] = Normalize(resized[i * 3 + 1]);
            tensor[2 * plane + i] = Normalize(resized[i * 3 + 2]);
        }
    }

    /// <summary>(x / 255 − 0,5) / 0,5 wie <c>rescale</c> + <c>normalize</c> des Bild-Prozessors.</summary>
    public static float Normalize(byte value) => (value * RescaleFactor - Mean) / Std;

    /// <summary>
    /// RGB24 (ohne Zeilen-Auffüllung) auf <paramref name="outWidth"/> × <paramref name="outHeight"/> mit Pillows
    /// BILINEAR-Resampling. Gleiche Größe → Kopie.
    /// </summary>
    public static byte[] ResizeBilinear(byte[] rgb, int width, int height, int outWidth, int outHeight)
    {
        ArgumentNullException.ThrowIfNull(rgb);
        if (rgb.Length != width * height * 3 || outWidth <= 0 || outHeight <= 0)
        {
            throw new ArgumentException("Ungültige Bildmaße.");
        }

        var needHorizontal = outWidth != width;
        var needVertical = outHeight != height;
        var (horizBounds, horizK, horizSize) = Coefficients(width, outWidth);
        var (vertBounds, vertK, vertSize) = Coefficients(height, outHeight);

        var source = rgb;
        var sourceWidth = width;
        if (needHorizontal)
        {
            // Nur die Zeilen, die der senkrechte Durchgang braucht (wie Pillow: ybox_first … ybox_last).
            var first = needVertical ? vertBounds[0] : 0;
            var last = needVertical ? vertBounds[(outHeight - 1) * 2] + vertBounds[(outHeight - 1) * 2 + 1] : height;
            var temp = new byte[outWidth * (last - first) * 3];
            for (var y = 0; y < last - first; y++)
            {
                var row = (y + first) * width * 3;
                for (var x = 0; x < outWidth; x++)
                {
                    var min = horizBounds[x * 2];
                    var count = horizBounds[x * 2 + 1];
                    var kOffset = x * horizSize;
                    long s0 = 1 << (PrecisionBits - 1), s1 = s0, s2 = s0;
                    for (var i = 0; i < count; i++)
                    {
                        var p = row + (min + i) * 3;
                        long k = horizK[kOffset + i];
                        s0 += rgb[p] * k;
                        s1 += rgb[p + 1] * k;
                        s2 += rgb[p + 2] * k;
                    }

                    var o = (y * outWidth + x) * 3;
                    temp[o] = Clip8(s0);
                    temp[o + 1] = Clip8(s1);
                    temp[o + 2] = Clip8(s2);
                }
            }

            if (needVertical)
            {
                for (var y = 0; y < outHeight; y++)
                {
                    vertBounds[y * 2] -= first;
                }
            }

            source = temp;
            sourceWidth = outWidth;
        }

        if (!needVertical)
        {
            return needHorizontal ? source : (byte[])rgb.Clone();
        }

        var result = new byte[outWidth * outHeight * 3];
        for (var y = 0; y < outHeight; y++)
        {
            var min = vertBounds[y * 2];
            var count = vertBounds[y * 2 + 1];
            var kOffset = y * vertSize;
            for (var x = 0; x < sourceWidth; x++)
            {
                long s0 = 1 << (PrecisionBits - 1), s1 = s0, s2 = s0;
                for (var i = 0; i < count; i++)
                {
                    var p = ((min + i) * sourceWidth + x) * 3;
                    long k = vertK[kOffset + i];
                    s0 += source[p] * k;
                    s1 += source[p + 1] * k;
                    s2 += source[p + 2] * k;
                }

                var o = (y * outWidth + x) * 3;
                result[o] = Clip8(s0);
                result[o + 1] = Clip8(s1);
                result[o + 2] = Clip8(s2);
            }
        }

        return result;
    }

    /// <summary>
    /// Pillows <c>precompute_coeffs</c> + <c>normalize_coeffs_8bpc</c> für den Dreiecksfilter (Träger 1): je
    /// Zielpixel erster Quellpixel und Anzahl (bounds), Gewichte in Festkomma (22 Bit), Zeilenlänge ksize.
    /// </summary>
    public static (int[] Bounds, int[] Weights, int KSize) Coefficients(int inSize, int outSize)
    {
        var scale = (double)inSize / outSize;
        var filterScale = Math.Max(scale, 1.0);
        var support = 1.0 * filterScale;
        var ksize = (int)Math.Ceiling(support) * 2 + 1;
        var bounds = new int[outSize * 2];
        var weights = new int[outSize * ksize];
        var pre = new double[ksize];
        for (var xx = 0; xx < outSize; xx++)
        {
            var center = (xx + 0.5) * scale;
            var ss = 1.0 / filterScale;
            var xmin = (int)(center - support + 0.5);
            if (xmin < 0)
            {
                xmin = 0;
            }

            var xmax = (int)(center + support + 0.5);
            if (xmax > inSize)
            {
                xmax = inSize;
            }

            xmax -= xmin;
            var ww = 0.0;
            Array.Clear(pre);
            for (var x = 0; x < xmax; x++)
            {
                var w = Triangle((x + xmin - center + 0.5) * ss);
                pre[x] = w;
                ww += w;
            }

            for (var x = 0; x < ksize; x++)
            {
                var k = ww != 0.0 && x < xmax ? pre[x] / ww : 0.0;
                weights[xx * ksize + x] = k < 0
                    ? (int)(-0.5 + k * (1 << PrecisionBits))
                    : (int)(0.5 + k * (1 << PrecisionBits));
            }

            bounds[xx * 2] = xmin;
            bounds[xx * 2 + 1] = xmax;
        }

        return (bounds, weights, ksize);
    }

    private static double Triangle(double x)
    {
        x = Math.Abs(x);
        return x < 1.0 ? 1.0 - x : 0.0;
    }

    private static byte Clip8(long value)
    {
        if (value >= (1L << PrecisionBits << 8))
        {
            return 255;
        }

        return value <= 0 ? (byte)0 : (byte)(value >> PrecisionBits);
    }
}
