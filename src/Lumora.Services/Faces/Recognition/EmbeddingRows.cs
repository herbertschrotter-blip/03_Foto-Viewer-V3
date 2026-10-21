namespace Lumora.Services.Faces.Recognition;

/// <summary>
/// Zusammenhängende Matrix normierter Fingerabdrücke (Zeile = Gesicht) — cache-freundlich für die SIMD-Schleifen.
/// </summary>
internal sealed class EmbeddingRows
{
    private EmbeddingRows(float[] data, int dimension, int count)
    {
        Data = data;
        Dimension = dimension;
        Count = count;
    }

    public float[] Data { get; }

    public int Dimension { get; }

    public int Count { get; }

    public ReadOnlySpan<float> Row(int index) => new(Data, index * Dimension, Dimension);

    /// <summary>Erste nicht leere Embedding-Länge; 0, wenn es keine gibt.</summary>
    public static int ResolveDimension(IReadOnlyList<FaceSample> samples)
    {
        foreach (var sample in samples)
        {
            if (sample is not null && sample.Embedding.Length > 0)
            {
                return sample.Embedding.Length;
            }
        }

        return 0;
    }

    /// <summary>
    /// Übernimmt die Samples, für die <paramref name="accept"/> gilt und deren Fingerabdruck gültig ist (Länge
    /// <paramref name="dimension"/>, normierbar). <paramref name="acceptedIndices"/> enthält die Indizes in
    /// <paramref name="samples"/> in Matrix-Reihenfolge; <paramref name="order"/> legt diese Reihenfolge fest.
    /// </summary>
    public static EmbeddingRows Build(
        IReadOnlyList<FaceSample> samples,
        IReadOnlyList<int> order,
        int dimension,
        Func<FaceSample, bool> accept,
        out List<int> acceptedIndices,
        out List<int> rejectedIndices)
    {
        acceptedIndices = new List<int>(order.Count);
        rejectedIndices = [];
        var data = new float[(long)order.Count * dimension];
        var row = 0;
        foreach (var index in order)
        {
            var sample = samples[index];
            if (!accept(sample) || sample.Embedding.Length != dimension
                || !VectorMath.TryNormalize(sample.Embedding.Span, data.AsSpan(row * dimension, dimension)))
            {
                rejectedIndices.Add(index);
                continue;
            }

            acceptedIndices.Add(index);
            row++;
        }

        if (row < order.Count)
        {
            Array.Resize(ref data, row * dimension);
        }

        return new EmbeddingRows(data, dimension, row);
    }

    /// <summary>Reihenfolge nach FaceId (stabil) — macht das Ergebnis unabhängig von der Eingabe-Reihenfolge.</summary>
    public static int[] OrderByFaceId(IReadOnlyList<FaceSample> samples)
    {
        var order = new int[samples.Count];
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        var keys = new long[samples.Count];
        for (var i = 0; i < keys.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(samples[i], nameof(samples));
            keys[i] = samples[i].FaceId;
        }

        // Stabil sortieren: Schlüssel (FaceId, Eingabe-Index).
        Array.Sort(order, (a, b) =>
        {
            var c = keys[a].CompareTo(keys[b]);
            return c != 0 ? c : a.CompareTo(b);
        });
        return order;
    }
}
