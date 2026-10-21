using System.Diagnostics;
using System.Globalization;
using System.Text;
using Lumora.Services.Faces.Recognition;
using Xunit.Abstractions;

namespace Lumora.Tests.Faces.Recognition;

/// <summary>
/// Bewertungs-Harness für Lernen &amp; Gruppieren (Spike 10, docs/spikes/10-gesichter-lernen.md) auf synthetischen
/// ArcFace-ähnlichen Daten. Größe über <c>LUMORA_FACELEARN_SCALE</c> (<c>small</c> = Standard im normalen Testlauf,
/// <c>full</c> = Messung für die Spike-Tabelle). <c>LUMORA_FACELEARN_OUT</c> = Datei, an die die Tabellen angehängt werden.
/// Im kleinen Lauf prüfen lockere Untergrenzen, dass Qualität und Tempo nicht einbrechen.
/// </summary>
public sealed class FaceLearningEvaluationTests(ITestOutputHelper output)
{
    private static readonly bool Full =
        string.Equals(Environment.GetEnvironmentVariable("LUMORA_FACELEARN_SCALE"), "full", StringComparison.OrdinalIgnoreCase);

    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    private readonly FaceClusterer _clusterer = new();
    private readonly FaceRecognizer _recognizer = new();

    [Fact]
    public void Synthetic_data_follows_ArcFace_statistics()
    {
        var world = new SyntheticFaceWorld(seed: 11) { LowQualityFraction = 0f };
        for (var p = 0; p < 40; p++)
        {
            world.AddPerson(p);
        }

        world.AddPerson(100, lookalikeOf: 0);
        world.AddPerson(101, aging: true);
        var faces = Enumerable.Range(0, 40).ToDictionary(p => p, p => world.Faces(p, 12));
        var look = world.Faces(100, 12);
        var young = world.Faces(101, 6, 0f, 0.1f);
        var old = world.Faces(101, 6, 0.9f, 1f);

        var same = new List<float>();
        var different = new List<float>();
        for (var p = 0; p < 40; p++)
        {
            for (var i = 0; i < 12; i++)
            {
                for (var j = i + 1; j < 12; j++)
                {
                    same.Add(SyntheticFaceWorld.Cosine(faces[p][i].Embedding, faces[p][j].Embedding));
                }

                for (var q = p + 1; q < 40; q++)
                {
                    different.Add(SyntheticFaceWorld.Cosine(faces[p][i].Embedding, faces[q][i].Embedding));
                }
            }
        }

        var lookalike = (from a in faces[0] from b in look select SyntheticFaceWorld.Cosine(a.Embedding, b.Embedding)).ToList();
        var aging = (from a in young from b in old select SyntheticFaceWorld.Cosine(a.Embedding, b.Embedding)).ToList();

        var report = new Report(output);
        report.Line("| Paare | p5 | Median | p95 |");
        report.Line("|---|---:|---:|---:|");
        report.Row("gleiche Person", same);
        report.Row("verschiedene Personen", different);
        report.Row("Doppelgänger-Paar", lookalike);
        report.Row("Alterung Anfang ↔ Ende", aging);
        report.Flush();

        Percentile(same, 0.05).Should().BeInRange(0.45f, 0.6f);
        Percentile(same, 0.95).Should().BeInRange(0.7f, 0.85f);
        Percentile(different, 0.95).Should().BeInRange(0.1f, 0.3f);
        Percentile(lookalike, 0.5).Should().BeInRange(0.33f, 0.47f);
    }

    [Fact]
    public void Clustering_quality_and_parameter_variants()
    {
        var report = new Report(output);
        ClusterMetrics? standard = null;
        foreach (var (worldName, share, rho) in new[] { ("normal", 0.1, 0.62f), ("schwierig", 0.3, 0.62f), ("Geschwister", 0.3, 0.75f) })
        {
            var (faces, _) = ClusterWorld(Full ? 300 : 40, seed: 21, share, rho);
            var metrics = RunClusterVariants(report, $"{worldName}, Doppelgänger-ρ {rho.ToString("0.00", De)}", faces, share);
            standard ??= metrics;
        }

        report.Flush();
        standard!.Purity.Should().BeGreaterThan(0.99);
        standard.Completeness.Should().BeGreaterThan(0.6);
    }

