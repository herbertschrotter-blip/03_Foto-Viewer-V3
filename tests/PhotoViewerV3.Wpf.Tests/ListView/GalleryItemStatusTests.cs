using PhotoViewerV3.Gallery.Data;
using PhotoViewerV3.Gallery.ViewModels;

namespace PhotoViewerV3.Wpf.Tests.ListView;

/// <summary>Spalte „Status“ der Listenansicht: fehlt / Eingang / Archiv aus Missing und Wurzel-Rolle (ADR-0020).</summary>
public sealed class GalleryItemStatusTests
{
    [Theory]
    [InlineData(0, false, "Eingang")]
    [InlineData(1, false, "Archiv")]
    [InlineData(null, false, "")]
    [InlineData(0, true, "fehlt")]
    [InlineData(1, true, "fehlt")]
    public void StatusText_aus_Rolle_und_Missing(int? rootRole, bool missing, string expected)
    {
        var item = new GalleryItem(Dto(rootRole, missing));

        item.StatusText.Should().Be(expected);
        item.IsInbox.Should().Be(rootRole == 0);
        item.IsArchived.Should().Be(rootRole == 1);
    }

    [Fact]
    public void Missing_aendern_meldet_StatusText()
    {
        var item = new GalleryItem(Dto(1, missing: false));
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.Missing = true;

        changed.Should().Contain(nameof(GalleryItem.StatusText));
        item.StatusText.Should().Be("fehlt");
    }

    [Fact]
    public void Rollenwechsel_macht_die_Instanz_ungueltig()
    {
        var item = new GalleryItem(Dto(0, missing: false));

        item.CoreMatches(Dto(1, missing: false)).Should().BeFalse("Eingang → Archiv muss neu aufgebaut werden");
        item.CoreMatches(Dto(0, missing: false)).Should().BeTrue();
    }

    private static GalleryFileDto Dto(int? rootRole, bool missing)
        => new(7, 1, "a.jpg", ".jpg", 1, 0, 0, false, null, missing, @"C:\x", rootRole);
}
