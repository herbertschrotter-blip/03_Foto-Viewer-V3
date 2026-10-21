using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PhotoViewerV3.Services.Faces;
using PhotoViewerV3.Services.Motifs;
using PhotoViewerV3.Services.Motifs.Siglip;

namespace PhotoViewerV3.Shell.Smoke;

/// <summary>
/// Rauchtest-Ersatz für die SigLIP-Encoder (ADR-0052) — ohne Modell (es darf weder ins Repo noch in den Rauchtest):
/// Text-Vektoren sind feste Zufallsvektoren je Text (Startwert aus dem Text), ein helles Bild (mittlere Helligkeit ≥ 100)
/// bekommt genau den Vektor des Standard-Begriffs „Strand“, ein dunkles den von „Nacht“ — so entstehen vorhersagbare
/// Treffer. Jedes Bild wartet <see cref="Delay"/>, lang genug für Pause/Fortsetzen. Bilder kommen über den echten WIC-Lader.
/// </summary>
internal sealed class SmokeMotifEncoderFactory : IMotifEncoderFactory
{
    public const string Version = "smoke-fake-motif/v1";
    public const string BrightTerm = "Strand";
    public const string DarkTerm = "Nacht";

    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(120);

    public string ModelVersion => Version;

    public bool IsAvailable => true;

    public MotifScoring Scoring => MotifScoring.Siglip2Base;

    public IMotifImageEncoder CreateImageEncoder(bool useGpu) => new ImageEncoder();

    public IMotifTextEncoder CreateTextEncoder() => new TextEncoder();

    /// <summary>Fester Zufallsvektor je Text (normiert).</summary>
    internal static float[] TextVector(string text)
    {
        var seed = 17;
        foreach (var c in text.ToLowerInvariant())
        {
            seed = unchecked(seed * 31 + c);
        }

        var random = new Random(seed);
        var vector = new float[SiglipModel.Dimensions];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(random.NextDouble() * 2 - 1);
        }

        MotifVectors.NormalizeL2(vector);
        return vector;
    }

    /// <summary>Vektor eines Standard-Begriffs wie im <see cref="MotifTermService"/>: Mittel aus Wort und Beschreibung.</summary>
    internal static float[] TermVector(string name)
    {
        var term = MotifDefaults.Terms.First(t => t.Name == name);
        return MotifVectors.MeanNormalized([TextVector(term.Name), TextVector(term.Prompt)]);
    }

    private sealed class ImageEncoder : IMotifImageEncoder
    {
        private readonly float[] _bright = TermVector(BrightTerm);
        private readonly float[] _dark = TermVector(DarkTerm);

        public string ModelVersion => Version;

        public string ExecutionProvider => "Rauchtest";

        public float[] EncodeImage(FaceImage image)
        {
            Thread.Sleep(Delay);
            long sum = 0;
            foreach (var b in image.Pixels)
            {
                sum += b;
            }

            return (sum / Math.Max(1, image.Pixels.Length) >= 100 ? _bright : _dark).ToArray();
        }

        public void Dispose()
        {
        }
    }

    private sealed class TextEncoder : IMotifTextEncoder
    {
        public string ModelVersion => Version;

        public IReadOnlyList<float[]> EncodeTexts(IReadOnlyList<string> texts) => texts.Select(TextVector).ToList();

        public void Dispose()
        {
        }
    }
}
