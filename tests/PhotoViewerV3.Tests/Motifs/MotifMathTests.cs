using System.Text;
using PhotoViewerV3.Services.Faces;
using PhotoViewerV3.Services.Motifs;
using PhotoViewerV3.Services.Motifs.Siglip;

namespace PhotoViewerV3.Tests.Motifs;

/// <summary>
/// Rechenteile der Motiv-Erkennung (ADR-0052): Vorverarbeitung nach preprocessor_config.json (PIL-BILINEAR, Normierung),
/// Vektoren (float32/float16), SigLIP-Wahrscheinlichkeit, Treffer je Schwelle, Gemma-BPE-Tokenizer an einer kleinen
/// tokenizer.json.
/// </summary>
public sealed class MotifMathTests
{
    [Fact]
    public void Normierung_wie_SiglipImageProcessor()
    {
        SiglipPreprocessor.Normalize(0).Should().Be(-1f);
        SiglipPreprocessor.Normalize(255).Should().BeApproximately(1f, 1e-6f);
        SiglipPreprocessor.Normalize(128).Should().BeApproximately((128 / 255f - 0.5f) / 0.5f, 1e-6f);
    }

    [Fact]
    public void Pil_Bilinear_verkleinert_mit_Dreiecksfilter_und_Festkomma()
    {
        // 4 × 1 → 2 × 1 (Faktor 2, Träger 2): Gewichte je Zielpixel 3/7, 3/7, 1/7 bzw. 1/7, 3/7, 3/7 (von Hand nach Resample.c)
        var row = new byte[] { 0, 0, 0, 100, 100, 100, 200, 200, 200, 255, 255, 255 };
        var result = SiglipPreprocessor.ResizeBilinear(row, 4, 1, 2, 1);
        result.Should().Equal(71, 71, 71, 209, 209, 209);

        // 448 → 224 (wie die Ladegröße): je Zielpixel vier Quellpixel mit 1/8, 3/8, 3/8, 1/8
        var (bounds, weights, ksize) = SiglipPreprocessor.Coefficients(448, 224);
        bounds[2].Should().Be(1);
        bounds[3].Should().Be(4);
        weights.Skip(ksize).Take(4).Select(w => w / (double)(1 << 22)).Should().Equal(new[] { 0.125, 0.375, 0.375, 0.125 }, (a, b) => Math.Abs(a - b) < 1e-6);
        bounds[0].Should().Be(0, "am Rand abgeschnitten");
        bounds[1].Should().Be(3);
    }

    [Fact]
    public void Einfarbiges_Bild_ergibt_konstante_Werte_in_NCHW()
    {
        var pixels = new byte[300 * 200 * 3];
        for (var i = 0; i < pixels.Length; i += 3)
        {
            (pixels[i], pixels[i + 1], pixels[i + 2]) = ((byte)255, (byte)0, (byte)51);
        }

        var tensor = new float[SiglipPreprocessor.TensorLength];
        SiglipPreprocessor.Fill(new FaceImage(pixels, 300, 200), tensor);
        const int plane = 224 * 224;
        tensor.Take(plane).Should().AllSatisfy(v => v.Should().BeApproximately(1f, 1e-6f));
        tensor.Skip(plane).Take(plane).Should().AllSatisfy(v => v.Should().Be(-1f));
        tensor.Skip(2 * plane).Should().AllSatisfy(v => v.Should().BeApproximately(51 / 127.5f - 1f, 1e-6f));
    }

    [Fact]
    public void Vergroessern_und_Seitenverhaeltnis_werden_ohne_Zuschnitt_auf_224_gebracht()
    {
        // Links schwarz, rechts weiß (100 × 50): nach 224 × 224 bleibt die Kante in der Mitte, oben = unten
        var pixels = new byte[100 * 50 * 3];
        for (var y = 0; y < 50; y++)
        {
            for (var x = 50; x < 100; x++)
            {
                var p = (y * 100 + x) * 3;
                pixels[p] = pixels[p + 1] = pixels[p + 2] = 255;
            }
        }

        var result = SiglipPreprocessor.ResizeBilinear(pixels, 100, 50, 224, 224);
        result[(0 * 224 + 10) * 3].Should().Be(0);
        result[(223 * 224 + 213) * 3].Should().Be(255);
        result[(5 * 224 + 111) * 3].Should().Be(result[(200 * 224 + 111) * 3], "keine senkrechte Änderung");
    }

    [Fact]
    public void Vektoren_float32_und_float16_verlustarm()
    {
        var random = new Random(3);
        var a = Enumerable.Range(0, 768).Select(_ => (float)random.NextDouble() - 0.5f).ToArray();
        var b = Enumerable.Range(0, 768).Select(_ => (float)random.NextDouble() - 0.5f).ToArray();
        MotifVectors.NormalizeL2(a);
        MotifVectors.NormalizeL2(b);
        MotifVectors.Decode(MotifVectors.Encode(a)).Should().Equal(a);
        var half = MotifVectors.DecodeHalf(MotifVectors.EncodeHalf(a));
        MotifVectors.EncodeHalf(a).Length.Should().Be(1536);
        Math.Abs(MotifVectors.Dot(half, b) - MotifVectors.Dot(a, b)).Should().BeLessThan(0.001f);
        MotifVectors.Dot(a, a).Should().BeApproximately(1f, 1e-5f);
        var mean = MotifVectors.MeanNormalized([a, b]);
        MotifVectors.Dot(mean, mean).Should().BeApproximately(1f, 1e-5f);
        MotifVectors.Dot(mean, a).Should().BeApproximately(MotifVectors.Dot(mean, b), 1e-5f);
    }

