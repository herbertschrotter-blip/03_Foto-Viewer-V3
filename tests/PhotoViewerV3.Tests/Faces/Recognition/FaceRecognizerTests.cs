using PhotoViewerV3.Services.Faces.Recognition;

namespace PhotoViewerV3.Tests.Faces.Recognition;

public sealed class FaceRecognizerTests
{
    private readonly FaceRecognizer _recognizer = new();

    [Fact]
    public void Leeres_Training_liefert_unbekannt()
    {
        var world = new SyntheticFaceWorld(seed: 1);
        world.AddPerson(1);

        var model = _recognizer.Train([], []);
        var suggestion = model.Suggest(world.Face(1).Unconfirmed());

        model.PersonIds.Should().BeEmpty();
        model.ExampleCount.Should().Be(0);
        suggestion.Unknown.Should().BeTrue();
        suggestion.PersonId.Should().BeNull();
        suggestion.Alternatives.Should().BeEmpty();
        _recognizer.CheckOutlier(world.Face(1).Unconfirmed(), 1, model).Kind.Should().Be(OutlierReason.FirstExample);
    }

    [Theory]
    [InlineData(1, 0.85)]
    [InlineData(3, 0.9)]
    [InlineData(10, 0.95)]
    public void Ordnet_bekannte_Personen_mit_wenigen_Beispielen_zu(int examples, double minimumAccuracy)
    {
        var (world, train) = KnownWorld(persons: 20, examples, seed: 2);
        var model = _recognizer.Train(train, []);

        var queries = Enumerable.Range(0, 20).SelectMany(p => world.Faces(p, 5, allowLowQuality: false)).ToList();
        var suggestions = model.SuggestMany(queries.Select(q => q.Unconfirmed()).ToList());

        var correct = queries.Zip(suggestions).Count(x => x.Second.PersonId == x.First.PersonId);
        var wrong = queries.Zip(suggestions).Count(x => x.Second.PersonId is { } id && id != x.First.PersonId);
        (correct / (double)queries.Count).Should().BeGreaterThanOrEqualTo(minimumAccuracy);
        wrong.Should().Be(0);
        suggestions.Where(s => !s.Unknown).Should().OnlyContain(s => s.Score >= FaceRecognizerOptions.Default.MinScore);
    }

    [Fact]
    public void Unbekannte_Person_bekommt_keinen_Namen()
    {
        var (world, train) = KnownWorld(persons: 20, examples: 5, seed: 3);
        world.AddPerson(500);

        var model = _recognizer.Train(train, []);
        var suggestions = world.Faces(500, 40).Select(f => model.Suggest(f.Unconfirmed())).ToList();

        suggestions.Should().OnlyContain(s => s.Unknown && s.PersonId == null);
        suggestions.Should().OnlyContain(s => s.Alternatives.Count > 0, "„vielleicht …?“ bleibt möglich");
    }

    [Fact]
    public void Ablehnung_bietet_den_naechstbesten_Kandidaten_an()
    {
        var world = new SyntheticFaceWorld(seed: 4) { LowQualityFraction = 0f };
        world.AddPerson(1);
        world.AddPerson(2, lookalikeOf: 1, lookalikeRho: 0.9f); // fast Zwillinge
        world.AddPerson(3);
        var train = world.Faces(1, 5).Select(f => f.Confirmed())
            .Concat(world.Faces(2, 5).Select(f => f.Confirmed()))
            .Concat(world.Faces(3, 5).Select(f => f.Confirmed())).ToList();
        var query = world.Face(2, cosToCenter: 0.9f);

        var model = _recognizer.Train(train, []);
        var first = model.Suggest(query.Unconfirmed());
        first.Unknown.Should().BeFalse();
        var rejectedPerson = first.PersonId!.Value;
        var expectedNext = first.Alternatives[0].PersonId;

        var retrained = _recognizer.Train(train, [new FaceRejection(query.FaceId, rejectedPerson)]);
        var next = retrained.Suggest(query.Unconfirmed());
        var viaParameter = model.Suggest(query.Unconfirmed(), [rejectedPerson]);

        next.PersonId.Should().Be(expectedNext);
        next.PersonId.Should().NotBe(rejectedPerson);
        next.Alternatives.Select(a => a.PersonId).Should().NotContain(rejectedPerson);
        viaParameter.PersonId.Should().Be(next.PersonId);
        new[] { 1, 2 }.Should().Contain(next.PersonId!.Value, "der Zwilling ist der nächstbeste, nicht die fremde Person 3");

        // Ablehnung gilt nur für dieses Gesicht
        var other = world.Face(rejectedPerson, cosToCenter: 0.9f);
        retrained.Suggest(other.Unconfirmed()).PersonId.Should().Be(rejectedPerson);
    }

