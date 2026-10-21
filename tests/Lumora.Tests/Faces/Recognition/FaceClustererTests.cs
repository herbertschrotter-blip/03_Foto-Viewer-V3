using Lumora.Services.Faces.Recognition;

namespace Lumora.Tests.Faces.Recognition;

public sealed class FaceClustererTests
{
    private readonly FaceClusterer _clusterer = new();

    [Fact]
    public void Leere_Eingabe_ergibt_leeres_Ergebnis()
    {
        var result = _clusterer.Cluster([]);

        result.Groups.Should().BeEmpty();
        result.Noise.Should().BeEmpty();
        result.SkippedLowQuality.Should().BeEmpty();
    }

    [Fact]
    public void Ein_einzelnes_Gesicht_ist_Rauschen()
    {
        var world = new SyntheticFaceWorld(seed: 1);
        world.AddPerson(1);
        var face = world.Face(1);

        var result = _clusterer.Cluster([face.Unconfirmed()]);

        result.Groups.Should().BeEmpty();
        result.Noise.Should().Equal(face.FaceId);
    }

    [Fact]
    public void Mehrere_Personen_ergeben_reine_vollstaendige_Gruppen()
    {
        var world = new SyntheticFaceWorld(seed: 2) { LowQualityFraction = 0f };
        var faces = new List<SyntheticFace>();
        for (var p = 0; p < 8; p++)
        {
            world.AddPerson(p);
            faces.AddRange(world.Faces(p, 15));
        }

        var result = _clusterer.Cluster(faces.Select(f => f.Unconfirmed()).ToList());

        result.Groups.Should().HaveCount(8);
        AssertPure(result, faces);
        result.Groups.Should().OnlyContain(g => g.Count == 15);
        result.Noise.Should().BeEmpty();
        result.Groups.Should().OnlyContain(g => g.Cohesion > 0.5f && g.Cohesion < 0.9f);
    }

