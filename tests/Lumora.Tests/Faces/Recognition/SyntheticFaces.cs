using Lumora.Services.Faces.Recognition;

namespace Lumora.Tests.Faces.Recognition;

/// <summary>Ein synthetisches Gesicht mit Wahrheit (Person, Alter im Verlauf, schlechte Qualität).</summary>
internal sealed record SyntheticFace(long FaceId, float[] Embedding, float Quality, int PersonId, float Age, bool LowQuality)
{
    public FaceSample Unconfirmed() => new(FaceId, Embedding, Quality);

    public FaceSample ConfirmedAs(int personId) => new(FaceId, Embedding, Quality, personId);

    public FaceSample Confirmed() => ConfirmedAs(PersonId);
}

/// <summary>
/// Synthetische Fingerabdrücke auf der Einheitskugel, die die ArcFace-Statistik nachbilden (ADR-0036, Spike 10):
/// <list type="bullet">
/// <item>gleiche Person: Paar-Kosinus ~0,5–0,8 (jedes Gesicht hat Kosinus c ∈ [0,72; 0,90] zum Personen-Zentrum,
/// Paar ≈ c₁·c₂, weil die Rauschanteile in 512 Dimensionen fast orthogonal sind);</item>
/// <item>verschiedene Personen: ~0,0–0,25 (Personen teilen sich zu 30 % eine von mehreren „Gruppen-Richtungen“,
/// sonst ~0);</item>
/// <item>Doppelgänger-Paare: Zentren mit Kosinus ~0,62 → Gesichter ~0,35–0,45;</item>
/// <item>Alterung: das Zentrum dreht sich mit dem Alter t ∈ [0, 1] um bis zu ~50° (Anfang ↔ Ende ~0,64);</item>
/// <item>schlechte Qualität: Kosinus 0,3–0,55 zum Zentrum, Qualität 0,05–0,35 (sonst 0,5–1,0).</item>
/// </list>
/// Vollständig deterministisch über die Saat.
/// </summary>
internal sealed class SyntheticFaceWorld
{
    private readonly Random _random;
    private readonly int _dimension;
    private readonly List<float[]> _groupDirections = [];
    private readonly Dictionary<int, Person> _persons = [];
    private long _nextFaceId;

    public SyntheticFaceWorld(int seed, int dimension = 512, int groups = 6, long firstFaceId = 1)
    {
        _random = new Random(seed);
        _dimension = dimension;
        _nextFaceId = firstFaceId;
        for (var g = 0; g < groups; g++)
        {
            _groupDirections.Add(RandomUnit());
        }
    }

    public int Dimension => _dimension;

    public float LowQualityFraction { get; init; } = 0.05f;

    public float MinCos { get; init; } = 0.72f;

    public float MaxCos { get; init; } = 0.90f;

    /// <summary>Legt eine Person an; optional als Doppelgänger einer vorhandenen und/oder mit Alterungsverlauf.</summary>
    public void AddPerson(int personId, int? lookalikeOf = null, bool aging = false, float lookalikeRho = 0.62f)
    {
        float[] center;
        if (lookalikeOf is { } other)
        {
            center = Mix(_persons[other].Center, lookalikeRho, Orthogonalize(RandomUnit(), _persons[other].Center), MathF.Sqrt(1 - lookalikeRho * lookalikeRho));
        }
        else
        {
            var group = _groupDirections[_random.Next(_groupDirections.Count)];
            center = Mix(group, MathF.Sqrt(0.3f), RandomUnit(), MathF.Sqrt(0.7f));
        }

        float[]? drift = null;
        if (aging)
        {
            drift = Orthogonalize(RandomUnit(), center);
        }

        _persons[personId] = new Person(center, drift);
    }

    /// <summary>Erzeugt Gesichter einer Person (Alter gleichmäßig in [ageFrom, ageTo]).</summary>
    public List<SyntheticFace> Faces(int personId, int count, float ageFrom = 0f, float ageTo = 1f, bool allowLowQuality = true)
    {
        var result = new List<SyntheticFace>(count);
        for (var i = 0; i < count; i++)
        {
            var age = count == 1 ? ageFrom : ageFrom + (ageTo - ageFrom) * i / (count - 1);
            var low = allowLowQuality && _random.NextDouble() < LowQualityFraction;
            result.Add(Face(personId, age, low));
        }

        return result;
    }

    public SyntheticFace Face(int personId, float age = 0.5f, bool lowQuality = false, float? cosToCenter = null)
    {
        var center = CenterAt(personId, age);
        var c = cosToCenter ?? (lowQuality ? Uniform(0.3f, 0.55f) : Uniform(MinCos, MaxCos));
        var embedding = Mix(center, c, Orthogonalize(RandomUnit(), center), MathF.Sqrt(1 - c * c));
        var quality = lowQuality ? Uniform(0.05f, 0.35f) : Uniform(0.5f, 1f);
        return new SyntheticFace(_nextFaceId++, embedding, quality, personId, age, lowQuality);
    }

    /// <summary>Ein Gesicht genau zwischen zwei Personen (Brücke).</summary>
    public SyntheticFace Bridge(int personA, int personB, float quality = 0.9f)
    {
        var a = CenterAt(personA, 0.5f);
        var b = CenterAt(personB, 0.5f);
        var mid = Mix(a, 1f, b, 1f);
        var embedding = Mix(mid, 0.85f, Orthogonalize(RandomUnit(), mid), MathF.Sqrt(1 - 0.85f * 0.85f));
        return new SyntheticFace(_nextFaceId++, embedding, quality, -1, 0.5f, false);
    }

    public float[] CenterAt(int personId, float age)
    {
        var person = _persons[personId];
        if (person.Drift is null)
        {
            return person.Center;
        }

        const float maxAngle = 50f * MathF.PI / 180f;
        var angle = maxAngle * age;
        return Mix(person.Center, MathF.Cos(angle), person.Drift, MathF.Sin(angle));
    }

    public static float Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return (float)(dot / Math.Sqrt(na * nb));
    }

    private float Uniform(float min, float max) => min + (float)_random.NextDouble() * (max - min);

    private float[] RandomUnit()
    {
        var v = new float[_dimension];
        for (var i = 0; i < v.Length; i += 2)
        {
            // Box-Muller
            var u1 = 1.0 - _random.NextDouble();
            var u2 = _random.NextDouble();
            var r = Math.Sqrt(-2.0 * Math.Log(u1));
            v[i] = (float)(r * Math.Cos(2 * Math.PI * u2));
            if (i + 1 < v.Length)
            {
                v[i + 1] = (float)(r * Math.Sin(2 * Math.PI * u2));
            }
        }

        return Normalize(v);
    }

    private static float[] Mix(float[] a, float wa, float[] b, float wb)
    {
        var v = new float[a.Length];
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = wa * a[i] + wb * b[i];
        }

        return Normalize(v);
    }

    private static float[] Orthogonalize(float[] v, float[] against)
    {
        double dot = 0;
        for (var i = 0; i < v.Length; i++)
        {
            dot += v[i] * against[i];
        }

        var result = new float[v.Length];
        for (var i = 0; i < v.Length; i++)
        {
            result[i] = v[i] - (float)dot * against[i];
        }

        return Normalize(result);
    }

    private static float[] Normalize(float[] v)
    {
        double sum = 0;
        foreach (var x in v)
        {
            sum += x * x;
        }

        var inv = (float)(1 / Math.Sqrt(sum));
        for (var i = 0; i < v.Length; i++)
        {
            v[i] *= inv;
        }

        return v;
    }

    private sealed record Person(float[] Center, float[]? Drift);
}
