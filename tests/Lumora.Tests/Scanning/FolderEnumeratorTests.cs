using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Services.Media;
using Lumora.Services.Scanning;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Scanning;

public sealed class FolderEnumeratorTests
{
    private static FolderEnumerator CreateEnumerator()
        => new(new MediaFormatRegistry(), NullLogger<FolderEnumerator>.Instance);

    [Fact]
    public async Task Enumerate_findet_nur_registrierte_Endungen_rekursiv()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        fx.WriteLibraryFile(Path.Combine("a", "one.jpg"));
        fx.WriteLibraryFile(Path.Combine("a", "two.PNG"));
        fx.WriteLibraryFile(Path.Combine("a", "b", "clip.mp4"));
        fx.WriteLibraryFile(Path.Combine("a", "notes.txt"));
        fx.WriteLibraryFile(Path.Combine("a", "b", "doc.docx"));
        fx.WriteLibraryFile("readme");

        var files = CreateEnumerator().Enumerate(fx.LibraryRoot).ToList();

        files.Select(f => f.Name).Should().BeEquivalentTo(new[] { "one.jpg", "two.PNG", "clip.mp4" });

        var png = files.Single(f => f.Name == "two.PNG");
        png.Extension.Should().Be(".png");
        png.MediaType.Should().Be(MediaType.Image);
        png.FolderPath.Should().Be(Path.Combine(fx.LibraryRoot, "a"));
        png.FullPath.Should().Be(Path.Combine(fx.LibraryRoot, "a", "two.PNG"));
        png.Size.Should().Be(2048);

        var clip = files.Single(f => f.Name == "clip.mp4");
        clip.MediaType.Should().Be(MediaType.Video);
        clip.FolderPath.Should().Be(Path.Combine(fx.LibraryRoot, "a", "b"));
    }

    [Fact]
    public void Enumerate_liefert_nichts_fuer_fehlenden_Root()
    {
        var missing = Path.Combine(Path.GetTempPath(), "Lumora.Tests", "gibt-es-nicht-" + Guid.NewGuid().ToString("N"));

        CreateEnumerator().Enumerate(missing).Should().BeEmpty();
    }

    [Fact]
    public async Task Enumerate_bricht_bei_Cancellation_ab()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        fx.WriteLibraryFile("one.jpg");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => CreateEnumerator().Enumerate(fx.LibraryRoot, cts.Token).ToList();

        act.Should().Throw<OperationCanceledException>();
    }
}
