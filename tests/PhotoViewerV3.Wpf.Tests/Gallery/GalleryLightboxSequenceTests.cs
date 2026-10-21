using PhotoViewerV3.Gallery.ViewModels;
using PhotoViewerV3.Wpf.Tests.Fixtures;

namespace PhotoViewerV3.Wpf.Tests.Gallery;

/// <summary>
/// Start der Lightbox aus der Page (0.2.42): per Referenz, sonst per FileId; ein Item, das gar nicht in der Page steht,
/// öffnet allein statt still bei Index 0 zu starten.
/// </summary>
public sealed class GalleryLightboxSequenceTests
{
    private static readonly List<GalleryItem> Page = Enumerable.Range(0, 256).Select(GalleryGridHost.CreateItem).ToList();

    [Fact]
    public void Item_per_Referenz_startet_an_seiner_Position()
    {
        var (entries, start, match) = GalleryViewModel.BuildLightboxSequence(Page, Page[12]);

        match.Should().Be(GalleryViewModel.LightboxStartMatch.Reference);
        start.Should().Be(12);
        entries.Should().HaveCount(256);
        entries[start].FileId.Should().Be(Page[12].Id);
        entries.Select(e => e.FileId).Should().Equal(Page.Select(i => i.Id));
    }

    [Fact]
    public void Andere_Instanz_mit_gleicher_FileId_startet_an_der_Position_dieser_Datei()
    {
        var copy = GalleryGridHost.CreateItem(72); // gleiche Id wie Page[72], andere Instanz (z. B. nach Reload)

        var (entries, start, match) = GalleryViewModel.BuildLightboxSequence(Page, copy);

        match.Should().Be(GalleryViewModel.LightboxStartMatch.Id);
        start.Should().Be(72);
        entries[start].FileId.Should().Be(copy.Id);
        entries.Should().HaveCount(256);
    }

    [Fact]
    public void Item_ausserhalb_der_Page_oeffnet_allein_statt_bei_Index_0()
    {
        var foreign = GalleryGridHost.CreateItem(5000);

        var (entries, start, match) = GalleryViewModel.BuildLightboxSequence(Page, foreign);

        match.Should().Be(GalleryViewModel.LightboxStartMatch.NotInPage);
        start.Should().Be(0);
        entries.Should().ContainSingle().Which.FileId.Should().Be(foreign.Id);
        entries[0].Name.Should().Be(foreign.Name);
    }
}
