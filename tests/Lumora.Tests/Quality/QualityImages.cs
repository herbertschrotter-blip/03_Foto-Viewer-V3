using Lumora.Services.Library;
using Lumora.Services.Quality;
using Lumora.Tests.Duplicates;

namespace Lumora.Tests.Quality;

/// <summary>Synthetische Bilder für die Güte-Tests (keine Fremd-Assets): Weichzeichnen, Abdunkeln, Aufhellen.</summary>
internal static class QualityImages
{
    /// <summary>Scharfes, detailreiches Muster (Formen-Szene plus feines Schachbrett).</summary>
    public static SyntheticImages.Rgb Detailed(int seed, int width = 512, int height = 384)
    {
        var scene = SyntheticImages.Scene(seed, width, height);
        var pixels = (byte[])scene.Pixels.Clone();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (((x / 3) + (y / 3)) % 2 == 0)
                {
                    var p = ((y * width) + x) * 3;
                    for (var c = 0; c < 3; c++)
                    {
                        pixels[p + c] = (byte)Math.Clamp(pixels[p + c] + 40, 0, 255);
                    }
                }
            }
        }

        return scene with { Pixels = pixels };
    }

    /// <summary>Kastenfilter mit Radius <paramref name="radius"/>, mehrfach (≈ Gauß).</summary>
    public static SyntheticImages.Rgb Blur(SyntheticImages.Rgb image, int radius, int passes = 2)
    {
        var current = image.Pixels;
        for (var pass = 0; pass < passes; pass++)
        {
            current = BoxBlur(current, image.Width, image.Height, radius);
        }

        return image with { Pixels = current };
    }

    /// <summary>Weichgezeichnet außer einem Rechteck (Porträt mit unscharfem Hintergrund).</summary>
    public static SyntheticImages.Rgb BlurOutside(SyntheticImages.Rgb image, int radius, double left, double top, double right, double bottom)
    {
        var blurred = Blur(image, radius);
        var pixels = (byte[])blurred.Pixels.Clone();
        for (var y = (int)(top * image.Height); y < (int)(bottom * image.Height); y++)
        {
            for (var x = (int)(left * image.Width); x < (int)(right * image.Width); x++)
            {
                var p = ((y * image.Width) + x) * 3;
                pixels[p] = image.Pixels[p];
                pixels[p + 1] = image.Pixels[p + 1];
                pixels[p + 2] = image.Pixels[p + 2];
            }
        }

        return image with { Pixels = pixels };
    }

    /// <summary>Helligkeit mal Faktor (0,15 = stark unterbelichtet, 3 = stark überbelichtet).</summary>
    public static SyntheticImages.Rgb Scale(SyntheticImages.Rgb image, double factor)
        => image with { Pixels = image.Pixels.Select(p => (byte)Math.Clamp((int)Math.Round(p * factor), 0, 255)).ToArray() };

    private static byte[] BoxBlur(byte[] source, int width, int height, int radius)
    {
        var temp = new byte[source.Length];
        var target = new byte[source.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                for (var c = 0; c < 3; c++)
                {
                    int sum = 0, count = 0;
                    for (var dx = -radius; dx <= radius; dx++)
                    {
                        var xx = Math.Clamp(x + dx, 0, width - 1);
                        sum += source[(((y * width) + xx) * 3) + c];
                        count++;
                    }

                    temp[(((y * width) + x) * 3) + c] = (byte)(sum / count);
                }
            }
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                for (var c = 0; c < 3; c++)
                {
                    int sum = 0, count = 0;
                    for (var dy = -radius; dy <= radius; dy++)
                    {
                        var yy = Math.Clamp(y + dy, 0, height - 1);
                        sum += temp[(((yy * width) + x) * 3) + c];
                        count++;
                    }

                    target[(((y * width) + x) * 3) + c] = (byte)(sum / count);
                }
            }
        }

        return target;
    }

    public static ImageQualityResult Rate(SyntheticImages.Rgb image) => ImageQuality.Analyze(image.Pixels, image.Width, image.Height);
}

/// <summary>Fester Bereich (nur <see cref="GetCurrentAsync"/> wird vom Index gebraucht).</summary>
internal sealed class FixedLibraryScope(params int[] libraryIds) : ILibraryScope
{
    public int[] LibraryIds { get; set; } = libraryIds;

    public Task<LibraryScopeInfo> GetCurrentAsync(CancellationToken ct = default)
        => Task.FromResult(LibraryIds.Length == 0
            ? LibraryScopeInfo.None
            : new LibraryScopeInfo(LibraryIds[0], 1, "Test", LibraryIds, LibraryIds, LibraryIds.Length > 1));

    public Task<LibraryScopeInfo> GetForLibraryAsync(int libraryId, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<LibraryGroupInfo>> GetGroupsAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task ShowAllLinkedAsync(int groupId, CancellationToken ct = default) => throw new NotSupportedException();

    public Task RenameGroupAsync(int groupId, string name, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>Bildwirkung-Fake: positiv = e1, negativ = e2 (Bild-Vektor e1 → ≈ 1, e2 → ≈ 0, sonst 0,5).</summary>
internal sealed class FakeQualityAesthetics(string modelVersion = "fake-motif/v1") : IQualityAesthetics
{
    public bool Available { get; set; } = true;

    public int Calls { get; private set; }

    public string EmbeddingModelVersion => modelVersion;

    public Task<AestheticModel?> GetModelAsync(CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(Available
            ? new AestheticModel(modelVersion, Lumora.Tests.Motifs.FakeMotifEncoderFactory.Unit(1), Lumora.Tests.Motifs.FakeMotifEncoderFactory.Unit(2))
            : null);
    }
}
