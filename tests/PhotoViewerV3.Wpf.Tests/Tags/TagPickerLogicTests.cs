using PhotoViewerV3.Gallery.Data;
using PhotoViewerV3.Gallery.Tags;
using PhotoViewerV3.Gallery.ViewModels;
using PhotoViewerV3.Services.Tags;

namespace PhotoViewerV3.Wpf.Tests.Tags;

/// <summary>
/// Logik rund um Tags ohne Fenster: Ergebnis des Tag-Auswahl-Dialogs (nur Änderungen, „gemischt“ belassen = keine
/// Änderung), Tag-Filter in der <see cref="GalleryQuery"/>, LIKE-Muster der Tag-Namenssuche und Tag-Anzeige am Item.
/// </summary>
public sealed class TagPickerLogicTests
{
    private static readonly TagInfo Urlaub = new(1, "Urlaub", "#E53935", 0);
    private static readonly TagInfo Familie = new(2, "Familie", "#43A047", 1);
    private static readonly TagInfo Arbeit = new(3, "Arbeit", "#1E88E5", 2);

    [Fact]
    public void ComputeResult_liefert_nur_geaenderte_Tags()
    {
        var items = new[]
        {
            new TagPickItem(Urlaub, initial: false, 0, 3) { IsChecked = true },   // neu zuweisen
            new TagPickItem(Familie, initial: true, 3, 3) { IsChecked = false },  // entfernen
            new TagPickItem(Arbeit, initial: true, 3, 3),                         // unverändert
        };

        var result = TagPickerDialog.ComputeResult(items);

        result.AssignTagIds.Should().Equal(1);
        result.RemoveTagIds.Should().Equal(2);
        result.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void Gemischter_Zustand_bleibt_ohne_Aenderung_bis_zur_Entscheidung()
    {
        var mixed = new TagPickItem(Urlaub, initial: null, 1, 3);
        mixed.AllowsMixed.Should().BeTrue();
        mixed.Hint.Should().Be("1 von 3");
        TagPickerDialog.ComputeResult([mixed]).IsEmpty.Should().BeTrue();

        mixed.IsChecked = true;
        TagPickerDialog.ComputeResult([mixed]).AssignTagIds.Should().Equal(1);

        mixed.IsChecked = false;
        TagPickerDialog.ComputeResult([mixed]).RemoveTagIds.Should().Equal(1);
    }

    [Fact]
    public void Eindeutiger_Ausgangszustand_ist_zweistufig()
    {
        new TagPickItem(Urlaub, initial: true, 2, 2).AllowsMixed.Should().BeFalse();
        new TagPickItem(Urlaub, initial: false, 0, 2).Hint.Should().BeEmpty();
    }

    [Fact]
    public void GalleryQuery_HasTagFilter()
    {
        new GalleryQuery(1, GalleryFilterState.None, null).HasTagFilter.Should().BeFalse();
        new GalleryQuery(1, GalleryFilterState.None, null, TagIds: Array.Empty<int>()).HasTagFilter.Should().BeFalse();
        new GalleryQuery(1, GalleryFilterState.None, null, TagIds: [1]).HasTagFilter.Should().BeTrue();
        new GalleryQuery(1, GalleryFilterState.None, null, TagMode: GalleryTagMode.None).HasTagFilter.Should().BeTrue("„ohne Tag“ wirkt auch ohne gewählte Tags");
    }

    [Theory]
    [InlineData("urlaub", "%urlaub%")]
    [InlineData("  50% Rabatt ", "%50\\% Rabatt%")]
    [InlineData("a_b", "%a\\_b%")]
    public void Tag_Suche_nutzt_ein_escaptes_LIKE_Muster(string term, string expected)
        => GalleryPageSource.BuildTagLikePattern(term).Should().Be(expected);

    [Fact]
    public void GalleryItem_zeigt_Tags_als_Text_und_prueft_Zugehoerigkeit()
    {
        var item = new GalleryItem(new GalleryFileDto(5, 1, "a.jpg", ".jpg", 1, 0, 0, false, null, false, @"C:\x"));
        item.HasTags.Should().BeFalse();
        item.TagsText.Should().BeEmpty();

        item.Tags = [Urlaub, Familie];
        item.HasTags.Should().BeTrue();
        item.HasTag(2).Should().BeTrue();
        item.HasTag(3).Should().BeFalse();
        item.TagsText.Should().Be("Urlaub, Familie");
    }

    [Fact]
    public void TagBrushes_liefert_eingefrorene_Pinsel_und_Grau_fuer_Ungueltiges()
    {
        var red = TagBrushes.ForHex("#e53935");
        red.IsFrozen.Should().BeTrue();
        red.Color.R.Should().Be(0xE5);
        ReferenceEquals(red, TagBrushes.ForHex("#E53935")).Should().BeTrue("Cache ohne Groß-/Kleinschreibung");
        TagBrushes.ForHex("rot").Color.Should().Be(System.Windows.Media.Color.FromRgb(0x75, 0x75, 0x75));
    }
}
