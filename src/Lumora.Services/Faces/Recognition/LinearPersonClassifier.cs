namespace Lumora.Services.Faces.Recognition;

/// <summary>
/// Linearer Klassifikator je Person (One-vs-Rest, logistische Regression per SGD) auf normierten Fingerabdrücken —
/// bewusste Eigenimplementierung statt ML.NET (ADR-0036).
/// <list type="bullet">
/// <item>Positiv: alle Lernbeispiele der Person. Negativ: höchstens <see cref="FaceRecognizerOptions.MaxNegatives"/>
/// Beispiele anderer Personen — die Hälfte von den Personen mit den ähnlichsten Zentren (schwierige Gegenbeispiele),
/// der Rest zufällig (feste Saat je Person).</item>
/// <item>Ausgewogen: jede Epoche wechselt Positiv/Negativ ab, Positive werden zyklisch wiederholt — auch eine Person mit
/// einem einzigen Beispiel bekommt so gleich viel Gewicht wie ihre Gegenbeispiele.</item>
/// <item>L2-Regularisierung über einen verzögerten Skalierungsfaktor (kein zusätzlicher Durchlauf je Schritt).</item>
/// </list>
/// </summary>
internal static class LinearPersonClassifier
{
    private const float LearningRate = 0.5f;

    /// <summary>Trainiert Gewichte und Bias einer Person; false, wenn es keine Gegenbeispiele gibt.</summary>
    public static bool Train(
        EmbeddingRows rows,
        int personIndex,
        int[] personStart,
        int[] personCount,
        int[] nearestPersons,
        FaceRecognizerOptions options,
        int personId,
        Span<float> weights,
        out float bias)
    {
        bias = 0f;
        weights.Clear();
        var totalOthers = rows.Count - personCount[personIndex];
        if (totalOthers <= 0)
        {
            return false;
        }

        var random = new Random(unchecked(options.Seed ^ (personId * (int)0x9E3779B1)));
        var negatives = SelectNegatives(rows.Count, personIndex, personStart, personCount, nearestPersons, options, random);

        var positives = new int[personCount[personIndex]];
        for (var i = 0; i < positives.Length; i++)
        {
            positives[i] = personStart[personIndex] + i;
        }

        var stepsPerEpoch = Math.Max(negatives.Length, positives.Length);
        var dimension = rows.Dimension;
        var direction = new float[dimension]; // Gewichte = scale · direction
        var scale = 1f;
        var b = 0f;
        for (var epoch = 0; epoch < options.Epochs; epoch++)
        {
            Shuffle(positives, random);
            Shuffle(negatives, random);
            var rate = LearningRate / (1f + epoch);
            for (var s = 0; s < stepsPerEpoch; s++)
            {
                Update(rows.Row(positives[s % positives.Length]), 1f);
                Update(rows.Row(negatives[s % negatives.Length]), 0f);
            }

            void Update(ReadOnlySpan<float> x, float label)
            {
                var logit = scale * VectorMath.Dot(direction, x) + b;
                var gradient = label - VectorMath.Sigmoid(logit);
                scale *= 1f - rate * options.L2;
                if (scale < 1e-3f)
                {
                    VectorMath.Scale(direction, scale);
                    scale = 1f;
                }

                if (gradient != 0f)
                {
                    VectorMath.AddScaled(direction, x, rate * gradient / scale);
                    b += rate * gradient;
                }
            }
        }

        direction.AsSpan().CopyTo(weights);
        VectorMath.Scale(weights, scale);
        bias = b;
        return true;
    }

    private static int[] SelectNegatives(
        int total,
        int personIndex,
        int[] personStart,
        int[] personCount,
        int[] nearestPersons,
        FaceRecognizerOptions options,
        Random random)
    {
        var others = total - personCount[personIndex];
        var max = Math.Max(1, options.MaxNegatives);
        if (others <= max)
        {
            var all = new int[others];
            var w = 0;
            for (var i = 0; i < total; i++)
            {
                if (i < personStart[personIndex] || i >= personStart[personIndex] + personCount[personIndex])
                {
                    all[w++] = i;
                }
            }

            return all;
        }

        var chosen = new HashSet<int>();
        var hardBudget = max / 2;
        foreach (var other in nearestPersons)
        {
            if (chosen.Count >= hardBudget)
            {
                break;
            }

            // Von jeder nahen Person höchstens ein Viertel des Budgets, damit mehrere Nachbarn vertreten sind.
            var take = Math.Min(personCount[other], Math.Max(1, hardBudget / 4));
            take = Math.Min(take, hardBudget - chosen.Count);
            var start = personStart[other];
            var count = personCount[other];
            for (var t = 0; t < take; t++)
            {
                chosen.Add(start + (count == take ? t : random.Next(count)));
            }
        }

        var guard = 0;
        while (chosen.Count < max && guard++ < max * 20)
        {
            var candidate = random.Next(total);
            if (candidate >= personStart[personIndex] && candidate < personStart[personIndex] + personCount[personIndex])
            {
                continue;
            }

            chosen.Add(candidate);
        }

        var result = chosen.ToArray();
        Array.Sort(result);
        return result;
    }

    private static void Shuffle(int[] values, Random random)
    {
        for (var i = values.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }
}
