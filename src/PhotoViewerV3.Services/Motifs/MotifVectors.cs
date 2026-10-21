using System.Buffers.Binary;
using System.Numerics;

namespace PhotoViewerV3.Services.Motifs;

/// <summary>
/// Vektoren der Motiv-Erkennung (ADR-0052): float32 little-endian als BLOB (<c>FileEmbeddings.Embedding</c>,
/// <c>MotifTerms.TextEmbedding</c>), L2-Normierung und Skalarprodukt (= Kosinus bei normierten Vektoren) über
/// <see cref="Vector{T}"/>.
/// </summary>
public static class MotifVectors
{
    public static byte[] Encode(ReadOnlySpan<float> vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), vector[i]);
        }

        return bytes;
    }

    /// <summary>
    /// Bild-Vektor als float16 (2 Bytes je Wert, 768 → 1 536 Bytes): halbiert den Katalog (500 000 Fotos ≈ 0,8 statt
    /// 1,6 GB); der Kosinus ändert sich dadurch um weniger als 0,001 (Test).
    /// </summary>
    public static byte[] EncodeHalf(ReadOnlySpan<float> vector)
    {
        var bytes = new byte[vector.Length * 2];
        for (var i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(i * 2), (Half)vector[i]);
        }

        return bytes;
    }

    public static float[] DecodeHalf(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % 2 != 0)
        {
            throw new InvalidDataException($"Vektor mit {bytes.Length} Bytes ist kein float16-Feld.");
        }

        var vector = new float[bytes.Length / 2];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)BinaryPrimitives.ReadHalfLittleEndian(bytes.Slice(i * 2));
        }

        return vector;
    }

    public static float[] Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % sizeof(float) != 0)
        {
            throw new InvalidDataException($"Vektor mit {bytes.Length} Bytes ist kein float32-Feld.");
        }

        var vector = new float[bytes.Length / sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(i * sizeof(float)));
        }

        return vector;
    }

    /// <summary>Normiert auf Länge 1 (Nullvektor bleibt unverändert).</summary>
    public static void NormalizeL2(Span<float> vector)
    {
        var norm = MathF.Sqrt(Dot(vector, vector));
        if (norm <= 0 || float.IsNaN(norm))
        {
            return;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] /= norm;
        }
    }

    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException("Vektoren unterschiedlicher Länge.");
        }

        var sum = 0f;
        var i = 0;
        if (Vector.IsHardwareAccelerated && a.Length >= Vector<float>.Count)
        {
            var acc = Vector<float>.Zero;
            for (; i <= a.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                acc += new Vector<float>(a.Slice(i)) * new Vector<float>(b.Slice(i));
            }

            sum = Vector.Dot(acc, Vector<float>.One);
        }

        for (; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    /// <summary>Mittelwert mehrerer Vektoren, danach normiert (mehrere Vorlagen je Begriff).</summary>
    public static float[] MeanNormalized(IReadOnlyList<float[]> vectors)
    {
        if (vectors.Count == 0)
        {
            throw new ArgumentException("Mindestens ein Vektor nötig.", nameof(vectors));
        }

        var mean = new float[vectors[0].Length];
        foreach (var vector in vectors)
        {
            for (var i = 0; i < mean.Length; i++)
            {
                mean[i] += vector[i];
            }
        }

        NormalizeL2(mean);
        return mean;
    }
}