    [Fact]
    public void Alle_Kandidaten_abgelehnt_ergibt_unbekannt()
    {
        var (world, train) = KnownWorld(persons: 3, examples: 3, seed: 5);
        var query = world.Face(0);

        var model = _recognizer.Train(train, [new(query.FaceId, 0), new(query.FaceId, 1), new(query.FaceId, 2)]);
        var suggestion = model.Suggest(query.Unconfirmed());

        suggestion.Unknown.Should().BeTrue();
        suggestion.Alternatives.Should().BeEmpty();
    }

    [Fact]
    public void Ein_falsches_Beispiel_unter_zwanzig_aendert_die_Zuordnung_nicht()
    {
        var world = new SyntheticFaceWorld(seed: 6) { LowQualityFraction = 0f };
        world.AddPerson(1);
        world.AddPerson(2);
        for (var p = 3; p < 10; p++)
        {
            world.AddPerson(p);
        }

        var correct = world.Faces(1, 20).Select(f => f.Confirmed()).ToList();
        var others = Enumerable.Range(3, 7).SelectMany(p => world.Faces(p, 10)).Select(f => f.Confirmed()).ToList();
        var bExamples = world.Faces(2, 20).Select(f => f.Confirmed()).ToList();
        var wrong = world.Face(2, cosToCenter: 0.9f).ConfirmedAs(1); // B-Gesicht versehentlich als A bestätigt
        var aQueries = world.Faces(1, 50).Select(f => f.Unconfirmed()).ToList();
        var bQueries = world.Faces(2, 50).Select(f => f.Unconfirmed()).ToList();

        // a) B ist bekannt: Zuordnungen sind mit und ohne falsches Beispiel identisch
        var clean = _recognizer.Train([.. correct, .. bExamples, .. others], []);
        var dirty = _recognizer.Train([.. correct, wrong, .. bExamples, .. others], []);
        aQueries.Concat(bQueries).Select(q => dirty.Suggest(q).PersonId)
            .Should().Equal(aQueries.Concat(bQueries).Select(q => clean.Suggest(q).PersonId));

        // b) B ist unbekannt: B-Gesichter werden trotzdem nicht A zugeordnet (k = 3 mittelt)
        var dirtyWithoutB = _recognizer.Train([.. correct, wrong, .. others], []);
        bQueries.Should().OnlyContain(q => dirtyWithoutB.Suggest(q, null).PersonId != 1);
        aQueries.Should().OnlyContain(q => dirtyWithoutB.Suggest(q, null).PersonId == 1);

        // Gegenprobe: ein Einzel-Nachbar (k = 1) folgt dem falschen Beispiel
        var singleNeighbor = _recognizer.Train([.. correct, wrong, .. others], [], FaceRecognizerOptions.Default with { NeighborCount = 1 });
        bQueries.Count(q => singleNeighbor.Suggest(q).PersonId == 1).Should().BeGreaterThan(bQueries.Count / 2);
    }

    [Fact]
    public void Ausreisser_Pruefung_meldet_das_falsche_Beispiel_und_neu_lernen_entfernt_seine_Wirkung()
    {
        var world = new SyntheticFaceWorld(seed: 7) { LowQualityFraction = 0f };
        world.AddPerson(1);
        world.AddPerson(2);
        world.AddPerson(3);
        var aExamples = world.Faces(1, 2).Select(f => f.Confirmed()).ToList();
        var wrongFace = world.Face(2, cosToCenter: 0.9f);
        var others = world.Faces(3, 5).Select(f => f.Confirmed()).ToList();
        var bQueries = world.Faces(2, 30).Select(f => f.Unconfirmed()).ToList();

        // Mit nur zwei richtigen Beispielen zieht das falsche B-Gesicht B-Anfragen zu A …
        List<FaceSample> withWrong = [.. aExamples, wrongFace.ConfirmedAs(1), .. others];
        var dirty = _recognizer.Train(withWrong, []);
        var check = _recognizer.CheckOutlier(wrongFace.ConfirmedAs(1), 1, dirty);
        check.IsOutlier.Should().BeTrue("das Gesicht passt kaum zu den übrigen Beispielen von A");
        check.Kind.Should().Be(OutlierReason.FarFromExamples);
        check.Reason.Should().NotBeNullOrWhiteSpace();
        var dirtyScores = bQueries.Select(q => ScoreFor(dirty.Suggest(q), 1)).ToList();

        // … nach dem Entfernen lernt A nur aus den verbleibenden Beispielen.
        var clean = _recognizer.Train([.. aExamples, .. others], []);
        clean.ExampleCountOf(1).Should().Be(2);
        bQueries.Should().OnlyContain(q => clean.Suggest(q).PersonId != 1);
        bQueries.Select(q => ScoreFor(clean.Suggest(q), 1)).Average().Should().BeLessThan(dirtyScores.Average());
    }

