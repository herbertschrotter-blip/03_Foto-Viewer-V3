using Lumora.Services.Faces;
using Lumora.Services.Faces.Alignment;
using Lumora.Services.Faces.Detection;
using Lumora.Services.Faces.Onnx;

namespace Lumora.Tests.Faces;

/// <summary>
/// Reine Rechenschritte der Gesichtserkennung ohne Modelle (ADR-0035): Fingerabdruck-Kodierung, Letterbox und
/// Normierung (InsightFace SCRFD), Anker-Dekodierung, NMS, Ähnlichkeitstransformation, Ausschnitt, Qualität.
/// </summary>
public sealed class FaceMathTests
{
    // ---- FaceEmbeddingCodec ---------------------------------------------------------------------

    [Fact]
    public void Codec_kodiert_512_float_als_2048_Bytes_little_endian_und_zurueck()
    {
        var values = Enumerable.Range(0, 512).Select(i => (float)Math.Sin(i) * 0.1f).ToArray();
        values[0] = 1.5f;

        var blob = FaceEmbeddingCodec.Encode(values);

        blob.Length.Should().Be(2048);
        blob[..4].Should().Equal(new byte[] { 0x00, 0x00, 0xC0, 0x3F }, "1.5f = 0x3FC00000, little-endian");
        FaceEmbeddingCodec.Decode(blob).Should().Equal(values);
    }

    [Fact]
    public void Codec_prueft_Laengen_und_berechnet_Kosinus()
    {
        FluentActions.Invoking(() => FaceEmbeddingCodec.Encode(new float[511])).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => FaceEmbeddingCodec.Decode(new byte[2047])).Should().Throw<ArgumentException>();

