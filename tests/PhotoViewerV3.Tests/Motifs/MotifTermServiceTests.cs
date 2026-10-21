using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Services.Motifs;
using PhotoViewerV3.Tests.Faces;
using PhotoViewerV3.Tests.Fixtures;
using static PhotoViewerV3.Tests.Motifs.MotifIndexServiceTests;

namespace PhotoViewerV3.Tests.Motifs;

/// <summary>
/// Begriffe der Motiv-Erkennung (ADR-0052): Standard-Liste einmal je Bibliothek, anlegen/umbenennen/löschen mit
/// Prüfungen, Treffer aus gespeicherten Vektoren (ohne neue Analyse), eigene Schwelle, ohne Modell erst später,
/// Treffer je Datei, Freitext-Suche, Nachrichten.
/// </summary>
public sealed class MotifTermServiceTests
{
    [Fact]
    public async Task Standard_Liste_einmal_je_Bibliothek_geloeschte_kommen_nicht_wieder()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        var (terms, _) = Create(fx);

        var first = await terms.GetTermsAsync(lib.Id);
        first.Select(t => t.Name).Should().Equal(MotifDefaults.Terms.Select(t => t.Name), "Reihenfolge der Start-Liste");
        first.Should().OnlyContain(t => t.Prompt != null && t.Threshold == null && t.FileCount == 0);
        MotifDefaults.Terms.Count.Should().BeInRange(75, 90, "etwa 80 deutsche Begriffe");
        MotifDefaults.Terms.Select(t => t.Name).Should().OnlyHaveUniqueItems().And.Contain(["Sonnenuntergang", "Meer", "Hund", "Bildschirmfoto", "Quittung", "Weihnachten"]);