    [Fact]
    public void Siglip_Wahrscheinlichkeit_aus_logit_scale_und_logit_bias()
    {
        var scoring = MotifScoring.Siglip2Base;
        scoring.Temperature.Should().BeApproximately(112.67, 0.01);
        scoring.Probability(0.1489f).Should().BeApproximately(0.5f, 0.01f);
        scoring.Probability(0f).Should().BeLessThan(1e-7f);
        scoring.Probability(1f).Should().BeApproximately(1f, 1e-6f);
        scoring.CosineFor(0.01).Should().BeApproximately(0.1081f, 0.0005f);
        scoring.Probability(scoring.CosineFor(0.03)).Should().BeApproximately(0.03f, 1e-4f);
    }

    [Fact]
    public void Treffer_je_Schwelle_mit_synthetischen_Vektoren()
    {
        var scoring = MotifScoring.Siglip2Base;
        var image = Unit(0, 0.12f, 1, 0.99278f); // cos 0,12 zu Begriff 0
        var terms = new List<MotifTermVector>
        {
            new(10, Unit(0, 1f), 0.01),   // p(0,12) ≈ 0,037 ≥ 0,01 → Treffer
            new(11, Unit(0, 1f), 0.1),    // eigene Schwelle 0,1 → kein Treffer
            new(12, Unit(2, 1f), 0.0001), // orthogonal → p ≈ 5e-8 → nein
        };
        var hits = MotifMatcher.Match(image, terms, scoring);
        hits.Select(h => h.TermId).Should().Equal(10);
        hits[0].Score.Should().BeApproximately(scoring.Probability(0.12f), 1e-6f);
    }

    [Fact]
    public void Tokenizer_BPE_mit_Rangfolge_Byte_Fallback_eos_und_Auffuellen()
    {
        var tokenizer = GemmaTokenizer.Parse(Encoding.UTF8.GetBytes(SmallTokenizerJson));
        // „ein▁hund“: e,i,n,▁,h,u,n,d → Merges nach Rang: (e,i) (ei,n) (▁,h) (u,n) (▁h,un) (▁hun,d)
        tokenizer.EncodeRaw("ein hund").Should().Equal(Id("ein"), Id("▁hund"));
        // Großbuchstaben → kleingeschrieben fürs Modell, dann <eos>=1 und <pad>=0
        var ids = tokenizer.EncodeForModel("Ein Hund");
        ids.Length.Should().Be(64);
        ids.Take(3).Should().Equal(new long[] { Id("ein"), Id("▁hund"), 1 });
        ids.Skip(3).Should().AllSatisfy(i => i.Should().Be(0));
        // Unbekanntes Zeichen → UTF-8-Bytes <0xC3><0xBC> (ü)
        tokenizer.EncodeRaw("ü").Should().Equal(Id("<0xC3>"), Id("<0xBC>"));
        // Kürzen: höchstens 63 Token + <eos>
        var longIds = tokenizer.EncodeForModel(string.Concat(Enumerable.Repeat("x", 100)));
        longIds[62].Should().Be((long)Id("x"));
        longIds[63].Should().Be(1);

        static int Id(string token) => SmallVocab[token];
    }

    private static float[] Unit(int index, float value, int index2 = -1, float value2 = 0)
    {
        var v = new float[768];
        v[index] = value;
        if (index2 >= 0)
        {
            v[index2] = value2;
        }

        return v;
    }

    private static readonly Dictionary<string, int> SmallVocab = new()
    {
        ["<pad>"] = 0, ["<eos>"] = 1, ["<bos>"] = 2, ["<unk>"] = 3,
        ["e"] = 10, ["i"] = 11, ["n"] = 12, ["▁"] = 13, ["h"] = 14, ["u"] = 15, ["d"] = 16, ["x"] = 17,
        ["ei"] = 20, ["ein"] = 21, ["▁h"] = 22, ["un"] = 23, ["▁hun"] = 24, ["▁hund"] = 25, ["nd"] = 26,
        ["<0xC3>"] = 30, ["<0xBC>"] = 31,
    };

    // „nd“ hat einen schlechteren Rang als (▁hun,d): sonst entstünde ▁h|u|nd
    private static readonly string SmallTokenizerJson = """
        {
          "version": "1.0",
          "added_tokens": [ { "id": 0, "content": "<pad>" } ],
          "normalizer": { "type": "Replace", "pattern": { "String": " " }, "content": "▁" },
          "model": {
            "type": "BPE",
            "byte_fallback": true,
            "vocab": VOCAB,
            "merges": [ ["e", "i"], ["ei", "n"], ["▁", "h"], ["u", "n"], ["▁h", "un"], ["▁hun", "d"], "n d" ]
          }
        }
        """.Replace("VOCAB", "{" + string.Join(",", SmallVocab.Select(kv => $"\"{kv.Key}\": {kv.Value}")) + "}");
}