    private ClusterMetrics RunClusterVariants(Report report, string worldName, List<SyntheticFace> faces, double share)
    {
        var variants = new (string Name, FaceClusterOptions Options)[]
        {
            ("Standard (Merge 0,55, min 3)", FaceClusterOptions.Default),
            ("Merge 0,50", FaceClusterOptions.Default with { MergeSimilarity = 0.5f }),
            ("Merge 0,45", FaceClusterOptions.Default with { MergeSimilarity = 0.45f }),
            ("Merge 0,60", FaceClusterOptions.Default with { MergeSimilarity = 0.6f }),
            ("Mindestgröße 2", FaceClusterOptions.Default with { MinGroupSize = 2 }),
            ("ohne Qualitätsfilter", FaceClusterOptions.Default with { MinQuality = 0f }),
            ("Single Linkage ≈ DBSCAN (Kante 0,50, Merge 0)", FaceClusterOptions.Default with { EdgeSimilarity = 0.5f, MergeSimilarity = -1f }),
        };

        report.Line($"Gruppierung, Welt „{worldName}“: {faces.Count} Gesichter, {faces.Select(f => f.PersonId).Distinct().Count()} Personen ({share * 100:0} % Doppelgänger, 20 % Alterung, 5 % schlechte Qualität).");
        report.Line();
        report.Line("| Variante | Gruppen | gemischte Gruppen | Reinheit | Vollständigkeit | gruppiert | markiert | fremde markiert | Markierte fremd | Zeit |");
        report.Line("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        ClusterMetrics? standard = null;
        foreach (var (name, options) in variants)
        {
            var watch = Stopwatch.StartNew();
            var result = _clusterer.Cluster(faces.Select(f => f.Unconfirmed()).ToList(), options);
            watch.Stop();
            var m = ClusterMetrics.Compute(result, faces, options.MinGroupSize);
            standard ??= m;
            report.Line($"| {name} | {m.Groups} | {m.MixedGroups} | {P(m.Purity)} | {P(m.Completeness)} | {P(m.Grouped)} | {P(m.Flagged)} | {P(m.ForeignFlagged)} | {P(m.FlaggedForeign)} | {Ms(watch.Elapsed)} |");
        }

        report.Line();
        return standard!;
    }

    [Fact]
    public void Unknown_threshold_sweep()
    {
        var known = Full ? 200 : 30;
        var unknownPersons = Full ? 50 : 10;
        var report = new Report(output);
        report.Line($"Schwelle „unbekannt“ (k-NN-Ähnlichkeit) — {known} bekannte, {unknownPersons} unbekannte Personen, 20 % Doppelgänger.");
        report.Line();
        report.Line("| Beispiele | Schwelle | richtig | falsch | „unbekannt“ | Unbekannte fälschlich zugeordnet |");
        report.Line("|---:|---:|---:|---:|---:|---:|");
        foreach (var examples in new[] { 1, 3, 10 })
        {
            RecognitionWorld(known, unknownPersons, examples, seed: 71, out var train, out var queries, lookalikeShare: 0.2);
            foreach (var threshold in new[] { 0.36f, 0.40f, 0.42f, 0.45f, 0.50f })
            {
                var model = _recognizer.Train(train.Select(f => f.Confirmed()).ToList(), [],
                    FaceRecognizerOptions.Default with { UnknownSimilarity = threshold });
                var m = RecognitionMetrics.Compute(model, queries, known);
                report.Line($"| {examples} | {threshold.ToString("0.00", De)} | {P(m.Correct)} | {P(m.Wrong)} | {P(m.Unknown)} | {P(m.FalseAccept)} |");
            }
        }

        report.Flush();
    }

    [Fact]
    public void Recognition_accuracy_by_examples_unknown_and_rejections()
    {
        var known = Full ? 200 : 30;
        var unknownPersons = Full ? 50 : 10;
        var report = new Report(output);
        report.Line($"Zuordnung: {known} bekannte Personen, {unknownPersons} unbekannte, je 10 Anfragen (10 % Doppelgänger, 20 % Alterung, 5 % schlechte Qualität).");
        report.Line();
        report.Line("| Beispiele je Person | Verfahren | richtig | falsch | „unbekannt“ | Unbekannte fälschlich zugeordnet | nach Ablehnung richtig | nach Ablehnung „unbekannt“ | nach Ablehnung falsch |");
        report.Line("|---:|---|---:|---:|---:|---:|---:|---:|---:|");
        var accuracies = new Dictionary<(int, bool), double>();
        foreach (var examples in new[] { 1, 3, 10 })
        {
            var world = RecognitionWorld(known, unknownPersons, examples, seed: 31, out var train, out var queries);
            foreach (var linear in new[] { true, false })
            {
                var options = FaceRecognizerOptions.Default with { UseLinearClassifier = linear };
                var model = _recognizer.Train(train.Select(f => f.Confirmed()).ToList(), [], options);
                var m = RecognitionMetrics.Compute(model, queries, known);
                accuracies[(examples, linear)] = m.Correct;
                report.Line($"| {examples} | {(linear ? "Ensemble (k-NN + linear)" : "nur k-NN")} | {P(m.Correct)} | {P(m.Wrong)} | {P(m.Unknown)} | {P(m.FalseAccept)} | {P(m.AfterRejectCorrect)} | {P(m.AfterRejectUnknown)} | {P(m.AfterRejectWrong)} |");
            }

            _ = world;
        }

        // Geschwister-Welt: jede zweite Person ist Doppelgänger der vorigen (Zentren-Kosinus 0,75 bzw. 0,85) und beide sind
        // bekannt — hier entstehen echte Verwechslungen, und die Ablehnung muss den nächstbesten Kandidaten bringen.
        report.Line();
        report.Line("Geschwister-Welt: Paare mit Zentren-Kosinus ρ (Gesichter des Paars ≈ ρ · 0,65), beide Personen bekannt, keine schlechte Qualität.");
        report.Line();
        report.Line("| ρ | Beispiele je Person | Verfahren | richtig | falsch | „unbekannt“ | nach Ablehnung richtig | nach Ablehnung „unbekannt“ | nach Ablehnung falsch |");
        report.Line("|---:|---:|---|---:|---:|---:|---:|---:|---:|");
        foreach (var rho in new[] { 0.75f, 0.85f })
        {
            foreach (var examples in new[] { 1, 3, 10 })
            {
                var siblings = new SyntheticFaceWorld(seed: 37) { LowQualityFraction = 0f };
                var train = new List<SyntheticFace>();
                var queries = new List<SyntheticFace>();
                var random = new Random(37);
                for (var p = 0; p < known; p++)
                {
                    siblings.AddPerson(p, p % 2 == 1 ? p - 1 : null, lookalikeRho: rho);
                    var faces = siblings.Faces(p, examples + 10).OrderBy(_ => random.Next()).ToList();
                    train.AddRange(faces.Take(examples));
                    queries.AddRange(faces.Skip(examples));
                }

                foreach (var linear in new[] { true, false })
                {
                    var model = _recognizer.Train(train.Select(f => f.Confirmed()).ToList(), [],
                        FaceRecognizerOptions.Default with { UseLinearClassifier = linear });
                    var m = RecognitionMetrics.Compute(model, queries, known);
                    report.Line($"| {rho.ToString("0.00", De)} | {examples} | {(linear ? "Ensemble" : "nur k-NN")} | {P(m.Correct)} | {P(m.Wrong)} | {P(m.Unknown)} | {P(m.AfterRejectCorrect)} | {P(m.AfterRejectUnknown)} | {P(m.AfterRejectWrong)} |");
                }
            }
        }

        report.Flush();
        accuracies[(1, true)].Should().BeGreaterThan(0.6);
        accuracies[(10, true)].Should().BeGreaterThan(0.85);
        accuracies[(10, true)].Should().BeGreaterThanOrEqualTo(accuracies[(1, true)]);
    }

    [Fact]
    public void One_wrong_example_among_twenty_neighbors_vs_single_neighbor()
    {
        var world = new SyntheticFaceWorld(seed: 41) { LowQualityFraction = 0f };
        world.AddPerson(1);
        world.AddPerson(2); // B — NICHT trainiert
        for (var p = 3; p < 13; p++)
        {
            world.AddPerson(p);
        }

        var train = world.Faces(1, 20).Select(f => f.Confirmed()).ToList();
        var wrong = world.Face(2, cosToCenter: 0.9f);
        train.Add(wrong.ConfirmedAs(1));
        for (var p = 3; p < 13; p++)
        {
            train.AddRange(world.Faces(p, 10).Select(f => f.Confirmed()));
        }

        var bQueries = world.Faces(2, Full ? 500 : 100).Select(f => f.Unconfirmed()).ToList();
        var aQueries = world.Faces(1, Full ? 500 : 100).Select(f => f.Unconfirmed()).ToList();
        var report = new Report(output);
        report.Line("Ein falsches Beispiel (Gesicht von B, hohe Qualität) unter 20 richtigen von A; B ist nicht trainiert.");
        report.Line();
        report.Line("| k | Ensemble | B-Anfragen → A | A-Anfragen → A |");
        report.Line("|---:|---|---:|---:|");
        var rates = new Dictionary<int, double>();
        foreach (var k in new[] { 1, 3, 5 })
        {
            foreach (var linear in new[] { true, false })
            {
                var model = _recognizer.Train(train, [], FaceRecognizerOptions.Default with { NeighborCount = k, UseLinearClassifier = linear });
                var bToA = bQueries.Count(q => model.Suggest(q).PersonId == 1) / (double)bQueries.Count;
                var aToA = aQueries.Count(q => model.Suggest(q).PersonId == 1) / (double)aQueries.Count;
                if (linear)
                {
                    rates[k] = bToA;
                }

                report.Line($"| {k} | {(linear ? "ja" : "nein")} | {P(bToA)} | {P(aToA)} |");
            }
        }

        report.Flush();
        rates[3].Should().BeLessThan(0.05, "k = 3 mittelt über mehrere Beispiele");
        rates[1].Should().BeGreaterThan(rates[3], "ein Einzel-Nachbar folgt dem falschen Beispiel");
    }

    [Fact]
    public void Timing_training_suggestion_clustering()
    {
        var report = new Report(output);
        report.Line($"Zeiten ({Environment.ProcessorCount} logische Kerne, Median aus 3 Läufen nach Aufwärmen).");
        report.Line();
        report.Line("| Fall | Training | Training nur k-NN | Vorschlag je Gesicht (1 Thread) | Vorschläge parallel je 1 000 | Ausreißer-Prüfung je Gesicht |");
        report.Line("|---|---:|---:|---:|---:|---:|");
        var sizes = Full ? new[] { (10_000, 200), (100_000, 500) } : new[] { (2_000, 40) };
        TimeSpan? first = null;
        foreach (var (count, persons) in sizes)
        {
            var world = new SyntheticFaceWorld(seed: 51);
            for (var p = 0; p < persons; p++)
            {
                world.AddPerson(p, aging: p % 5 == 0);
            }

            var perPerson = count / persons;
            var train = new List<FaceSample>(count);
            var queries = new List<FaceSample>();
            for (var p = 0; p < persons; p++)
            {
                train.AddRange(world.Faces(p, perPerson).Select(f => f.Confirmed()));
                queries.AddRange(world.Faces(p, 2).Select(f => f.Unconfirmed()));
            }

            var trainTime = Median(() => _recognizer.Train(train, []));
            var knnOnly = FaceRecognizerOptions.Default with { UseLinearClassifier = false };
            var knnTime = Median(() => _recognizer.Train(train, [], knnOnly));
            first ??= trainTime;
            var model = _recognizer.Train(train, []);
            var sample = queries.Take(Math.Min(queries.Count, 1000)).ToList();
            var single = Median(() =>
            {
                foreach (var q in sample)
                {
                    model.Suggest(q);
                }
            }) / sample.Count;
            var parallel = Median(() => model.SuggestMany(sample)) * (1000.0 / sample.Count);
            var outlier = Median(() =>
            {
                foreach (var q in sample.Take(200))
                {
                    _recognizer.CheckOutlier(q, 0, model);
                }
            }) / 200;
            report.Line($"| {count.ToString("N0", De)} Beispiele / {persons} Personen | {Ms(trainTime)} | {Ms(knnTime)} | {Ms(single)} | {Ms(parallel)} | {Ms(outlier)} |");
        }

        report.Line();
        report.Line("| Gruppierung | Zeit |");
        report.Line("|---|---:|");
        foreach (var n in Full ? new[] { 5_000, 10_000, 20_000 } : new[] { 1_000 })
        {
            var world = new SyntheticFaceWorld(seed: 61);
            var faces = new List<FaceSample>(n);
            var persons = n / 25;
            for (var p = 0; p < persons; p++)
            {
                world.AddPerson(p);
                faces.AddRange(world.Faces(p, 25).Select(f => f.Unconfirmed()));
            }

            var time = Median(() => _clusterer.Cluster(faces), runs: n >= 20_000 ? 1 : 3);
            report.Line($"| {n.ToString("N0", De)} unbekannte Gesichter | {Ms(time)} |");
        }

        report.Flush();
        first!.Value.Should().BeLessThan(TimeSpan.FromSeconds(Full ? 2 : 5));
    }

    // ---------------------------------------------------------------- Welten

    private static (List<SyntheticFace> Faces, SyntheticFaceWorld World) ClusterWorld(int persons, int seed, double lookalikeShare = 0.1, float lookalikeRho = 0.62f)
    {
        var world = new SyntheticFaceWorld(seed);
        var random = new Random(seed);
        var faces = new List<SyntheticFace>();
        for (var p = 0; p < persons; p++)
        {
            int? lookalike = p > 0 && random.NextDouble() < lookalikeShare ? random.Next(p) : null;
            world.AddPerson(p, lookalike, aging: random.NextDouble() < 0.2, lookalikeRho: lookalikeRho);
            // schiefe Verteilung: viele Personen mit wenigen, einige mit vielen Gesichtern (4…80)
            var count = 3 + (int)Math.Exp(random.NextDouble() * Math.Log(78));
            faces.AddRange(world.Faces(p, count));
        }

        // Shuffle (deterministisch), damit nichts von der Erzeugungs-Reihenfolge abhängt
        return (faces.OrderBy(_ => random.Next()).ToList(), world);
    }

    private static SyntheticFaceWorld RecognitionWorld(
        int known, int unknown, int examples, int seed, out List<SyntheticFace> train, out List<SyntheticFace> queries,
        double lookalikeShare = 0.1)
    {
        var world = new SyntheticFaceWorld(seed);
        var random = new Random(seed);
        train = [];
        queries = [];
        for (var p = 0; p < known + unknown; p++)
        {
            int? lookalike = p > 0 && random.NextDouble() < lookalikeShare ? random.Next(p) : null;
            world.AddPerson(p, lookalike, aging: random.NextDouble() < 0.2);
            var faces = world.Faces(p, examples + 10).OrderBy(_ => random.Next()).ToList();
            if (p < known)
            {
                train.AddRange(faces.Take(examples));
            }

            queries.AddRange(faces.Skip(examples).Take(10));
        }

        return world;
    }

    // ---------------------------------------------------------------- Metriken

    private sealed record ClusterMetrics(
        int Groups, int MixedGroups, double Purity, double Completeness, double Grouped, double Flagged, double ForeignFlagged,
        double FlaggedForeign)
    {
        public static ClusterMetrics Compute(FaceClusterResult result, List<SyntheticFace> faces, int minGroupSize)
        {
            var truth = faces.ToDictionary(f => f.FaceId);
            long inGroups = 0, majority = 0, foreign = 0, foreignFlagged = 0, flagged = 0, flaggedForeign = 0;
            var mixed = 0;
            var bestShare = new Dictionary<int, int>();
            foreach (var group in result.Groups)
            {
                var byPerson = group.Members.GroupBy(m => truth[m.FaceId].PersonId).ToDictionary(g => g.Key, g => g.Count());
                var top = byPerson.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First();
                inGroups += group.Count;
                majority += top.Value;
                mixed += byPerson.Count > 1 ? 1 : 0;
                foreach (var m in group.Members)
                {
                    var isForeign = truth[m.FaceId].PersonId != top.Key;
                    foreign += isForeign ? 1 : 0;
                    foreignFlagged += isForeign && m.IsOutlier ? 1 : 0;
                    flagged += m.IsOutlier ? 1 : 0;
                    flaggedForeign += isForeign && m.IsOutlier ? 1 : 0;
                }

                foreach (var (person, count) in byPerson)
                {
                    bestShare[person] = Math.Max(bestShare.GetValueOrDefault(person), count);
                }
            }

            // Vollständigkeit: je Person (mit genug guten Gesichtern) der Anteil ihrer guten Gesichter in ihrer größten Gruppe.
            var good = faces.Where(f => !f.LowQuality).GroupBy(f => f.PersonId).Where(g => g.Count() >= minGroupSize).ToList();
            var completeness = good.Sum(g => (double)bestShare.GetValueOrDefault(g.Key)) / good.Sum(g => g.Count());
            var goodFaces = faces.Count(f => !f.LowQuality);
            return new ClusterMetrics(
                result.Groups.Count,
                mixed,
                inGroups == 0 ? 1 : majority / (double)inGroups,
                completeness,
                inGroups / (double)goodFaces,
                inGroups == 0 ? 0 : flagged / (double)inGroups,
                foreign == 0 ? double.NaN : foreignFlagged / (double)foreign,
                flagged == 0 ? double.NaN : flaggedForeign / (double)flagged);
        }
    }

    private sealed record RecognitionMetrics(
        double Correct, double Wrong, double Unknown, double FalseAccept,
        double AfterRejectCorrect, double AfterRejectUnknown, double AfterRejectWrong)
    {
        public static RecognitionMetrics Compute(IFaceModel model, List<SyntheticFace> queries, int known)
        {
            int correct = 0, wrong = 0, unknown = 0, knownTotal = 0, falseAccept = 0, unknownTotal = 0;
            int rejected = 0, rejectCorrect = 0, rejectUnknown = 0, rejectWrong = 0;
            var suggestions = model.SuggestMany(queries.Select(q => q.Unconfirmed()).ToList());
            for (var i = 0; i < queries.Count; i++)
            {
                var query = queries[i];
                var suggestion = suggestions[i];
                var isKnown = query.PersonId < known;
                if (isKnown)
                {
                    knownTotal++;
                    if (suggestion.Unknown)
                    {
                        unknown++;
                    }
                    else if (suggestion.PersonId == query.PersonId)
                    {
                        correct++;
                    }
                    else
                    {
                        wrong++;
                    }
                }
                else
                {
                    unknownTotal++;
                    falseAccept += suggestion.Unknown ? 0 : 1;
                }

                // Herbert lehnt einen falschen Vorschlag ab → was kommt danach?
                if (!suggestion.Unknown && suggestion.PersonId != query.PersonId)
                {
                    rejected++;
                    var next = model.Suggest(query.Unconfirmed(), [suggestion.PersonId!.Value]);
                    if (next.Unknown)
                    {
                        rejectUnknown++;
                    }
                    else if (next.PersonId == query.PersonId)
                    {
                        rejectCorrect++;
                    }
                    else
                    {
                        rejectWrong++;
                    }
                }
            }

            double Share(int value, int total) => total == 0 ? double.NaN : value / (double)total;
            return new RecognitionMetrics(
                Share(correct, knownTotal), Share(wrong, knownTotal), Share(unknown, knownTotal), Share(falseAccept, unknownTotal),
                Share(rejectCorrect, rejected), Share(rejectUnknown, rejected), Share(rejectWrong, rejected));
        }
    }

    // ---------------------------------------------------------------- Hilfen

    private static TimeSpan Median(Action action, int runs = 3)
    {
        action(); // Aufwärmen (JIT)
        var times = new List<TimeSpan>();
        for (var r = 0; r < runs; r++)
        {
            var watch = Stopwatch.StartNew();
            action();
            times.Add(watch.Elapsed);
        }

        times.Sort();
        return times[times.Count / 2];
    }

    private static TimeSpan Median(Func<object> action, int runs = 3) => Median(() => { _ = action(); }, runs);

    private static float Percentile(List<float> values, double p)
    {
        var sorted = values.Order().ToList();
        return sorted[(int)Math.Clamp(Math.Round(p * (sorted.Count - 1)), 0, sorted.Count - 1)];
    }

    private static string P(double value) => double.IsNaN(value) ? "–" : (value * 100).ToString("0.0", De) + " %";

    private static string Ms(TimeSpan time) =>
        time.TotalMilliseconds < 10
            ? time.TotalMilliseconds.ToString("0.000", De) + " ms"
            : time.TotalMilliseconds.ToString("#,0", De) + " ms";

    private sealed class Report(ITestOutputHelper output)
    {
        private readonly StringBuilder _text = new();

        public void Line(string line = "") => _text.AppendLine(line);

        public void Row(string name, List<float> values) =>
            Line($"| {name} | {Percentile(values, 0.05).ToString("0.00", De)} | {Percentile(values, 0.5).ToString("0.00", De)} | {Percentile(values, 0.95).ToString("0.00", De)} |");

        public void Flush()
        {
            output.WriteLine(_text.ToString());
            var file = Environment.GetEnvironmentVariable("LUMORA_FACELEARN_OUT");
            if (!string.IsNullOrWhiteSpace(file))
            {
                lock (typeof(Report))
                {
                    File.AppendAllText(file, _text + Environment.NewLine, Encoding.UTF8);
                }
            }
        }
    }
}
