using System.IO;
using PhotoViewerV3.Faces;
using PhotoViewerV3.Services.Faces;
using PhotoViewerV3.Services.Motifs;
using PhotoViewerV3.Services.Motifs.Models;
using PhotoViewerV3.Services.Motifs.Siglip;
using Xunit.Abstractions;

namespace PhotoViewerV3.Wpf.Tests.Motifs;

/// <summary>
/// Motiv-Erkennung mit dem ECHTEN Modell (ADR-0052) — nur mit <c>PHOTOVIEWERV3_MOTIF_MODEL</c>, sonst übersprungen.
/// Referenzprüfung ohne persönliche Fotos: gezeichnete Hund-/Katzen-Glyphe, Textseite, Programmfenster und (falls
/// vorhanden) das Windows-Hintergrundbild „Sonnenuntergang über einem See“. Geprüft wird über die Standard-Begriffe
/// genau wie im Dienst (Mittel aus Wort und Beschreibung, SigLIP-Wahrscheinlichkeit).
/// </summary>
public sealed class MotifModelTests(ITestOutputHelper output)
{
    [MotifModelFact]
    public void Tokenizer_liefert_die_Gemma_Token_Ids()
    {
        var tokenizer = GemmaTokenizer.Load(Path.Combine(ModelFolder, "tokenizer.json"));
        tokenizer.VocabularySize.Should().Be(256_000);
        tokenizer.EncodeForModel("a photo of a dog").Take(8).Should().Equal(235250, 2686, 576, 476, 5929, 1, 0, 0);
        tokenizer.EncodeRaw("ein foto von einem hund").Select(id => tokenizer.Describe([id])).Should().Equal("ein", "▁foto", "▁von", "▁einem", "▁hund");
        tokenizer.EncodeRaw("fußball").Should().Equal(126493, 3367);
        tokenizer.EncodeForModel("Hund").Should().Equal(tokenizer.EncodeForModel("hund"), "kleingeschrieben wie beim Training");
    }

    [MotifModelFact]
    public void Referenzbilder_bekommen_die_passenden_Motive()
    {
        var factory = new OnnxSiglipEncoderFactory(new MotifModelProvider(new Services.Infrastructure.AppDataPaths(Path.GetTempPath()), modelFolder: ModelFolder));
        factory.IsAvailable.Should().BeTrue();
        using var images = factory.CreateImageEncoder(useGpu: true);
        using var cpu = factory.CreateImageEncoder(useGpu: false);
        using var texts = factory.CreateTextEncoder();
        var terms = MotifDefaults.Terms.Select(t => (t.Name, Vector: MotifVectors.MeanNormalized(texts.EncodeTexts([t.Name, t.Prompt]).ToList()))).ToList();

        string[] Top(FaceImage image, int count, out float[] vector)
        {
            var v = images.EncodeImage(image);
            vector = v;
            var ranked = terms.Select(t => (t.Name, P: factory.Scoring.Probability(MotifVectors.Dot(t.Vector, v)))).OrderByDescending(x => x.P).ToList();
            output.WriteLine(string.Join(" · ", ranked.Take(5).Select(x => $"{x.Name} {x.P:0.0000}")));
            return ranked.Take(count).Select(x => x.Name).ToArray();
        }

        float P(string name, float[] v) => factory.Scoring.Probability(MotifVectors.Dot(terms.Single(t => t.Name == name).Vector, v));

        var dog = MotifReferenceImages.Glyph("\U0001F415");
        Top(dog, 3, out var dogVector).Should().Contain("Hund");
        P("Hund", dogVector).Should().BeGreaterThan(P("Katze", dogVector) * 2);

        Top(MotifReferenceImages.Glyph("\U0001F408"), 3, out var catVector).Should().Contain("Katze");
        P("Katze", catVector).Should().BeGreaterThan(P("Hund", catVector) * 2);

        Top(MotifReferenceImages.Document(), 3, out var docVector).Should().Contain(x => x == "Dokument" || x == "Quittung");
        P("Dokument", docVector).Should().BeGreaterThan((float)MotifDefaultsValues.Threshold, "über der Standard-Schwelle");

        Top(MotifReferenceImages.Screenshot(), 3, out var shotVector).Should().Contain("Bildschirmfoto");
        P("Bildschirmfoto", shotVector).Should().BeGreaterThan(P("Hund", shotVector));

        // CPU und Grafikkarte liefern denselben Vektor
        MotifVectors.Dot(cpu.EncodeImage(dog), dogVector).Should().BeGreaterThan(0.999f);

        if (File.Exists(MotifReferenceImages.WindowsSunsetLake))
        {
            var lake = new WicFaceImageLoader().Load(MotifReferenceImages.WindowsSunsetLake, MotifDefaultsValues.LoadMaxEdge);
            Top(lake, 4, out var lakeVector).Should().Contain(x => x == "Sonnenuntergang" || x == "Sonnenaufgang" || x == "See" || x == "Meer");
            P("Sonnenuntergang", lakeVector).Should().BeGreaterThan(P("Hund", lakeVector) * 10);
        }
    }

    private static string ModelFolder => Environment.GetEnvironmentVariable(IMotifModelProvider.OverrideVariable)!;
}
