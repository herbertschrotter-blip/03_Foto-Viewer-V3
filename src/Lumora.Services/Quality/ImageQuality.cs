namespace Lumora.Services.Quality;

/// <summary>Messwerte eines Bildes: normierte Güte (0..1) und die Rohwerte dahinter (Tests, Messung).</summary>
/// <param name="Sharpness">Schärfe 0..1 (logarithmisch aus <paramref name="LaplaceVariance"/>).</param>
/// <param name="Exposure">Belichtung 0..1 (1 = gut).</param>
/// <param name="LaplaceVariance">Mittel der Laplace-Varianz der schärfsten Kacheln (Helligkeit 0–255).</param>
/// <param name="MeanLuma">Mittlere Helligkeit 0..1.</param>
/// <param name="Clipped">Anteil ausgefressener Pixel (Helligkeit ≥ 250).</param>
/// <param name="Crushed">Anteil abgesoffener Pixel (Helligkeit ≤ 5).</param>
public readonly record struct ImageQualityResult(
    double Sharpness,
    double Exposure,
    double LaplaceVariance,
    double MeanLuma,
    double Clipped,
    double Crushed);

/// <summary>
/// Foto-Güte aus Pixeln (ADR-0057), rein rechnerisch und ohne Modell. Eingang: RGB24 des Vorschaubilds, auf längste Kante
/// <see cref="LoadEdge"/> geladen (also in fast derselben Auflösung, in der das Vorschaubild gespeichert ist).
/// <para>
/// <b>Schärfe</b>: Laplace-Operator (4er-Nachbarschaft) auf der Helligkeit (BT.601), Varianz je Kachel eines 4 × 4-Rasters,
/// davon das Mittel der <see cref="TopTiles"/> schärfsten Kacheln — ein Porträt mit unscharfem Hintergrund zählt so nach
/// seinem scharfen Gesicht, ein verwackeltes Foto bleibt überall weich. Normiert logarithmisch zwischen
/// <see cref="SharpnessLow"/> (→ 0) und <see cref="SharpnessHigh"/> (→ 1).
/// </para>
/// <para>
/// <b>Belichtung</b>: mittlere Helligkeit nahe der Mitte (Glockenkurve um <see cref="TargetLuma"/>), abzüglich eines Abschlags
/// für ausgefressene (≥ 250) und abgesoffene (≤ 5) Pixel über einer kleinen Toleranz.
/// </para>
/// </summary>
public static class ImageQuality
{
    /// <summary>Version des Verfahrens (<c>FileQuality.Version</c>); ändert sie sich, wird neu berechnet.</summary>
    public const int Version = 1;

    /// <summary>Längere Kante beim Laden (Vorschaubild: kurze Kante 400 → lange ≈ 530; Originale werden verkleinert).</summary>
    public const int LoadEdge = 512;

    /// <summary>Laplace-Varianz, die als „ganz unscharf“ gilt (Schärfe 0).</summary>
    public const double SharpnessLow = 5;

    /// <summary>Laplace-Varianz, ab der ein Foto als „ganz scharf“ gilt (Schärfe 1).</summary>
    public const double SharpnessHigh = 8000;

    /// <summary>Ideale mittlere Helligkeit (0..1).</summary>
    public const double TargetLuma = 0.46;

    private const int Grid = 4;
    private const int TopTiles = 4;
    private const double LumaSpread = 0.2;
    private const double ClippedTolerance = 0.02;
    private const double CrushedTolerance = 0.05;
    private const double ClipWeight = 1.5;

