using System.IO;
using Lumora.Faces;
using Lumora.Services.Faces;
using Lumora.Services.Motifs;
using Lumora.Services.Motifs.Models;
using Lumora.Services.Motifs.Siglip;
using Xunit.Abstractions;

namespace Lumora.Wpf.Tests.Motifs;

/// <summary>Nur mit echtem Modell (<c>LUMORA_MOTIF_MODEL</c>), sonst übersprungen.</summary>
public sealed class MotifModelFactAttribute : FactAttribute
{
    public MotifModelFactAttribute()
    {
        var folder = Environment.GetEnvironmentVariable(IMotifModelProvider.OverrideVariable);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            Skip = "Nur mit Modell: LUMORA_MOTIF_MODEL auf den Ordner von siglip2-base-patch16-224 setzen.";
        }
    }
}

/// <summary>Erkundung (nur lokal, LUMORA_MOTIF_EXPLORE=1): Kosinus/Wahrscheinlichkeit je Referenzbild und Vorlage.</summary>
public sealed class MotifModelExplorationTests(ITestOutputHelper output)
{
    [MotifModelFact]
    public void Erkundung()
    {
        if (Environment.GetEnvironmentVariable("LUMORA_MOTIF_EXPLORE") != "1")
        {
            return;
        }

        var folder = Environment.GetEnvironmentVariable(IMotifModelProvider.OverrideVariable)!;
        var models = new MotifModelProvider(new Services.Infrastructure.AppDataPaths(Path.GetTempPath()), modelFolder: folder);
        var factory = new OnnxSiglipEncoderFactory(models);
        using var image = factory.CreateImageEncoder(useGpu: true);
        using var text = factory.CreateTextEncoder();
        var loader = new WicFaceImageLoader();
        var images = new List<(string Name, FaceImage Image)>
        {
            ("Hund-Emoji", MotifReferenceImages.Glyph("\U0001F415")),
            ("Katze-Emoji", MotifReferenceImages.Glyph("\U0001F408")),
            ("Dokument", MotifReferenceImages.Document()),
            ("Bildschirm", MotifReferenceImages.Screenshot()),
        };
        foreach (var (name, path) in new[] { ("See-Abend", MotifReferenceImages.WindowsSunsetLake), ("See-Tag", MotifReferenceImages.WindowsDayLake), ("Abstrakt", MotifReferenceImages.WindowsAbstract) })
        {
            if (File.Exists(path))
            {
                images.Add((name, loader.Load(path, 448)));
            }
        }

        var vectors = images.Select(i => image.EncodeImage(i.Image)).ToList();
        output.WriteLine($"Provider {image.ExecutionProvider}");
        string[] prompts =
        [
            "a photo of a dog", "a photo of a cat", "ein Foto von einem Hund", "ein Foto von einer Katze", "Hund", "Katze",
            "ein Foto eines Sonnenuntergangs", "Sonnenuntergang", "ein Foto von einem See", "See", "ein Foto vom Meer", "Meer",
            "ein Foto von einem Dokument", "Dokument", "ein Bildschirmfoto", "Bildschirmfoto", "ein Screenshot",
            "ein Foto von Bergen", "ein Foto von Schnee", "ein Foto von einer Quittung", "ein Foto von Essen",
            "ein Foto von einer Gruppe von Menschen", "ein Foto von Blumen", "eine Zeichnung von einem Hund", "ein Bild von einem Hund",
            "a photo of a sunset", "a photo of a lake", "a photo of the sea", "a photo of a document", "a screenshot",
            "a photo of mountains", "a photo of snow", "a photo of food", "a photo of flowers", "sunset", "lake", "dog", "cat",
            "ein Sonnenuntergang", "ein See", "ein Hund", "eine Katze", "Sonnenuntergang am See", "abstrakte Kunst", "abstract art",
        ];
        var textVectors = text.EncodeTexts(prompts);
        var tokenizer = ((OnnxSiglipTextEncoder)text).Tokenizer;
        output.WriteLine("Tokens „a photo of a dog“: " + string.Join(",", tokenizer.EncodeForModel("a photo of a dog").Take(8)) + " = " + tokenizer.Describe(tokenizer.EncodeRaw("a photo of a dog")));
        output.WriteLine("Tokens „ein foto von einem hund“: " + tokenizer.Describe(tokenizer.EncodeRaw("ein foto von einem hund")));
        output.WriteLine("Tokens „fußball“: " + tokenizer.Describe(tokenizer.EncodeRaw("fußball")) + " " + string.Join(",", tokenizer.EncodeRaw("fußball")));
        var header = "Prompt".PadRight(40) + string.Join("", images.Select(i => i.Name.PadLeft(12)));
        output.WriteLine(header);
        for (var t = 0; t < prompts.Length; t++)
        {
            var line = prompts[t].PadRight(40);
            for (var i = 0; i < images.Count; i++)
            {
                var cos = MotifVectors.Dot(textVectors[t], vectors[i]);
                line += $"{cos,6:0.000}/{factory.Scoring.Probability(cos),5:0.00}";
            }

            output.WriteLine(line);
        }

        // Varianten je Begriff: richtiges Bild (Index) vs. übrige — Abstand (richtig − bester falscher)
        (string De, string DePhrase, string En, int Correct)[] concepts =
        [
            ("Hund", "einem Hund", "a dog", 0), ("Katze", "einer Katze", "a cat", 1), ("Dokument", "einem Dokument", "a document", 2),
            ("Bildschirmfoto", "einem Bildschirm", "a computer screen", 3), ("Sonnenuntergang", "einem Sonnenuntergang", "a sunset", 4),
            ("See", "einem See", "a lake", 5),
        ];
        var variants = new (string Name, Func<(string De, string DePhrase, string En, int Correct), string[]> Texts)[]
        {
            ("Wort DE", c => [c.De]),
            ("Foto DE", c => [$"ein Foto von {c.DePhrase}"]),
            ("Foto EN", c => [$"a photo of {c.En}"]),
            ("Wort DE + Foto DE", c => [c.De, $"ein Foto von {c.DePhrase}"]),
            ("Wort DE + Foto EN", c => [c.De, $"a photo of {c.En}"]),
            ("Wort DE + Foto DE + EN", c => [c.De, $"ein Foto von {c.DePhrase}", $"a photo of {c.En}"]),
            ("Wort DE + Bild DE", c => [c.De, $"ein Bild mit {c.DePhrase}"]),
        };
        if (images.Count < 6)
        {
            return;
        }

        foreach (var (variantName, texts) in variants)
        {
            var margins = new List<float>();
            var line = variantName.PadRight(26);
            foreach (var concept in concepts)
            {
                var vector = MotifVectors.MeanNormalized(text.EncodeTexts(texts(concept)).ToList());
                var cos = vectors.Select(v => MotifVectors.Dot(vector, v)).ToList();
                var margin = cos[concept.Correct] - cos.Where((_, i) => i != concept.Correct && !(concept.Correct >= 4 && i is 4 or 5)).Max();
                margins.Add(margin);
                line += $" {concept.De}={cos[concept.Correct]:0.000}/{margin:+0.000;-0.000}";
            }

            output.WriteLine($"{line}  Ø Abstand {margins.Average():+0.000;-0.000}");
        }
    }
}