        var a = new float[] { 3, 4 };
        FaceEmbeddingCodec.NormalizeL2(a);
        a.Should().Equal(0.6f, 0.8f);
        FaceEmbeddingCodec.Cosine(a, new float[] { 0.6f, 0.8f }).Should().BeApproximately(1f, 1e-6f);
        FaceEmbeddingCodec.Cosine(new float[] { 1, 0 }, new float[] { 0, 1 }).Should().BeApproximately(0f, 1e-6f);
        FaceEmbeddingCodec.Cosine(new float[] { 1, 0 }, new float[] { -2, 0 }).Should().BeApproximately(-1f, 1e-6f);
    }

    // ---- Letterbox / Vorverarbeitung -------------------------------------------------------------

    [Theory]
    [InlineData(1280, 960, 640, 480, 0.5f)]
    [InlineData(960, 1280, 480, 640, 0.5f)]
    [InlineData(640, 640, 640, 640, 1f)]
    [InlineData(320, 240, 640, 480, 2f)]
    [InlineData(1000, 333, 640, 213, 213f / 333f)] // InsightFace: int(640 * 333/1000) = 213, Skalierung aus der Höhe
    [InlineData(333, 1000, 213, 640, 0.64f)]
    public void Letterbox_wie_InsightFace(int w, int h, int newW, int newH, float scale)
    {
        var box = ScrfdPreprocessor.ComputeLetterbox(w, h);

        box.NewWidth.Should().Be(newW);
        box.NewHeight.Should().Be(newH);
        box.Scale.Should().BeApproximately(scale, 1e-5f);
    }

    [Fact]
    public void Fill_normiert_RGB_in_Ebenen_und_fuellt_den_Rand_schwarz()
    {
        // 4 × 2 Bild: Pixel (R, G, B) = (255, 127.5±, 0) → (0.996, ~0, −0.996)
        var pixels = new byte[4 * 2 * 3];
        for (var i = 0; i < 8; i++)
        {
            pixels[i * 3] = 255;
            pixels[i * 3 + 1] = 128;
            pixels[i * 3 + 2] = 0;
        }

        var tensor = new float[3 * 640 * 640];
        var box = ScrfdPreprocessor.Fill(new FaceImage(pixels, 4, 2), tensor);

        box.NewWidth.Should().Be(640);
        box.NewHeight.Should().Be(320);
        const int plane = 640 * 640;
        tensor[0].Should().BeApproximately((255 - 127.5f) / 128f, 1e-5f, "Ebene 0 = R");
        tensor[plane].Should().BeApproximately((128 - 127.5f) / 128f, 1e-5f, "Ebene 1 = G");
        tensor[2 * plane].Should().BeApproximately(-127.5f / 128f, 1e-5f, "Ebene 2 = B");
        tensor[319 * 640 + 639].Should().BeApproximately((255 - 127.5f) / 128f, 1e-5f, "letztes Bildpixel unten rechts");
        tensor[320 * 640].Should().BeApproximately(-127.5f / 128f, 1e-5f, "Rand unterhalb der Letterbox ist schwarz");
        tensor[2 * plane + 639 * 640 + 639].Should().BeApproximately(-127.5f / 128f, 1e-5f);
    }

    [Fact]
    public void Fill_verkleinert_um_die_Haelfte_als_Mittelwert_zweier_Pixel()
    {
        // 1280 × 2 Bild, Spalten abwechselnd 0 und 200 → bei Faktor 2 trifft jede Zielspalte genau zwischen zwei Pixel.
        var pixels = new byte[1280 * 2 * 3];
        for (var y = 0; y < 2; y++)
        {
            for (var x = 0; x < 1280; x++)
            {
                var v = (byte)(x % 2 == 0 ? 0 : 200);
                var i = (y * 1280 + x) * 3;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = v;
            }
        }

        var tensor = new float[3 * 640 * 640];
        var box = ScrfdPreprocessor.Fill(new FaceImage(pixels, 1280, 2), tensor);

        box.NewWidth.Should().Be(640);
        box.NewHeight.Should().Be(1, "int(640 · 2/1280) = 1");
        tensor[10].Should().BeApproximately((100 - 127.5f) / 128f, 1e-4f);
    }

    // ---- SCRFD-Ausgaben, Anker, NMS --------------------------------------------------------------

    [Fact]
    public void Layout_ordnet_die_neun_Ausgaben_ueber_ihre_Formen_zu_auch_mehrdeutige_Laengen()
    {
        // Reihenfolge wie det_10g (448/471/494 Score, 451/474/497 Box, 454/477/500 Landmarken), hier gemischt.
        var shapes = new List<int[]>
        {
            new[] { 3200, 4 }, new[] { 12800, 1 }, new[] { 800, 10 },
            new[] { 1, 800, 1 }, new[] { 12800, 10 }, new[] { 800, 4 },
            new[] { 3200, 1 }, new[] { 12800, 4 }, new[] { 3200, 10 },
        };

        var layout = ScrfdOutputLayout.Resolve(shapes);

        layout.IndexOf(8, ScrfdOutputKind.Score).Should().Be(1, "12800 × 1 — gleiche Länge wie 3200 × 4, aber andere Art");
        layout.IndexOf(16, ScrfdOutputKind.Box).Should().Be(0);
        layout.IndexOf(32, ScrfdOutputKind.Score).Should().Be(3, "führende Batch-Dimension wird ignoriert");
        layout.IndexOf(8, ScrfdOutputKind.Landmarks).Should().Be(4);
        layout.IndexOf(32, ScrfdOutputKind.Box).Should().Be(5);
        layout.IndexOf(16, ScrfdOutputKind.Landmarks).Should().Be(8);

        FluentActions.Invoking(() => ScrfdOutputLayout.Resolve(shapes.Take(8).ToList()))
            .Should().Throw<InvalidDataException>().WithMessage("*fehlt*");
    }

    [Fact]
    public void Anker_werden_zeilenweise_mit_zwei_Ankern_je_Position_dekodiert()
    {
        const int stride = 8;
        var count = ScrfdOutputLayout.AnchorCount(640, stride);
        count.Should().Be(12800);
        var scores = new float[count];
        var boxes = new float[count * 4];
        var kps = new float[count * 10];

        // Spalte 3, Zeile 2, zweiter Anker → Index (2·80 + 3)·2 + 1; Mitte (24, 16)
        var index = (2 * 80 + 3) * 2 + 1;
        scores[index] = 0.9f;
        scores[index - 1] = 0.49f; // unter der Schwelle
        boxes[index * 4] = 1;
        boxes[index * 4 + 1] = 2;
        boxes[index * 4 + 2] = 3;
        boxes[index * 4 + 3] = 4;
        for (var k = 0; k < 5; k++)
        {
            kps[index * 10 + k * 2] = k;
            kps[index * 10 + k * 2 + 1] = -k;
        }

        var found = new List<FaceDetection>();
        ScrfdDecoder.DecodeStride(scores, boxes, kps, stride, 640, 0.5f, found);

        found.Should().ContainSingle();
        var d = found[0];
        (d.X1, d.Y1, d.X2, d.Y2).Should().Be((16f, 0f, 48f, 48f), "Mitte ∓ Abstand × Stride (links, oben, rechts, unten)");
        d.Score.Should().Be(0.9f);
        d.Landmarks[3].Should().Be(new FacePoint(24 + 3 * 8, 16 - 3 * 8));

        var image = ScrfdDecoder.ToImage(d, 0.5f);
        (image.X1, image.Y1, image.X2, image.Y2).Should().Be((32f, 0f, 96f, 96f));
        image.Landmarks[3].Should().Be(new FacePoint(96, -16));
    }

    [Fact]
    public void Decode_vereint_Strides_rechnet_in_Bildpixel_um_und_unterdrueckt_Doppelte()
    {
        var shapes = new List<int[]>();
        var outputs = new List<float[]>();
        foreach (var kind in new[] { 1, 4, 10 })
        {
            foreach (var stride in ScrfdDecoder.Strides)
            {
                var n = ScrfdOutputLayout.AnchorCount(640, stride);
                shapes.Add(new[] { n, kind });
                outputs.Add(new float[n * kind]);
            }
        }

        var layout = ScrfdOutputLayout.Resolve(shapes);
        // Stride 16: Position (10, 10) Anker 0 und Anker 1 mit fast gleicher Box → NMS behält den besseren.
        var s16 = outputs[layout.IndexOf(16, ScrfdOutputKind.Score)];
        var b16 = outputs[layout.IndexOf(16, ScrfdOutputKind.Box)];
        var i0 = (10 * 40 + 10) * 2;
        s16[i0] = 0.8f;
        s16[i0 + 1] = 0.95f;
        for (var j = 0; j < 4; j++)
        {
            b16[i0 * 4 + j] = 2;
            b16[(i0 + 1) * 4 + j] = 2.1f;
        }

        // Stride 32: weit entfernt, bleibt
        var s32 = outputs[layout.IndexOf(32, ScrfdOutputKind.Score)];
        var b32 = outputs[layout.IndexOf(32, ScrfdOutputKind.Box)];
        s32[0] = 0.7f;
        b32[0] = b32[1] = b32[2] = b32[3] = 0.5f;

        var result = ScrfdDecoder.Decode(outputs, layout, new Letterbox(640, 480, 0.5f));

        result.Should().HaveCount(2);
        result[0].Score.Should().Be(0.95f, "nach Score sortiert, der schwächere Doppelte fällt weg");
        result[0].X1.Should().BeApproximately((160 - 2.1f * 16) / 0.5f, 1e-3f, "Bildpixel = Eingabepixel / Skalierung");
        result[1].Score.Should().Be(0.7f);
    }

    [Fact]
    public void Nms_und_IoU_mit_InsightFace_Konvention()
    {
        var a = new FaceDetection(0, 0, 9, 9, 0.9f, new FacePoint[5]);
        var b = new FaceDetection(5, 0, 14, 9, 0.8f, new FacePoint[5]);
        var c = new FaceDetection(1, 1, 10, 10, 0.7f, new FacePoint[5]);

        ScrfdDecoder.IoU(a, a).Should().Be(1f);
        ScrfdDecoder.IoU(a, b).Should().BeApproximately(50f / 150f, 1e-6f, "Breite 10 je Box (+1), Überlappung 5 × 10");

        var kept = ScrfdDecoder.Nms(new[] { c, b, a }, 0.4f);

        kept.Select(k => k.Score).Should().Equal(0.9f, 0.8f);
    }

    // ---- Ausrichtung ---------------------------------------------------------------------------

    [Fact]
    public void Aehnlichkeitstransformation_findet_bekannte_Drehung_Skalierung_Verschiebung()
    {
        var angle = 30 * MathF.PI / 180;
        var known = new SimilarityTransform(2 * MathF.Cos(angle), 2 * MathF.Sin(angle), 15, -7);
        var source = new[] { new FacePoint(10, 20), new FacePoint(40, 22), new FacePoint(25, 35), new FacePoint(14, 50), new FacePoint(37, 48) };
        var target = source.Select(known.Apply).ToArray();

        var estimated = FaceAligner.Estimate(source, target);

        estimated.A.Should().BeApproximately(known.A, 1e-4f);
        estimated.B.Should().BeApproximately(known.B, 1e-4f);
        estimated.Tx.Should().BeApproximately(15, 1e-3f);
        estimated.Ty.Should().BeApproximately(-7, 1e-3f);
        estimated.Scale.Should().BeApproximately(2, 1e-4f);
        estimated.RotationDegrees.Should().BeApproximately(30, 1e-3f);

        var back = estimated.Inverse().Apply(estimated.Apply(new FacePoint(3, 4)));
        back.X.Should().BeApproximately(3, 1e-4f);
        back.Y.Should().BeApproximately(4, 1e-4f);
    }

    [Fact]
    public void Referenzpunkte_selbst_ergeben_die_Identitaet_und_verdoppelte_Punkte_Skalierung_einhalb()
    {
        var identity = FaceAligner.Estimate(FaceAligner.ArcFaceReference, FaceAligner.ArcFaceReference);
        identity.A.Should().BeApproximately(1, 1e-5f);
        identity.B.Should().BeApproximately(0, 1e-5f);
        identity.Tx.Should().BeApproximately(0, 1e-3f);

        var doubled = FaceAligner.ArcFaceReference.Select(p => new FacePoint(p.X * 2 + 100, p.Y * 2 + 50)).ToArray();
        var t = FaceAligner.Estimate(doubled, FaceAligner.ArcFaceReference);
        t.Scale.Should().BeApproximately(0.5f, 1e-5f);
        var mapped = t.Apply(doubled[2]);
        mapped.X.Should().BeApproximately(FaceAligner.ArcFaceReference[2].X, 1e-3f);
        mapped.Y.Should().BeApproximately(FaceAligner.ArcFaceReference[2].Y, 1e-3f);
    }

    [Fact]
    public void Warp_tastet_rueckwaerts_ab_mit_schwarzem_Rand()
    {
        // 224 × 224 Bild: linke Hälfte rot, rechte grün; Gesicht doppelt so groß wie die Referenz → 112er-Ausschnitt.
        const int size = 224;
        var pixels = new byte[size * size * 3];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var i = (y * size + x) * 3;
                pixels[i] = (byte)(x < size / 2 ? 255 : 0);
                pixels[i + 1] = (byte)(x < size / 2 ? 0 : 255);
            }
        }

        var image = new FaceImage(pixels, size, size);
        var landmarks = FaceAligner.ArcFaceReference.Select(p => new FacePoint(p.X * 2, p.Y * 2)).ToArray();

        var crop = FaceAligner.AlignCrop(image, landmarks);

        crop.Length.Should().Be(112 * 112 * 3);
        crop[(50 * 112 + 10) * 3].Should().Be(255, "links bleibt rot");
        crop[(50 * 112 + 100) * 3 + 1].Should().Be(255, "rechts bleibt grün");

        // Verschoben weit nach außen → Rand schwarz
        var shifted = FaceAligner.Warp(image, new SimilarityTransform(1, 0, 500, 500));
        shifted.Should().OnlyContain(b => b == 0);
    }

    [Fact]
    public void ArcFace_Tensor_normiert_mit_127_5_in_RGB_Ebenen()
    {
        var crop = new byte[112 * 112 * 3];
        crop[0] = 255;
        crop[1] = 0;
        crop[2] = 128;
        var tensor = new float[3 * 112 * 112];

        OnnxFaceAnalyzer.FillArcFaceTensor(crop, tensor);

        tensor[0].Should().BeApproximately(1f, 1e-6f);
        tensor[112 * 112].Should().BeApproximately(-1f, 1e-6f);
        tensor[2 * 112 * 112].Should().BeApproximately(0.5f / 127.5f, 1e-6f);
    }

    // ---- Qualität ------------------------------------------------------------------------------

    [Fact]
    public void Laplace_Varianz_flach_null_Schachbrett_hoch_und_Qualitaet_begrenzt()
    {
        var flat = Enumerable.Repeat((byte)90, 112 * 112 * 3).ToArray();
        FaceQuality.LaplacianVariance(flat, 112, 112).Should().Be(0);

        var checker = new byte[112 * 112 * 3];
        for (var y = 0; y < 112; y++)
        {
            for (var x = 0; x < 112; x++)
            {
                var v = (byte)((x + y) % 2 == 0 ? 0 : 255);
                var i = (y * 112 + x) * 3;
                checker[i] = checker[i + 1] = checker[i + 2] = v;
            }
        }

        FaceQuality.LaplacianVariance(checker, 112, 112).Should().BeGreaterThan(10_000);

        var best = FaceQuality.Score(0.95f, 200, 5000);
        var worst = FaceQuality.Score(0.5f, 10, 0);
        best.Quality.Should().BeApproximately(1f, 1e-3f);
        worst.Quality.Should().Be(0f);
        FaceQuality.Score(0.7f, 60, 100).Quality.Should().BeInRange(0.2f, 0.8f);
        FaceQuality.Score(0.7f, 80, 100).Quality.Should().BeGreaterThan(FaceQuality.Score(0.7f, 60, 100).Quality, "größer ist besser");
        best.DetScore.Should().Be(0.95f);
        best.Size.Should().Be(200);
        best.Sharpness.Should().Be(5000);
        FaceQuality.FaceSize(new FaceDetection(0, 0, 40, 90, 0.9f, new FacePoint[5])).Should().BeApproximately(60, 1e-4f);
    }
}
