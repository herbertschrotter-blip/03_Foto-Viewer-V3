namespace PhotoViewerV3.Services.Faces.Alignment;

/// <summary>
/// Ähnlichkeitstransformation (Drehung, gleichmäßige Skalierung, Verschiebung — keine Spiegelung) als 2×3-Matrix
/// <c>[[A, −B, Tx], [B, A, Ty]]</c>: <c>x' = A·x − B·y + Tx</c>, <c>y' = B·x + A·y + Ty</c>.
/// </summary>
public readonly record struct SimilarityTransform(float A, float B, float Tx, float Ty)
{
    public float Scale => MathF.Sqrt(A * A + B * B);

    /// <summary>Drehwinkel in Grad (gegen den Uhrzeigersinn in mathematischer Lage).</summary>
    public float RotationDegrees => MathF.Atan2(B, A) * 180f / MathF.PI;

    public FacePoint Apply(FacePoint p) => new(A * p.X - B * p.Y + Tx, B * p.X + A * p.Y + Ty);

    /// <summary>Umkehrabbildung (Zielpixel → Quellpixel).</summary>
    public SimilarityTransform Inverse()
    {
        var d = A * A + B * B;
        var ia = A / d;
        var ib = -B / d;
        // x = ia·(x' − Tx) − ib·(y' − Ty) …
        return new SimilarityTransform(ia, ib, -(ia * Tx - ib * Ty), -(ib * Tx + ia * Ty));
    }
}

/// <summary>
/// Ausrichtung eines Gesichts für ArcFace wie InsightFace <c>face_align.norm_crop</c> (ADR-0035): Ähnlichkeits-
/// transformation der 5 Landmarken auf die Referenzpunkte <see cref="ArcFaceReference"/> (112 × 112) nach der
/// Methode der kleinsten Quadrate (gleiches Optimum wie Umeyama/<c>skimage.SimilarityTransform.estimate</c>), dann
/// bilineares Rückwärts-Abtasten (<c>cv2.warpAffine</c>, INTER_LINEAR, Rand schwarz).
/// </summary>
public static class FaceAligner
{
    public const int CropSize = 112;

    /// <summary>InsightFace <c>arcface_dst</c> für 112 × 112.</summary>
    public static readonly FacePoint[] ArcFaceReference =
    [
        new(38.2946f, 51.6963f),
        new(73.5318f, 51.5014f),
        new(56.0252f, 71.7366f),
        new(41.5493f, 92.3655f),
        new(70.7299f, 92.2041f),
    ];

    /// <summary>Schätzt die Ähnlichkeitstransformation <paramref name="source"/> → <paramref name="target"/> (gleiche Punktzahl ≥ 2).</summary>
    public static SimilarityTransform Estimate(IReadOnlyList<FacePoint> source, IReadOnlyList<FacePoint> target)
    {
        if (source.Count != target.Count || source.Count < 2)
        {
            throw new ArgumentException("Es werden gleich viele (mindestens zwei) Punkte gebraucht.");
        }

        double smx = 0, smy = 0, tmx = 0, tmy = 0;
        var n = source.Count;
        for (var i = 0; i < n; i++)
        {
            smx += source[i].X;
            smy += source[i].Y;
            tmx += target[i].X;
            tmy += target[i].Y;
        }

        smx /= n;
        smy /= n;
        tmx /= n;
        tmy /= n;

        double sxx = 0, num_a = 0, num_b = 0;
        for (var i = 0; i < n; i++)
        {
            var px = source[i].X - smx;
            var py = source[i].Y - smy;
            var qx = target[i].X - tmx;
            var qy = target[i].Y - tmy;
            sxx += px * px + py * py;
            num_a += px * qx + py * qy;
            num_b += px * qy - py * qx;
        }

        if (sxx <= 1e-12)
        {
            throw new ArgumentException("Die Quellpunkte fallen zusammen.");
        }

        var a = num_a / sxx;
        var b = num_b / sxx;
        var tx = tmx - (a * smx - b * smy);
        var ty = tmy - (b * smx + a * smy);
        return new SimilarityTransform((float)a, (float)b, (float)tx, (float)ty);
    }

    /// <summary>
    /// Schneidet das ausgerichtete Gesicht aus (RGB, <see cref="CropSize"/>² × 3 Bytes, gerundet wie OpenCV).
    /// <paramref name="transform"/> bildet Bildpixel auf Ausschnittpixel ab.
    /// </summary>
    public static byte[] Warp(FaceImage image, SimilarityTransform transform, int size = CropSize)
    {
        ArgumentNullException.ThrowIfNull(image);
        var inv = transform.Inverse();
        var src = image.Pixels;
        var w = image.Width;
        var h = image.Height;
        var dst = new byte[size * size * 3];
        Span<float> acc = stackalloc float[3];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var sx = inv.A * x - inv.B * y + inv.Tx;
                var sy = inv.B * x + inv.A * y + inv.Ty;
                var x0 = (int)MathF.Floor(sx);
                var y0 = (int)MathF.Floor(sy);
                var fx = sx - x0;
                var fy = sy - y0;
                acc.Clear();
                Accumulate(src, w, h, x0, y0, (1 - fx) * (1 - fy), acc);
                Accumulate(src, w, h, x0 + 1, y0, fx * (1 - fy), acc);
                Accumulate(src, w, h, x0, y0 + 1, (1 - fx) * fy, acc);
                Accumulate(src, w, h, x0 + 1, y0 + 1, fx * fy, acc);
                var o = (y * size + x) * 3;
                dst[o] = ToByte(acc[0]);
                dst[o + 1] = ToByte(acc[1]);
                dst[o + 2] = ToByte(acc[2]);
            }
        }

        return dst;
    }

    /// <summary>Ausrichten in einem Schritt: Transformation aus den Landmarken, dann <see cref="Warp"/>.</summary>
    public static byte[] AlignCrop(FaceImage image, IReadOnlyList<FacePoint> landmarks)
        => Warp(image, Estimate(landmarks, ArcFaceReference));

    private static void Accumulate(byte[] src, int w, int h, int x, int y, float weight, Span<float> acc)
    {
        if (weight == 0 || x < 0 || y < 0 || x >= w || y >= h)
        {
            return; // Rand: schwarz (BORDER_CONSTANT 0)
        }

        var i = (y * w + x) * 3;
        acc[0] += src[i] * weight;
        acc[1] += src[i + 1] * weight;
        acc[2] += src[i + 2] * weight;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v), 0, 255);
}
