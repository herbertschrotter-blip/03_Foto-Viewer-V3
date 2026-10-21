using System.Numerics;

namespace Lumora.Services.Duplicates;

/// <summary>Bild-Fingerabdruck: 64-Bit-pHash und Merker „fast einfarbig“.</summary>
public readonly record struct ImageFingerprint(ulong Hash, bool LowDetail);

/// <summary>
/// Bild-Fingerabdruck für „ähnlich“ (ADR-0053): pHash nach dem verbreiteten DCT-Verfahren (wie Pythons <c>imagehash.phash</c>)
/// — Helligkeit (BT.601), flächengetreu auf 32 × 32 verkleinert (Seitenverhältnis wird bewusst ignoriert), 2-D-DCT-II,
/// davon die 8 × 8 niedrigsten Frequenzen; Bit = Koeffizient über dem Median der 64. Verkleinern, neu Kodieren, leichte
/// Helligkeits-/Kontraständerungen und kleine Ausschnitte ändern nur wenige Bits; ein anderes Motiv im Mittel ≈ 32.
/// Abstand = Zahl unterschiedlicher Bits (Hamming). Fast einfarbige Bilder (Standardabweichung der 32 × 32-Helligkeit
/// unter <see cref="LowDetailStdDev"/>) bekommen <see cref="ImageFingerprint.LowDetail"/> — ihr Fingerabdruck ist Rauschen
/// und zählt nicht als „ähnlich“ (schwarze Fotos, Bildschirmfotos einer weißen Seite).
/// </summary>
public static class PerceptualHash
{
    /// <summary>Version des Verfahrens (<c>FileFingerprints.ImageHashVersion</c>); ändert sie sich, wird neu berechnet.</summary>
    public const int Version = 1;

    /// <summary>Längere Kante, auf die das Bild beim Laden verkleinert wird (Vorschaubild bzw. Original).</summary>
    public const int LoadEdge = 128;

    /// <summary>Grenze „fast einfarbig“ (Standardabweichung der Helligkeit 0–255).</summary>
    public const double LowDetailStdDev = 3.0;

    private const int Grid = 32;
    private const int Low = 8;

    private static readonly double[] s_cos = BuildCosTable();

    /// <summary>Fingerabdruck aus RGB24-Pixeln (Zeile für Zeile, 3 Bytes je Pixel).</summary>
    public static ImageFingerprint Compute(ReadOnlySpan<byte> rgb, int width, int height)
    {
        if (width <= 0 || height <= 0 || rgb.Length < width * height * 3)
        {
            throw new ArgumentException($"RGB-Puffer passt nicht zu {width}×{height}.", nameof(rgb));
        }

        var gray = new double[width * height];
        for (int i = 0, p = 0; i < gray.Length; i++, p += 3)
        {
            gray[i] = (0.299 * rgb[p]) + (0.587 * rgb[p + 1]) + (0.114 * rgb[p + 2]);
        }

        return ComputeFromGray(gray, width, height);
    }

    /// <summary>Fingerabdruck aus Helligkeitswerten 0–255 (Zeile für Zeile).</summary>
    public static ImageFingerprint ComputeFromGray(ReadOnlySpan<double> gray, int width, int height)
    {
        // 1. flächengetreu auf 32 × 32: erst jede Zeile auf 32 Spalten, dann jede Spalte auf 32 Zeilen
        var rows = new double[height * Grid];
        for (var y = 0; y < height; y++)
        {
            Resample(gray.Slice(y * width, width), rows.AsSpan(y * Grid, Grid));
        }

        var small = new double[Grid * Grid];
        var column = new double[height];
        var target = new double[Grid];
        for (var x = 0; x < Grid; x++)
        {
            for (var y = 0; y < height; y++)
            {
                column[y] = rows[(y * Grid) + x];
            }

            Resample(column, target);
            for (var y = 0; y < Grid; y++)
            {
                small[(y * Grid) + x] = target[y];
            }
        }

        // 2. „fast einfarbig“?
        double mean = 0;
        foreach (var v in small)
        {
            mean += v;
        }

        mean /= small.Length;
        double variance = 0;
        foreach (var v in small)
        {
            variance += (v - mean) * (v - mean);
        }

        var lowDetail = Math.Sqrt(variance / small.Length) < LowDetailStdDev;

        // 3. DCT-II (getrennt nach Zeilen und Spalten), nur die 8 × 8 niedrigsten Frequenzen
        var rowDct = new double[Grid * Low];
        for (var y = 0; y < Grid; y++)
        {
            for (var u = 0; u < Low; u++)
            {
                double sum = 0;
                for (var x = 0; x < Grid; x++)
                {
                    sum += small[(y * Grid) + x] * s_cos[(u * Grid) + x];
                }

                rowDct[(y * Low) + u] = sum;
            }
        }

        var coefficients = new double[Low * Low];
        for (var v = 0; v < Low; v++)
        {
            for (var u = 0; u < Low; u++)
            {
                double sum = 0;
                for (var y = 0; y < Grid; y++)
                {
                    sum += rowDct[(y * Low) + u] * s_cos[(v * Grid) + y];
                }

                coefficients[(v * Low) + u] = sum;
            }
        }

        // 4. Bit = über dem Median
        var sorted = (double[])coefficients.Clone();
        Array.Sort(sorted);
        var median = (sorted[31] + sorted[32]) / 2;
        ulong hash = 0;
        for (var i = 0; i < coefficients.Length; i++)
        {
            if (coefficients[i] > median)
            {
                hash |= 1UL << i;
            }
        }

        return new ImageFingerprint(hash, lowDetail);
    }

    /// <summary>Hamming-Abstand zweier Fingerabdrücke (0–64).</summary>
    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    /// <summary>Gespeicherte Form (<c>FileFingerprints.ImageHash</c>, SQLite INTEGER ist vorzeichenbehaftet).</summary>
    public static long ToStored(ulong hash) => unchecked((long)hash);

    public static ulong FromStored(long stored) => unchecked((ulong)stored);

    /// <summary>Flächengetreues Umrechnen einer Zeile auf eine andere Länge (stückweise konstant integriert).</summary>
    private static void Resample(ReadOnlySpan<double> source, Span<double> target)
    {
        var scale = source.Length / (double)target.Length;
        for (var i = 0; i < target.Length; i++)
        {
            var start = i * scale;
            var end = start + scale;
            double sum = 0;
            for (var x = (int)start; x < end && x < source.Length; x++)
            {
                var left = Math.Max(start, x);
                var right = Math.Min(end, x + 1);
                if (right > left)
                {
                    sum += source[x] * (right - left);
                }
            }

            target[i] = sum / scale;
        }
    }

    private static double[] BuildCosTable()
    {
        var table = new double[Low * Grid];
        for (var u = 0; u < Low; u++)
        {
            for (var x = 0; x < Grid; x++)
            {
                table[(u * Grid) + x] = Math.Cos(((2 * x) + 1) * u * Math.PI / (2 * Grid));
            }
        }

        return table;
    }
}
