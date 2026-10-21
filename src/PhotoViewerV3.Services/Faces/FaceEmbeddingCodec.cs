using System.Buffers.Binary;

namespace PhotoViewerV3.Services.Faces;

/// <summary>
/// Kodierung der Gesichts-Fingerabdrücke für <c>Faces.Embedding</c> (ADR-0035, Schnittstelle zur Lern-/Gruppierlogik):
/// <see cref="Dimensions"/> float32, little-endian, ohne Kopf — 2048 Bytes. Die Vektoren sind L2-normiert (ArcFace
/// w600k_r50), die Kosinus-Ähnlichkeit zweier Fingerabdrücke ist damit ihr Skalarprodukt (<see cref="Cosine"/>).
/// Gleiches Format für <c>Faces.Landmarks</c> (10 float32, <see cref="EncodeFloats"/>).
/// </summary>
public static class FaceEmbeddingCodec
{
    /// <summary>Länge eines Fingerabdrucks (ArcFace w600k_r50).</summary>
    public const int Dimensions = 512;

    /// <summary>Bytes eines kodierten Fingerabdrucks.</summary>
    public const int ByteLength = Dimensions * sizeof(float);

    /// <summary>float[512] → 2048 Bytes (little-endian float32).</summary>
    public static byte[] Encode(ReadOnlySpan<float> embedding)
    {
        if (embedding.Length != Dimensions)
        {
            throw new ArgumentException($"Ein Fingerabdruck hat {Dimensions} Werte, nicht {embedding.Length}.", nameof(embedding));
        }

        return EncodeFloats(embedding);
    }

    /// <summary>2048 Bytes → float[512]; wirft bei falscher Länge.</summary>
    public static float[] Decode(ReadOnlySpan<byte> blob)
    {
        if (blob.Length != ByteLength)
        {
            throw new ArgumentException($"Ein kodierter Fingerabdruck hat {ByteLength} Bytes, nicht {blob.Length}.", nameof(blob));
        }

        return DecodeFloats(blob);
    }

    /// <summary>Beliebig viele float32 little-endian (z. B. die 10 Landmarken-Koordinaten).</summary>
    public static byte[] EncodeFloats(ReadOnlySpan<float> values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), values[i]);
        }

        return bytes;
    }

    /// <summary>Gegenstück zu <see cref="EncodeFloats"/>; die Länge muss ein Vielfaches von 4 sein.</summary>
    public static float[] DecodeFloats(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % sizeof(float) != 0)
        {
            throw new ArgumentException("Die Länge ist kein Vielfaches von 4 Bytes.", nameof(bytes));
        }

        var values = new float[bytes.Length / sizeof(float)];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes[(i * sizeof(float))..]);
        }

        return values;
    }

    /// <summary>Kosinus-Ähnlichkeit zweier Vektoren gleicher Länge (bei L2-normierten = Skalarprodukt).</summary>
    public static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException("Die Vektoren sind unterschiedlich lang.");
        }

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0f : (float)(dot / Math.Sqrt(na * nb));
    }

    /// <summary>Normiert den Vektor auf Länge 1 (in place); ein Nullvektor bleibt unverändert.</summary>
    public static void NormalizeL2(Span<float> values)
    {
        double sum = 0;
        foreach (var v in values)
        {
            sum += v * v;
        }

        if (sum <= 0)
        {
            return;
        }

        var inv = (float)(1.0 / Math.Sqrt(sum));
        for (var i = 0; i < values.Length; i++)
        {
            values[i] *= inv;
        }
    }
}