    [Fact]
    public void Ausreisser_Pruefung_passt_ersteBeispiel_andere_Person_Qualitaet_ungueltig()
    {
        var world = new SyntheticFaceWorld(seed: 8) { LowQualityFraction = 0f };
        world.AddPerson(1);
        world.AddPerson(2);
        world.AddPerson(3);
        var train = world.Faces(1, 12).Concat(world.Faces(2, 12)).Select(f => f.Confirmed()).ToList();
        var model = _recognizer.Train(train, []);

        var fits = _recognizer.CheckOutlier(world.Face(1).Unconfirmed(), 1, model);
        fits.IsOutlier.Should().BeFalse();
        fits.Kind.Should().Be(OutlierReason.Fits);
        fits.Score.Should().BeGreaterThan(0.5f);
        fits.TypicalScore.Should().BeGreaterThan(0.5f);

        var otherPerson = _recognizer.CheckOutlier(world.Face(2).Unconfirmed(), 1, model);
        otherPerson.IsOutlier.Should().BeTrue();
        otherPerson.Kind.Should().Be(OutlierReason.CloserToOtherPerson);
        otherPerson.CloserPersonId.Should().Be(2);

        var stranger = _recognizer.CheckOutlier(world.Face(3).Unconfirmed(), 1, model);
        stranger.IsOutlier.Should().BeTrue();
        stranger.Kind.Should().Be(OutlierReason.FarFromExamples);

        _recognizer.CheckOutlier(world.Face(3).Unconfirmed(), 3, model).Kind.Should().Be(OutlierReason.FirstExample);
        var lowCheck = _recognizer.CheckOutlier(world.Face(1, cosToCenter: 0.85f).Unconfirmed() with { Quality = 0.1f }, 1, model);
        lowCheck.Kind.Should().Be(OutlierReason.LowQuality);
        lowCheck.IsOutlier.Should().BeFalse();

        var invalid = _recognizer.CheckOutlier(new FaceSample(77, new float[512], 1f), 1, model);
        invalid.IsOutlier.Should().BeTrue();
        invalid.Kind.Should().Be(OutlierReason.InvalidEmbedding);
        _recognizer.CheckOutlier(new FaceSample(78, new float[3], 1f), 1, model).Kind.Should().Be(OutlierReason.InvalidEmbedding);
    }

    [Fact]
    public void Ausreisser_Pruefung_meldet_ungewoehnliches_Gesicht_bei_vielen_Beispielen()
    {
        var world = new SyntheticFaceWorld(seed: 9) { LowQualityFraction = 0f };
        world.AddPerson(1);
        var train = Enumerable.Range(0, 20).Select(_ => world.Face(1, cosToCenter: 0.93f).Confirmed()).ToList();
        var model = _recognizer.Train(train, []);

        // Ähnlichkeit ~0,6 liegt über der absoluten Schwelle, aber weit unter dem Üblichen (~0,86) dieser Person.
        var unusual = _recognizer.CheckOutlier(world.Face(1, cosToCenter: 0.62f).Unconfirmed(), 1, model);

        unusual.IsOutlier.Should().BeTrue();
        unusual.Kind.Should().Be(OutlierReason.UnusualForPerson);
        unusual.Score.Should().BeGreaterThan(FaceRecognizerOptions.Default.OutlierMinSimilarity);
    }

    [Fact]
    public void Gesicht_das_schon_Beispiel_ist_wird_bei_der_Pruefung_ausgeklammert()
    {
        var world = new SyntheticFaceWorld(seed: 10) { LowQualityFraction = 0f };
        world.AddPerson(1);
        world.AddPerson(2);
        var train = world.Faces(1, 8).Select(f => f.Confirmed()).ToList();
        var wrong = world.Face(2).ConfirmedAs(1);
        var model = _recognizer.Train([.. train, wrong], []);

        // Ohne Ausklammern wäre die Ähnlichkeit 1,0 (das Gesicht mit sich selbst).
        var check = _recognizer.CheckOutlier(wrong, 1, model);
        check.IsOutlier.Should().BeTrue();
        check.Score.Should().BeLessThan(0.5f);

        // Einziges Beispiel einer Person ist das Gesicht selbst → nichts zu vergleichen, Vorschlag ohne Selbsttreffer.
        var lone = world.Face(1).Confirmed();
        var single = _recognizer.Train([lone], []);
        _recognizer.CheckOutlier(lone, 1, single).Kind.Should().Be(OutlierReason.FirstExample);
        single.Suggest(lone).Unknown.Should().BeTrue();
    }