        await terms.DeleteTermAsync(first.Single(t => t.Name == "Hund").Id);
        var again = await terms.GetTermsAsync(lib.Id);
        again.Should().HaveCount(first.Count - 1).And.NotContain(t => t.Name == "Hund");
    }

    [Fact]
    public async Task Anlegen_Umbenennen_und_Pruefungen()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        var (terms, _) = Create(fx);
        var changes = new List<int>();
        fx.Messenger.Register<List<int>, MotifTermsChangedMessage>(changes, (r, m) => r.Add(m.LibraryId));

        var id = await terms.AddTermAsync(lib.Id, "  Seilbahn ", "a photo of a cable car");
        var row = (await terms.GetTermsAsync(lib.Id)).Single(t => t.Id == id);
        row.Name.Should().Be("Seilbahn");
        row.Prompt.Should().Be("a photo of a cable car");
        row.HasVector.Should().BeTrue("Modell vorhanden → Text-Vektor sofort");
        changes.Should().Contain(lib.Id);

        var duplicate = () => terms.AddTermAsync(lib.Id, "seilBAHN");
        await duplicate.Should().ThrowAsync<MotifTermException>().WithMessage("*gibt es schon*");
        var empty = () => terms.AddTermAsync(lib.Id, "   ");
        await empty.Should().ThrowAsync<MotifTermException>();
        var tooLong = () => terms.AddTermAsync(lib.Id, new string('x', 61));
        await tooLong.Should().ThrowAsync<MotifTermException>();
        var clash = () => terms.UpdateTermAsync(id, "Hund", null);
        await clash.Should().ThrowAsync<MotifTermException>();

        await terms.UpdateTermAsync(id, "SEILBAHN", null); // nur andere Schreibweise ist erlaubt
        (await terms.GetTermsAsync(lib.Id)).Single(t => t.Id == id).Should().Match<MotifTermInfo>(t => t.Name == "SEILBAHN" && t.Prompt == null && t.HasVector);
        var badThreshold = () => terms.SetThresholdAsync(id, 1.5);
        await badThreshold.Should().ThrowAsync<MotifTermException>();
    }

    [Fact]
    public async Task Neuer_Begriff_und_Schwelle_wirken_auf_gespeicherte_Vektoren_ohne_neue_Analyse()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("a.jpg");
        fx.WriteLibraryFile("p2_b.jpg");
        await fx.ScanAsync(lib.Id);
        var (terms, store) = Create(fx);
        var a = (await fx.FindFileAsync("a.jpg"))!.Id;
        var b = (await fx.FindFileAsync("p2_b.jpg"))!.Id;
        await terms.GetTermsAsync(lib.Id);
        // Vektoren wie vom Index: a = schwacher Hund (Kosinus 0,12 → p ≈ 0,037), b = deutliche Katze (p ≈ 1)
        await terms.EnsureTextVectorsAsync(lib.Id);
        var weakDog = FakeMotifEncoderFactory.Unit(1);
        weakDog[1] = 0.12f;
        weakDog[500] = MathF.Sqrt(1 - 0.12f * 0.12f);
        await store.WriteAsync(lib.Id, [new(a, "fake-motif/v1", weakDog), new(b, "fake-motif/v1", FakeMotifEncoderFactory.Unit(2))],
            MotifScoring.Siglip2Base, 0.01, "fake-motif/v1", CancellationToken.None);
        Hits(fx, "Hund").Should().Equal("a.jpg");
        Hits(fx, "Katze").Should().Equal("p2_b.jpg");

        // Eigene Schwelle 5 % über p ≈ 0,037 → kein Treffer; zurück auf Standard → wieder da
        var dog = (await terms.GetTermsAsync(lib.Id)).Single(t => t.Name == "Hund").Id;
        await terms.SetThresholdAsync(dog, 0.05);
        Hits(fx, "Hund").Should().BeEmpty();
        (await terms.GetTermsAsync(lib.Id)).Single(t => t.Id == dog).Threshold.Should().Be(0.05);
        await terms.SetThresholdAsync(dog, null);
        Hits(fx, "Hund").Should().Equal("a.jpg");

        // Eigener Begriff „Haustier“ mit Beschreibung „a cat“ → Treffer sofort aus dem gespeicherten Vektor
        var pet = await terms.AddTermAsync(lib.Id, "Haustier", "a cat");
        Hits(fx, "Haustier").Should().Equal("p2_b.jpg");

        // Beschreibung auf „a dog“ → Treffer wandern (nur Kosinus 0,085 → mit eigener Schwelle 0,0001)
        await terms.UpdateTermAsync(pet, "Haustier", "a dog");
        Hits(fx, "Haustier").Should().BeEmpty();
        await terms.SetThresholdAsync(pet, 0.0001);
        Hits(fx, "Haustier").Should().Equal("a.jpg");

        var motifs = await terms.GetMotifsForFilesAsync([a, b]);
        motifs.Where(m => m.FileId == a).Select(m => m.Name).Should().Equal("Hund", "Haustier"); // höchste Wahrscheinlichkeit zuerst
        (await terms.GetTermsAsync(lib.Id)).Single(t => t.Name == "Haustier").FileCount.Should().Be(1);
    }

    [Fact]
    public async Task Ohne_Modell_bleiben_Begriffe_ohne_Vektor_und_bekommen_ihn_spaeter()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("a.jpg");
        await fx.ScanAsync(lib.Id);
        var factory = new FakeMotifEncoderFactory { IsAvailable = false };
        var store = new MotifStore(fx.ContextFactory);
        var terms = new MotifTermService(store, factory, new MemoryMotifSettingsStore(MotifSettings.Default), messenger: fx.Messenger);
        var a = (await fx.FindFileAsync("a.jpg"))!.Id;

        await terms.AddTermAsync(lib.Id, "Hundefoto", "a dog");
        (await terms.GetTermsAsync(lib.Id)).Should().OnlyContain(t => !t.HasVector);
        factory.TextCalls.Should().Be(0);
        terms.IsModelAvailable.Should().BeFalse();
        (await terms.SearchAsync(lib.Id, "dog", 10)).Should().BeEmpty();

        // Modell da: Vektor des Bildes existiert schon (z. B. aus einer früheren Version) → Text-Vektoren nachrechnen
        factory.IsAvailable = true;
        await store.WriteAsync(lib.Id, [new(a, "fake-motif/v1", FakeMotifEncoderFactory.Unit(1))], MotifScoring.Siglip2Base, 0.01, "fake-motif/v1", CancellationToken.None);
        Hits(fx, "Hundefoto").Should().BeEmpty("noch kein Text-Vektor");
        var computed = await terms.EnsureTextVectorsAsync(lib.Id);
        computed.Should().Be(MotifDefaults.Terms.Count + 1);
        Hits(fx, "Hundefoto").Should().Equal("a.jpg");
        Hits(fx, "Hund").Should().Equal("a.jpg");
        (await terms.EnsureTextVectorsAsync(lib.Id)).Should().Be(0, "nichts mehr offen");
    }

    [Fact]
    public async Task Freitext_Suche_liefert_die_aehnlichsten_Bilder_der_Bibliothek()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("a.jpg");
        fx.WriteLibraryFile("p2_b.jpg");
        fx.WriteLibraryFile("p3_c.jpg");
        await fx.ScanAsync(lib.Id);
        var (terms, store) = Create(fx);
        var ids = new[] { "a.jpg", "p2_b.jpg", "p3_c.jpg" }.Select(n => fx.FindFileAsync(n).Result!.Id).ToArray();
        var mixed = FakeMotifEncoderFactory.Unit(1);
        mixed[3] = 1f;
        MotifVectors.NormalizeL2(mixed); // halb Hund, halb Strand
        await store.WriteAsync(lib.Id,
        [
            new(ids[0], "fake-motif/v1", FakeMotifEncoderFactory.Unit(1)),
            new(ids[1], "fake-motif/v1", FakeMotifEncoderFactory.Unit(2)),
            new(ids[2], "fake-motif/v1", mixed),
        ], MotifScoring.Siglip2Base, 0.01, "fake-motif/v1", CancellationToken.None);

        var hits = await terms.SearchAsync(lib.Id, "dog", 2);
        hits.Select(h => h.FileId).Should().Equal(ids[0], ids[2]);
        hits[0].Score.Should().BeApproximately(1f, 1e-3f);
        hits[1].Score.Should().BeApproximately(0.707f, 1e-2f);

        var other = await CreateSecondLibraryAsync(fx);
        (await terms.SearchAsync(other.Id, "dog", 5)).Should().BeEmpty("nur Bild-Vektoren dieser Bibliothek");
    }

    private static (MotifTermService Terms, MotifStore Store) Create(TempLibraryFixture fx)
    {
        var store = new MotifStore(fx.ContextFactory);
        var terms = new MotifTermService(store, new FakeMotifEncoderFactory(), new MemoryMotifSettingsStore(MotifSettings.Default),
            NullLogger<MotifTermService>.Instance, fx.Messenger);
        return (terms, store);
    }
}
