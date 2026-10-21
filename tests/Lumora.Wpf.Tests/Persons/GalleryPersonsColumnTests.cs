using Lumora.Gallery.Data;
using Lumora.Gallery.ListView;
using Lumora.Gallery.ViewModels;

namespace Lumora.Wpf.Tests.Persons;

/// <summary>Listenspalte „Personen“ und Kachel-Daten (0.2.38): Texte, Vorschläge mit „?“, Sortierschlüssel, Spalten-Einstellung.</summary>
public sealed class GalleryPersonsColumnTests
{
    [Fact]
    public void Spalte_Personen_sortiert_nur_innerhalb_der_Seite_und_ist_einblendbar()
    {
        var column = GalleryListColumns.Get(GalleryListColumnKey.Persons);
        column.Header.Should().Be("Personen");
        column.SortsServerSide.Should().BeFalse();
        GalleryListColumns.NextSort(GalleryListColumnKey.Persons, GallerySort.NameAsc).Should().BeNull();

        GalleryListColumnSettings.Default.IsVisible(GalleryListColumnKey.Persons).Should().BeTrue("Standard zeigt alle Spalten");
        GalleryListColumnSettings.Parse("Name,Tags").IsVisible(GalleryListColumnKey.Persons).Should().BeFalse("gespeicherte Auswahl ohne Personen bleibt");
        GalleryListColumnSettings.Parse("Name,Tags").WithVisibility(GalleryListColumnKey.Persons, true).Serialize().Should().Be("Name,Tags,Persons");
    }

    [Fact]
    public void Bestaetigte_Namen_und_Vorschlaege_mit_Fragezeichen()
    {
        var persons = GalleryItemPersons.From(
        [
            new GalleryFacePersonRow(1, true, 2, "Bert"),
            new GalleryFacePersonRow(1, true, 1, "anna"),
            new GalleryFacePersonRow(1, false, 3, "Carla"),
            new GalleryFacePersonRow(1, true, 2, "Bert"),
        ]);

        persons.Confirmed.Should().Equal("anna", "Bert");
        persons.ConfirmedCount.Should().Be(2);
        persons.ConfirmedText.Should().Be("anna, Bert");
        persons.SuggestionsText.Should().Be("Carla?");
        persons.Separator.Should().Be(", ");
        persons.ToString().Should().Be("anna, Bert, Carla?");
        persons.ToolTip.Should().Contain("Personen: anna, Bert").And.Contain("Vorschläge: Carla?");

        var onlySuggested = GalleryItemPersons.From([new GalleryFacePersonRow(1, false, 3, "Carla")]);
        onlySuggested.HasConfirmed.Should().BeFalse("Kachel-Symbol nur bei bestätigten Personen");
        onlySuggested.Separator.Should().BeEmpty();
        var keys = new object[] { GalleryItemPersons.Empty.SortKey, onlySuggested.SortKey, persons.SortKey }.Order(Comparer<object>.Default).ToList();
        keys.Should().Equal([persons.SortKey, onlySuggested.SortKey, GalleryItemPersons.Empty.SortKey], "bestätigte Namen, dann Vorschläge, dann leer");
        GalleryItemPersons.Empty.SortKey.Text.Should().BeEmpty();
        GalleryItemPersons.Empty.ToolTip.Should().Be("Keine Personen");
    }
}
