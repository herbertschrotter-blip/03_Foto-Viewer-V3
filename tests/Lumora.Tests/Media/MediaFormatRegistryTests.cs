using Lumora.Domain.Enums;
using Lumora.Services.Media;

namespace Lumora.Tests.Media;

public sealed class MediaFormatRegistryTests
{
    private readonly MediaFormatRegistry _registry = new();

    [Theory]
    [InlineData(".JPG", MediaType.Image)]
    [InlineData("heic", MediaType.Image)]
    [InlineData(" .WebP ", MediaType.Image)]
    [InlineData(".cr3", MediaType.Image)]
    [InlineData("mp4", MediaType.Video)]
    [InlineData(".MKV", MediaType.Video)]
    public void TryClassify_erkennt_registrierte_Endungen_unabhaengig_von_Punkt_und_Schreibweise(string extension, MediaType expected)
    {
        _registry.TryClassify(extension, out var mediaType).Should().BeTrue();
        mediaType.Should().Be(expected);
        _registry.IsSupported(extension).Should().BeTrue();
    }

    [Theory]
    [InlineData("txt")]
    [InlineData(".docx")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryClassify_lehnt_unbekannte_oder_leere_Endungen_ab(string? extension)
    {
        _registry.TryClassify(extension, out _).Should().BeFalse();
        _registry.IsSupported(extension).Should().BeFalse();
    }

    [Fact]
    public void Endungen_werden_klein_und_mit_Punkt_normalisiert()
    {
        var registry = new MediaFormatRegistry(new[] { "JPG", " .Png ", "", "jpg" }, new[] { "MP4" });

        registry.ImageExtensions.Should().BeEquivalentTo(new[] { ".jpg", ".png" });
        registry.VideoExtensions.Should().BeEquivalentTo(new[] { ".mp4" });
        registry.TryClassify("PNG", out var mediaType).Should().BeTrue();
        mediaType.Should().Be(MediaType.Image);
    }

    [Fact]
    public void Standardlisten_enthalten_die_V01_Formate()
    {
        _registry.ImageExtensions.Should().Contain(new[] { ".jpg", ".jpeg", ".png", ".webp", ".heic", ".dng" });
        _registry.VideoExtensions.Should().Contain(new[] { ".mp4", ".mov", ".mkv" });
        _registry.ImageExtensions.Overlaps(_registry.VideoExtensions).Should().BeFalse();
    }
}
