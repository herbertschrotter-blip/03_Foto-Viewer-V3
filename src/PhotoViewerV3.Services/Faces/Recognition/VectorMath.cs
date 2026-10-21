using System.Numerics;
using System.Runtime.InteropServices;

namespace PhotoViewerV3.Services.Faces.Recognition;

/// <summary>
/// SIMD-Grundoperationen auf float-Vektoren (<see cref="Vector{T}"/>; System.Numerics.Tensors ist nicht referenziert).
/// Reihenfolge der Summation ist fest — Ergebnisse sind auf derselben Maschine bitgenau reproduzierbar.
/// </summary>
internal static class VectorMath
{
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var length = a.Length;
        var i = 0;
        var sum = 0f;
        if (Vector.IsHardwareAccelerated && length >= Vector<float>.Count * 2)
        {
            var va = MemoryMarshal.Cast<float, Vector<float>>(a);
            var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
            var acc0 = Vector<float>.Zero;
            var acc1 = Vector<float>.Zero;
            var v = 0;
            for (; v + 1 < va.Length; v += 2)
            {
                acc0 += va[v] * vb[v];
                acc1 += va[v + 1] * vb[v + 1];
            }

            if (v < va.Length)
            {
                acc0 += va[v] * vb[v];
            }

            sum = Vector.Sum(acc0 + acc1);
            i = va.Length * Vector<float>.Count;
        }

        for (; i < length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    /// <summary><paramref name="target"/> += <paramref name="factor"/> · <paramref name="source"/>.</summary>
    public static void AddScaled(Span<float> target, ReadOnlySpan<float> source, float factor)
    {
        var length = target.Length;
        var i = 0;
        if (Vector.IsHardwareAccelerated && length >= Vector<float>.Count)
        {
            var vt = MemoryMarshal.Cast<float, Vector<float>>(target);
            var vs = MemoryMarshal.Cast<float, Vector<float>>(source);
            var f = new Vector<float>(factor);
            for (var v = 0; v < vt.Length; v++)
            {
                vt[v] += vs[v] * f;
            }

            i = vt.Length * Vector<float>.Count;
        }

        for (; i < length; i++)
        {
            target[i] += source[i] * factor;
        }
    }

    public static void Scale(Span<float> target, float factor)
    {
        var length = target.Length;
        var i = 0;
        if (Vector.IsHardwareAccelerated && length >= Vector<float>.Count)
        {
            var vt = MemoryMarshal.Cast<float, Vector<float>>(target);
            var f = new Vector<float>(factor);
            for (var v = 0; v < vt.Length; v++)
            {
                vt[v] *= f;
            }

            i = vt.Length * Vector<float>.Count;
        }

        for (; i < length; i++)
        {
            target[i] *= factor;
        }
    }

    /// <summary>L2-normiert <paramref name="source"/> nach <paramref name="target"/>; false bei Nullvektor, NaN oder ∞.</summary>
    public static bool TryNormalize(ReadOnlySpan<float> source, Span<float> target)
    {
        var squared = Dot(source, source);
        if (!(squared > 1e-12f) || float.IsInfinity(squared))
        {
            return false;
        }

        source.CopyTo(target);
        Scale(target, 1f / MathF.Sqrt(squared));
        return true;
    }

    /// <summary>Normiert in place; false bei Nullvektor (Inhalt bleibt dann unverändert).</summary>
    public static bool NormalizeInPlace(Span<float> vector)
    {
        var squared = Dot(vector, vector);
        if (!(squared > 1e-12f) || float.IsInfinity(squared))
        {
            return false;
        }

        Scale(vector, 1f / MathF.Sqrt(squared));
        return true;
    }

    public static float Sigmoid(float x) => x >= 0 ? 1f / (1f + MathF.Exp(-x)) : MathF.Exp(x) / (1f + MathF.Exp(x));

    /// <summary>Median einer (unsortierten) Liste; sortiert eine Kopie.</summary>
    public static float Median(ReadOnlySpan<float> values)
    {
        if (values.IsEmpty)
        {
            return 0f;
        }

        var copy = values.ToArray();
        Array.Sort(copy);
        var mid = copy.Length / 2;
        return copy.Length % 2 == 1 ? copy[mid] : (copy[mid - 1] + copy[mid]) / 2f;
    }

    /// <summary>Median der absoluten Abweichungen vom Median (MAD).</summary>
    public static float MedianAbsoluteDeviation(ReadOnlySpan<float> values, float median)
    {
        if (values.IsEmpty)
        {
            return 0f;
        }

        var deviations = new float[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            deviations[i] = MathF.Abs(values[i] - median);
        }

        return Median(deviations);
    }
}
