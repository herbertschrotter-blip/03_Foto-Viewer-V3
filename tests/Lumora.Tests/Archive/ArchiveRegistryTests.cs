using Lumora.Domain.Enums;
using Lumora.Services.Media;

namespace Lumora.Tests.Archive;

public sealed class ArchiveRegistryTests
{
    private readonly MediaFormatRegistry _registry = new();

    [Theory]
    [InlineData(".zip")]
    [InlineData("RAR")]
    [InlineData(" .7z ")]
    [InlineData(".tar")]
    [InlineData(".gz")]
    [InlineData(".tgz")]
    [InlineData(".bz2")]
    [InlineData(".XZ")]
    public void TryClassify_liefert_Archive_fuer_Archiv_Endungen(string extension)
    {
        _registry.TryClassify(extension, out var mediaType).Should().BeTrue();
        mediaType.Should().Be(MediaType.Archive);
        _registry.IsSupported(extension).Should().BeTrue();
        _registry.IsMedia(extension).Should().BeFalse("Archive sind keine Fotos oder Videos");
    }

    [Fact]
    public void IsMedia_gilt_nur_fuer_Bild_und_Video()
    {
        _registry.IsMedia(".jpg").Should().BeTrue();
        _registry.IsMedia("mp4").Should().BeTrue();
        _registry.IsMedia(".txt").Should().BeFalse();
        _registry.IsMedia(null).Should().BeFalse();
    }

    [Fact]
    public void Archivliste_ist_disjunkt_zu_Bild_und_Video_und_Standard_bleibt_bei_eigenen_Listen()
    {
        _registry.ArchiveExtensions.Should().BeEquivalentTo(MediaFormatRegistry.DefaultArchiveExtensions);
        _registry.ArchiveExtensions.Overlaps(_registry.ImageExtensions).Should().BeFalse();
        _registry.ArchiveExtensions.Overlaps(_registry.VideoExtensions).Should().BeFalse();

        var custom = new MediaFormatRegistry(new[] { "jpg" }, new[] { "mp4" });
        custom.TryClassify(".zip", out var mediaType).Should().BeTrue();
        mediaType.Should().Be(MediaType.Archive);

        var none = new MediaFormatRegistry(new[] { "jpg" }, new[] { "mp4" }, Array.Empty<string>());
        none.IsSupported(".zip").Should().BeFalse();
    }
}