    /// <summary>Güte aus RGB24-Pixeln (Zeile für Zeile, 3 Bytes je Pixel).</summary>
    public static ImageQualityResult Analyze(ReadOnlySpan<byte> rgb, int width, int height)
    {
        if (width <= 0 || height <= 0 || rgb.Length < width * height * 3)
        {
            throw new ArgumentException($"RGB-Puffer passt nicht zu {width}×{height}.", nameof(rgb));
        }

        // Helligkeit BT.601 in Festkomma (77/150/29 von 256) — ganzzahlig ist auch im Debug-Build schnell.
        var luma = new int[width * height];
        long sum = 0;
        int clipped = 0, crushed = 0;
        for (int i = 0, p = 0; i < luma.Length; i++, p += 3)
        {
            var y = ((77 * rgb[p]) + (150 * rgb[p + 1]) + (29 * rgb[p + 2])) >> 8;
            luma[i] = y;
            sum += y;
            if (y >= 250)
            {
                clipped++;
            }
            else if (y <= 5)
            {
                crushed++;
            }
        }

        var count = (double)luma.Length;
        var mean = sum / count / 255.0;
        var clippedShare = clipped / count;
        var crushedShare = crushed / count;
        var variance = LaplaceVariance(luma, width, height);
        return new ImageQualityResult(
            NormalizeSharpness(variance),
            ExposureScore(mean, clippedShare, crushedShare),
            variance,
            mean,
            clippedShare,
            crushedShare);
    }

    /// <summary>Schärfe 0..1 aus der Laplace-Varianz (logarithmisch zwischen <see cref="SharpnessLow"/> und <see cref="SharpnessHigh"/>).</summary>
    public static double NormalizeSharpness(double laplaceVariance)
    {
        var low = Math.Log(1 + SharpnessLow);
        var high = Math.Log(1 + SharpnessHigh);
        return Math.Clamp((Math.Log(1 + Math.Max(0, laplaceVariance)) - low) / (high - low), 0, 1);
    }

    /// <summary>Belichtung 0..1 aus mittlerer Helligkeit (0..1) und den Anteilen ausgefressener/abgesoffener Pixel.</summary>
    public static double ExposureScore(double meanLuma, double clippedShare, double crushedShare)
    {
        var offset = (meanLuma - TargetLuma) / LumaSpread;
        var level = Math.Exp(-0.5 * offset * offset);
        var penalty = (ClipWeight * Math.Max(0, clippedShare - ClippedTolerance)) + (ClipWeight * Math.Max(0, crushedShare - CrushedTolerance));
        return Math.Clamp(level * (1 - Math.Min(1, penalty)), 0, 1);
    }

    /// <summary>Mittel der Laplace-Varianz der <see cref="TopTiles"/> schärfsten Kacheln (Raster <see cref="Grid"/> × <see cref="Grid"/>).</summary>
    private static double LaplaceVariance(int[] luma, int width, int height)
    {
        if (width < 3 || height < 3)
        {
            return 0;
        }

        var tiles = Grid * Grid;
        var sums = new long[tiles];
        var squares = new long[tiles];
        var counts = new long[tiles];
        var column = new int[width];
        for (var x = 0; x < width; x++)
        {
            column[x] = Math.Min(Grid - 1, x * Grid / width);
        }

        for (var y = 1; y < height - 1; y++)
        {
            var tileRow = Math.Min(Grid - 1, y * Grid / height) * Grid;
            var row = y * width;
            for (var x = 1; x < width - 1; x++)
            {
                var i = row + x;
                long l = (4 * luma[i]) - luma[i - 1] - luma[i + 1] - luma[i - width] - luma[i + width];
                var t = tileRow + column[x];
                sums[t] += l;
                squares[t] += l * l;
                counts[t]++;
            }
        }

        var variances = new List<double>(tiles);
        for (var t = 0; t < tiles; t++)
        {
            if (counts[t] > 0)
            {
                var m = sums[t] / (double)counts[t];
                variances.Add(Math.Max(0, (squares[t] / (double)counts[t]) - (m * m)));
            }
        }

        if (variances.Count == 0)
        {
            return 0;
        }

        variances.Sort();
        var take = Math.Min(TopTiles, variances.Count);
        double top = 0;
        for (var k = variances.Count - take; k < variances.Count; k++)
        {
            top += variances[k];
        }

        return top / take;
    }
}
