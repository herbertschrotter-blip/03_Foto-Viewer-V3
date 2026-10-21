using Lumora.Gallery.ListView;
using Lumora.Gallery.ViewModels;
using Lumora.Settings.CategoryManagement;

namespace Lumora.Wpf.Tests.Categories;

/// <summary>
/// Kategorie-Spalten in den Spalten-Einstellungen der Listenansicht (ADR-0027): Token „Kategorie:{Id}“ (sichtbar, mit
/// Reihenfolge) und „!Kategorie:{Id}“ (ausgeblendet); neue Kategorien ohne Token sind sichtbar. Dazu Indexer der
/// Item-Werte und das Zerlegen der Werteingabe der Verwaltungsseite.
/// </summary>
[Collection(Lumora.Wpf.Tests.Fixtures.UiTestCollection.Name)]
public sealed class CategoryColumnSettingsTests
{
    [Fact]
    public void Alte_Einstellung_ohne_Kategorien_bleibt_gleich_und_zeigt_jede_Kategorie()
    {
        var settings = GalleryListColumnSettings.Parse("Name,Size,Tags");

        settings.Serialize().Should().Be("Name,Size,Tags");
        settings.IsCategoryVisible(5).Should().BeTrue("eine neue Kategorie erscheint automatisch");
        settings.Visible.Should().Equal(GalleryListColumnKey.Name, GalleryListColumnKey.Size, GalleryListColumnKey.Tags);
    }

    [Fact]
    public void Kategorie_ausblenden_und_wieder_einblenden_wird_gespeichert()
    {
        var hidden = GalleryListColumnSettings.Parse("Name,Kategorie:5,Size").WithCategoryVisibility(5, false);

        hidden.Serialize().Should().Be("Name,Size,!Kategorie:5");
        GalleryListColumnSettings.Parse(hidden.Serialize()).IsCategoryVisible(5).Should().BeFalse();

        var shown = hidden.WithCategoryVisibility(5, true);
        shown.IsCategoryVisible(5).Should().BeTrue();
        shown.VisibleTokens.Should().Equal("Name", "Size", "Kategorie:5");
    }

    [Fact]
    public void Reihenfolge_mit_Kategorie_Spalten_und_fremde_Token_werden_ignoriert()
    {
        var settings = GalleryListColumnSettings.Parse("size, kategorie:7, Gibtsnicht, Kategorie:x, !Kategorie:3, Kategorie:3")
            .WithTokenOrder(["Kategorie:7", "Name", "Kategorie:9", "Size"]);

        settings.VisibleTokens.Should().Equal("Kategorie:7", "Name", "Kategorie:9", "Size");
        settings.IsCategoryVisible(3).Should().BeFalse("ausgeblendet hat Vorrang vor einem sichtbaren Token");
        settings.Visible.Should().Equal(GalleryListColumnKey.Name, GalleryListColumnKey.Size);
    }

    [Fact]
    public void Indexer_der_Item_Werte_liefert_leer_ohne_Wert()
    {
        var values = new GalleryItemCategories(new Dictionary<int, (int, string)> { [5] = (11, "NN") });

        values[5].Should().Be("NN");
        values[6].Should().BeEmpty();
        values.ValueIdOf(5).Should().Be(11);
        values.ValueIdOf(6).Should().BeNull();
        GalleryItemCategories.Empty.Count.Should().Be(0);
    }

    [Fact]
    public void Kategorie_Spalte_sortiert_ueber_den_Indexer_Pfad()
    {
        Lumora.Wpf.Tests.Fixtures.TestUiThread.Run(() =>
        {
            var items = Enumerable.Range(0, 3).Select(Lumora.Wpf.Tests.Fixtures.GalleryGridHost.CreateItem).ToList();
            items[0].Categories = new GalleryItemCategories(new Dictionary<int, (int, string)> { [5] = (1, "NN") });
            items[2].Categories = new GalleryItemCategories(new Dictionary<int, (int, string)> { [5] = (2, "N") });
            var view = new System.Windows.Data.ListCollectionView(items);

            view.SortDescriptions.Add(new System.ComponentModel.SortDescription("Categories[5]", System.ComponentModel.ListSortDirection.Ascending));

            view.Cast<GalleryItem>().Select(i => i.Categories[5]).Should().Equal(string.Empty, "N", "NN");
            return true;
        }, TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData("N, NN", new[] { "N", "NN" })]
    [InlineData(" N ;NN,, ", new[] { "N", "NN" })]
    [InlineData("", new string[0])]
    public void Werteingabe_wird_an_Komma_und_Semikolon_zerlegt(string input, string[] expected)
        => CategoryManagementViewModel.SplitValues(input).Should().Equal(expected);
}
