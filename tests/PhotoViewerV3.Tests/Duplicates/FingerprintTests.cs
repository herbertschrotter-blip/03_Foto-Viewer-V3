using System.Numerics;
using System.Security.Cryptography;
using PhotoViewerV3.Services.Duplicates;

namespace PhotoViewerV3.Tests.Duplicates;

/// <summary>
/// Inhalts-Prüfsumme und Bild-Fingerabdruck (ADR-0053): SHA-256 gleich/ungleich, pHash stabil gegen Verkleinern, neu
/// Kodieren, leichtes Beschneiden und Helligkeit — fern bei anderem Motiv; einfarbige Bilder als „fast einfarbig“.
/// </summary>
public sealed class FingerprintTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PhotoViewerV3.Tests", "fp-" + Guid.NewGuid().ToString("N"));

    public FingerprintTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Pruefsumme_ist_SHA256_und_trennt_gleich_von_ungleich()
    {
        var bytes = Enumerable.Range(0, 3_000_000).Select(i => (byte)(i * 31 % 251)).ToArray();
        var a = Write("a.bin", bytes);
        var b = Write("b.bin", bytes);
        var changed = (byte[])bytes.Clone();
        changed[^1] ^= 1;
        var c = Write("c.bin", changed);

        var hashA = ContentHasher.Compute(a);
        hashA.Should().Equal(SHA256.HashData(bytes), "über alle Bytes, auch über mehrere Lesepuffer");
        ContentHasher.Compute(b).Should().Equal(hashA);
        ContentHasher.Compute(c).Should().NotEqual(hashA, "ein einziges anderes Bit am Ende");
        ContentHasher.ToHex(hashA).Should().HaveLength(64);
    }

    [Fact]
    public void Pruefsumme_liest_geteilt_waehrend_die_Datei_offen_ist()
    {
        var path = Write("offen.bin", new byte[1000]);
        using var open = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        ContentHasher.Compute(path).Should().Equal(SHA256.HashData(new byte[1000]));
    }

    private static ImageFingerprint Fp(SyntheticImages.Rgb image) => PerceptualHash.Compute(image.Pixels, image.Width, image.Height);

    private static int Distance(SyntheticImages.Rgb a, SyntheticImages.Rgb b) => PerceptualHash.Distance(Fp(a).Hash, Fp(b).Hash);

    [Theory]
    [InlineData(11)]
    [InlineData(42)]
    [InlineData(1975)]
    public void pHash_bleibt_nah_bei_Abwandlungen_desselben_Fotos(int seed)
    {
        var original = SyntheticImages.Scene(seed);

        Distance(original, original).Should().Be(0);
        Distance(original, SyntheticImages.Downscale(original, 160, 120)).Should().BeLessThanOrEqualTo(4, "verkleinert (Vorschaubild, WhatsApp)");
        Distance(original, SyntheticImages.Recode(original)).Should().BeLessThanOrEqualTo(4, "neu gespeichert");
        Distance(original, SyntheticImages.Recode(SyntheticImages.Downscale(original, 320, 240), 7)).Should().BeLessThanOrEqualTo(6, "verkleinert und neu gespeichert");
        Distance(original, SyntheticImages.Brighten(original, 12)).Should().BeLessThanOrEqualTo(4, "etwas heller");
        Distance(original, SyntheticImages.Crop(original, 0.03)).Should().BeLessThanOrEqualTo(DuplicateSettings.DefaultSimilarThreshold, "leicht beschnitten");
    }

    [Fact]
    public void pHash_ist_fern_bei_anderem_Motiv()
    {
        var scenes = Enumerable.Range(1, 12).Select(s => Fp(SyntheticImages.Scene(s * 101, 320, 240)).Hash).ToList();
        var distances = new List<int>();
        for (var i = 0; i < scenes.Count; i++)
        {
            for (var j = i + 1; j < scenes.Count; j++)
            {
                distances.Add(PerceptualHash.Distance(scenes[i], scenes[j]));
            }
        }

        distances.Min().Should().BeGreaterThan(DuplicateSettings.DefaultSimilarThreshold, "verschiedene Motive gelten nie als ähnlich");
        distances.Average().Should().BeInRange(20, 44, "im Mittel etwa die Hälfte der 64 Bits");
    }

    [Fact]
    public void pHash_hat_32_gesetzte_Bits_und_erkennt_einfarbige_Bilder()
    {
        var scene = Fp(SyntheticImages.Scene(5));
        BitOperations.PopCount(scene.Hash).Should().BeInRange(28, 36, "Median-Schwelle teilt die 64 Koeffizienten etwa hälftig");
        scene.LowDetail.Should().BeFalse();

        Fp(SyntheticImages.Flat(0)).LowDetail.Should().BeTrue("schwarzes Foto");
        Fp(SyntheticImages.Flat(250)).LowDetail.Should().BeTrue("weiße Seite");
    }

    [Fact]
    public void pHash_rechnet_auch_kleine_und_schmale_Bilder()
    {
        var tiny = SyntheticImages.Downscale(SyntheticImages.Scene(3), 20, 15);
        var act = () => Fp(tiny);
        act.Should().NotThrow();
        Fp(SyntheticImages.Downscale(SyntheticImages.Scene(3), 400, 30)).Hash.Should().NotBe(0UL);
    }

    [Fact]
    public void Gespeicherte_Form_ist_verlustfrei()
    {
        foreach (var hash in new[] { 0UL, 1UL, ulong.MaxValue, 0x8000_0000_0000_0000UL, 0x1234_5678_9ABC_DEF0UL })
        {
            PerceptualHash.FromStored(PerceptualHash.ToStored(hash)).Should().Be(hash);
        }
    }
}