    [Fact]
    public void Schlechte_Qualitaet_wird_nie_Lernbeispiel_bekommt_aber_einen_niedrigeren_Vorschlag()
    {
        var world = new SyntheticFaceWorld(seed: 11) { LowQualityFraction = 0f };
        world.AddPerson(1);
        world.AddPerson(2);
        var good = world.Faces(1, 5).Select(f => f.Confirmed()).ToList();
        var poor = world.Faces(2, 5).Select(f => f.Confirmed() with { Quality = 0.1f }).ToList();

        var model = _recognizer.Train([.. good, .. poor], []);

        model.ExampleCountOf(1).Should().Be(5);
        model.ExampleCountOf(2).Should().Be(0);
        model.PersonIds.Should().Equal(1);
        model.IgnoredCount.Should().Be(5);

        var face = world.Face(1, cosToCenter: 0.85f);
        var sharp = model.Suggest(face.Unconfirmed());
        var blurred = model.Suggest(face.Unconfirmed() with { Quality = 0.1f });
        sharp.PersonId.Should().Be(1);
        blurred.Score.Should().BeLessThan(sharp.Score);
        blurred.Similarity.Should().Be(sharp.Similarity);
    }

    [Fact]
    public void Unbestaetigte_Gesichter_und_eigene_Vorschlaege_fliessen_nie_ins_Lernen()
    {
        var world = new SyntheticFaceWorld(seed: 12) { LowQualityFraction = 0f };
        world.AddPerson(1);
        world.AddPerson(2);
        var confirmed = world.Faces(1, 5).Select(f => f.Confirmed()).ToList();
        var unconfirmed = world.Faces(2, 30).Select(f => f.Unconfirmed()).ToList();

        var model = _recognizer.Train([.. confirmed, .. unconfirmed], []);

        model.ExampleCount.Should().Be(5);
        model.PersonIds.Should().Equal(1);
        model.IgnoredCount.Should().Be(0, "unbestätigte Gesichter sind keine ignorierten Beispiele, sondern gar keine");
        // Vorschläge des Modells ändern das Modell nicht (unveränderlich): wiederholte Vorschläge sind gleich.
        var query = world.Face(1).Unconfirmed();
        var first = model.Suggest(query);
        for (var i = 0; i < 5; i++)
        {
            model.Suggest(world.Face(1).Unconfirmed());
        }

        model.Suggest(query).Should().BeEquivalentTo(first);
        model.ExampleCount.Should().Be(5);
    }

    [Fact]
    public void Training_ist_deterministisch_und_unabhaengig_von_der_Reihenfolge()
    {
        var (world, train) = KnownWorld(persons: 30, examples: 4, seed: 13); // > CandidatePersons: Vorauswahl aktiv
        var shuffled = train.OrderBy(_ => Random.Shared.Next()).ToList();
        var queries = Enumerable.Range(0, 30).Select(p => world.Face(p).Unconfirmed()).ToList();

        var a = _recognizer.Train(train, []);
        var b = _recognizer.Train(shuffled, []);

        foreach (var q in queries)
        {
            var sa = a.Suggest(q);
            var sb = b.Suggest(q);
            sb.PersonId.Should().Be(sa.PersonId);
            sb.Score.Should().Be(sa.Score);
            sb.Similarity.Should().Be(sa.Similarity);
            sb.Alternatives.Should().Equal(sa.Alternatives);
        }
    }

    [Fact]
    public void Eine_einzige_Person_ohne_Gegenbeispiele()
    {
        var world = new SyntheticFaceWorld(seed: 14) { LowQualityFraction = 0f };
        world.AddPerson(1);
        world.AddPerson(2);
        var model = _recognizer.Train(world.Faces(1, 6).Select(f => f.Confirmed()).ToList(), []);

        world.Faces(1, 20).Select(f => model.Suggest(f.Unconfirmed()).PersonId).Should().OnlyContain(id => id == 1);
        world.Faces(2, 20).Select(f => model.Suggest(f.Unconfirmed())).Should().OnlyContain(s => s.Unknown);
    }