    [Fact]
    public void Doppelgaenger_werden_nicht_zusammengelegt()
    {
        var world = new SyntheticFaceWorld(seed: 3) { LowQualityFraction = 0f };
        world.AddPerson(1);
        world.AddPerson(2, lookalikeOf: 1, lookalikeRho: 0.75f); // Geschwister: Paar-Kosinus ~0,45–0,55
        var faces = world.Faces(1, 20).Concat(world.Faces(2, 20)).ToList();

        var result = _clusterer.Cluster(faces.Select(f => f.Unconfirmed()).ToList());

        AssertPure(result, faces);
        result.Groups.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void Eine_Bruecke_verbindet_zwei_Personen_nicht()
    {
        var world = new SyntheticFaceWorld(seed: 4) { LowQualityFraction = 0f };
        world.AddPerson(1);
        world.AddPerson(2);
        var faces = world.Faces(1, 12).Concat(world.Faces(2, 12)).ToList();
        var bridges = Enumerable.Range(0, 3).Select(_ => world.Bridge(1, 2)).ToList();

        var result = _clusterer.Cluster(faces.Concat(bridges).Select(f => f.Unconfirmed()).ToList());

        // Die Brücken landen höchstens bei EINER der beiden Personen (oder im Rauschen) — A und B bleiben getrennt.
        AssertPure(result, faces);
        result.Groups.Should().HaveCount(2);
        result.Groups.Select(g => g.Members.Count(m => faces.Any(f => f.FaceId == m.FaceId && f.PersonId == 1)))
            .Should().Contain(12).And.Contain(0);
    }

    [Fact]
    public void Mitglieder_sind_nach_Naehe_sortiert_und_der_zweifelhafte_steht_markiert_am_Ende()
    {
        var world = new SyntheticFaceWorld(seed: 5) { LowQualityFraction = 0f };
        world.AddPerson(1);
        var faces = Enumerable.Range(0, 12).Select(_ => world.Face(1, cosToCenter: 0.92f)).ToList();
        var doubtful = world.Face(1, cosToCenter: 0.66f); // Paar-Ähnlichkeit ~0,61: kommt in die Gruppe, liegt aber fern
        faces.Add(doubtful);

        var result = _clusterer.Cluster(faces.Select(f => f.Unconfirmed()).ToList());

        result.Groups.Should().ContainSingle();
        var members = result.Groups[0].Members;
        members.Should().HaveCount(13);
        members[^1].FaceId.Should().Be(doubtful.FaceId);
        members[^1].IsOutlier.Should().BeTrue();
        members[^1].OutlierScore.Should().BeGreaterThan(FaceClusterOptions.Default.OutlierRobustZ);
        members.Take(12).Should().OnlyContain(m => !m.IsOutlier);
        members.Select(m => m.Distance).Should().BeInAscendingOrder();
    }

    [Fact]
    public void Fremdes_Gesicht_fliegt_als_Rauschen_raus()
    {
        var world = new SyntheticFaceWorld(seed: 6) { LowQualityFraction = 0f };
        world.AddPerson(1);
        world.AddPerson(2);
        var faces = world.Faces(1, 10);
        var stranger = world.Face(2);

        var result = _clusterer.Cluster(faces.Append(stranger).Select(f => f.Unconfirmed()).ToList());

        result.Groups.Should().ContainSingle().Which.Members.Select(m => m.FaceId)
            .Should().BeEquivalentTo(faces.Select(f => f.FaceId));
        result.Noise.Should().Equal(stranger.FaceId);
    }

    [Fact]
    public void Schlechte_Qualitaet_und_ungueltige_Fingerabdruecke_werden_nicht_gruppiert()
    {
        var world = new SyntheticFaceWorld(seed: 7) { LowQualityFraction = 0f };
        world.AddPerson(1);
        var good = world.Faces(1, 6);
        var bad = world.Face(1, lowQuality: true);
        var zero = new FaceSample(9001, new float[512], 0.9f);
        var wrongLength = new FaceSample(9002, new float[] { 1f, 0f, 0f }, 0.9f);
        var nan = new FaceSample(9003, Enumerable.Repeat(float.NaN, 512).ToArray(), 0.9f);

        var result = _clusterer.Cluster(
            good.Select(f => f.Unconfirmed()).Append(bad.Unconfirmed()).Append(zero).Append(wrongLength).Append(nan).ToList());

        result.Groups.Should().ContainSingle().Which.Members.Select(m => m.FaceId)
            .Should().BeEquivalentTo(good.Select(f => f.FaceId));
        result.SkippedLowQuality.Should().BeEquivalentTo(new[] { bad.FaceId, 9001L, 9002L, 9003L });
    }

    [Fact]
    public void Kleine_Gruppen_unter_der_Mindestgroesse_sind_Rauschen()
    {
        var world = new SyntheticFaceWorld(seed: 8) { LowQualityFraction = 0f };
        world.AddPerson(1);
        world.AddPerson(2);
        var pair = Enumerable.Range(0, 2).Select(_ => world.Face(1, cosToCenter: 0.88f)).ToList();
        var triple = Enumerable.Range(0, 3).Select(_ => world.Face(2, cosToCenter: 0.88f)).ToList();
        var input = pair.Concat(triple).Select(f => f.Unconfirmed()).ToList();

        var strict = _clusterer.Cluster(input);
        var loose = _clusterer.Cluster(input, FaceClusterOptions.Default with { MinGroupSize = 2 });

        strict.Groups.Should().ContainSingle().Which.Count.Should().Be(3);
        strict.Noise.Should().BeEquivalentTo(pair.Select(f => f.FaceId));
        loose.Groups.Should().HaveCount(2);
    }

    [Fact]
    public void Ergebnis_ist_deterministisch_und_unabhaengig_von_der_Reihenfolge()
    {
        var world = new SyntheticFaceWorld(seed: 9);
        var faces = new List<SyntheticFace>();
        for (var p = 0; p < 12; p++)
        {
            world.AddPerson(p, lookalikeOf: p % 4 == 3 ? p - 1 : null, aging: p % 5 == 0);
            faces.AddRange(world.Faces(p, 10 + p));
        }

        var input = faces.Select(f => f.Unconfirmed()).ToList();
        var shuffled = input.OrderBy(_ => Random.Shared.Next()).ToList();

        var a = _clusterer.Cluster(input);
        var b = _clusterer.Cluster(shuffled);

        Describe(a).Should().Equal(Describe(b));
        a.Noise.Should().Equal(b.Noise);
    }

    [Fact]
    public void Viele_Personen_mit_Alterung_und_Doppelgaengern_bleiben_rein()
    {
        var world = new SyntheticFaceWorld(seed: 10);
        var random = new Random(10);
        var faces = new List<SyntheticFace>();
        for (var p = 0; p < 60; p++)
        {
            world.AddPerson(p, lookalikeOf: p > 0 && random.NextDouble() < 0.2 ? random.Next(p) : null, aging: random.NextDouble() < 0.2);
            faces.AddRange(world.Faces(p, 4 + random.Next(20)));
        }

        var result = _clusterer.Cluster(faces.Select(f => f.Unconfirmed()).ToList());

        AssertPure(result, faces);
        var grouped = result.Groups.Sum(g => g.Count);
        grouped.Should().BeGreaterThan((int)(faces.Count(f => !f.LowQuality) * 0.9));
        result.Groups.Select(g => g.Count).Should().BeInDescendingOrder();
    }

    [Fact]
    public void Abbruch_wird_beachtet()
    {
        var world = new SyntheticFaceWorld(seed: 11);
        world.AddPerson(1);
        var input = world.Faces(1, 50).Select(f => f.Unconfirmed()).ToList();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => _clusterer.Cluster(input, cancellationToken: cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Zentrum_ist_normiert_und_passt_zur_Gruppe()
    {
        var world = new SyntheticFaceWorld(seed: 12) { LowQualityFraction = 0f };
        world.AddPerson(1);
        var faces = world.Faces(1, 8);

        var group = _clusterer.Cluster(faces.Select(f => f.Unconfirmed()).ToList()).Groups.Should().ContainSingle().Subject;

        var centroid = group.Centroid.ToArray();
        SyntheticFaceWorld.Cosine(centroid, centroid).Should().BeApproximately(1f, 1e-4f);
        centroid.Sum(x => x * x).Should().BeApproximately(1f, 1e-3f);
        SyntheticFaceWorld.Cosine(centroid, world.CenterAt(1, 0.5f)).Should().BeGreaterThan(0.9f);
    }

    private static void AssertPure(FaceClusterResult result, List<SyntheticFace> faces)
    {
        var truth = faces.ToDictionary(f => f.FaceId, f => f.PersonId);
        foreach (var group in result.Groups)
        {
            group.Members.Where(m => truth.ContainsKey(m.FaceId)).Select(m => truth[m.FaceId]).Distinct()
                .Should().ContainSingle("jede Gruppe enthält nur eine Person");
        }
    }

    private static List<string> Describe(FaceClusterResult result) =>
        result.Groups.Select(g => string.Join(",", g.Members.Select(m => $"{m.FaceId}:{m.Distance:R}:{m.IsOutlier}")) + $"|{g.Cohesion:R}").ToList();
}