    [Fact]
    public void Viele_Personen_mit_Vorauswahl_der_Kandidaten()
    {
        var (world, train) = KnownWorld(persons: 120, examples: 3, seed: 15);
        var model = _recognizer.Train(train, []);
        var queries = Enumerable.Range(0, 120).SelectMany(p => world.Faces(p, 2, allowLowQuality: false)).ToList();

        var suggestions = model.SuggestMany(queries.Select(q => q.Unconfirmed()).ToList());

        model.PersonIds.Should().HaveCount(120);
        var correct = queries.Zip(suggestions).Count(x => x.Second.PersonId == x.First.PersonId);
        (correct / (double)queries.Count).Should().BeGreaterThan(0.9);
        suggestions.Should().OnlyContain(s => s.Alternatives.Count <= FaceRecognizerOptions.Default.MaxAlternatives);
    }

    [Fact]
    public void SuggestMany_entspricht_einzelnen_Vorschlaegen_und_ist_parallel_lesbar()
    {
        var (world, train) = KnownWorld(persons: 40, examples: 3, seed: 16);
        var model = _recognizer.Train(train, []);
        var queries = Enumerable.Range(0, 40).SelectMany(p => world.Faces(p, 3)).Select(f => f.Unconfirmed()).ToList();

        var sequential = queries.Select(q => model.Suggest(q)).ToList();
        var parallel = model.SuggestMany(queries);
        var concurrent = new FaceSuggestion[queries.Count];
        Parallel.For(0, queries.Count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => concurrent[i] = model.Suggest(queries[i]));

        parallel.Should().BeEquivalentTo(sequential, o => o.WithStrictOrdering());
        concurrent.Should().BeEquivalentTo(sequential, o => o.WithStrictOrdering());
    }

    [Fact]
    public void Ungueltige_Anfragen_liefern_unbekannt()
    {
        var (_, train) = KnownWorld(persons: 3, examples: 2, seed: 17);
        var model = _recognizer.Train(train, []);

        model.Suggest(new FaceSample(1, new float[512], 1f)).Unknown.Should().BeTrue();
        model.Suggest(new FaceSample(2, new float[100], 1f)).Unknown.Should().BeTrue();
        model.Suggest(new FaceSample(3, ReadOnlyMemory<float>.Empty, 1f)).Unknown.Should().BeTrue();
    }

    [Fact]
    public void Doppelte_FaceIds_und_ungueltige_Beispiele_werden_ignoriert()
    {
        var world = new SyntheticFaceWorld(seed: 18) { LowQualityFraction = 0f };
        world.AddPerson(1);
        var face = world.Face(1);
        var model = _recognizer.Train(
            [face.Confirmed(), face.Confirmed(), new FaceSample(99, new float[512], 1f, 1), new FaceSample(98, new float[7], 1f, 1)],
            []);

        model.ExampleCountOf(1).Should().Be(1);
        model.IgnoredCount.Should().Be(3);
    }

    [Fact]
    public void Alterung_naechste_Beispiele_tragen_den_Verlauf()
    {
        var world = new SyntheticFaceWorld(seed: 19) { LowQualityFraction = 0f };
        world.AddPerson(1, aging: true);
        for (var p = 2; p < 12; p++)
        {
            world.AddPerson(p);
        }

        // Beispiele über die Jahre verteilt; Anfragen aus allen Lebensphasen
        var train = world.Faces(1, 12, 0f, 1f).Select(f => f.Confirmed())
            .Concat(Enumerable.Range(2, 10).SelectMany(p => world.Faces(p, 5)).Select(f => f.Confirmed())).ToList();
        var model = _recognizer.Train(train, []);
        var queries = world.Faces(1, 30, 0f, 1f).Select(f => f.Unconfirmed()).ToList();

        queries.Count(q => model.Suggest(q).PersonId == 1).Should().BeGreaterThan(27);
    }

    private static float ScoreFor(FaceSuggestion suggestion, int personId) =>
        suggestion.PersonId == personId
            ? suggestion.Score
            : suggestion.Alternatives.Where(a => a.PersonId == personId).Select(a => a.Score).DefaultIfEmpty(0f).First();

    private static (SyntheticFaceWorld World, List<FaceSample> Train) KnownWorld(int persons, int examples, int seed)
    {
        var world = new SyntheticFaceWorld(seed) { LowQualityFraction = 0f };
        var train = new List<FaceSample>();
        for (var p = 0; p < persons; p++)
        {
            world.AddPerson(p);
            train.AddRange(world.Faces(p, examples).Select(f => f.Confirmed()));
        }

        return (world, train);
    }
}
