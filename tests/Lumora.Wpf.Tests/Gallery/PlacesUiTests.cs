using Lumora.Gallery.Places;
using Lumora.Services.Places;

namespace Lumora.Wpf.Tests.Gallery;

/// <summary>
/// Oberfläche zu Orten ohne Fenster (ADR-0050): Flyout-Liste Land → Region → Ort mit Zählern, Suche und Aufklappen;
/// Dialog-Logik „Ort setzen“ (Suche, Koordinaten-Eingabe in mehreren Schreibweisen, zuletzt verwendete Orte, Entfernen).
/// </summary>
public sealed class PlacesUiTests
{
    private static readonly ReverseGeocoder Geocoder = new();

    private static int PlaceAt(double lat, double lon) => Geocoder.Nearest(lat, lon)!.PlaceId;

    [Fact]
    public void Flyout_Liste_nach_Land_Region_Ort_mit_Zaehlern()
    {
        var wien = PlaceAt(48.2082, 16.3738);
        var graz = PlaceAt(47.0707, 15.4395);
        var rom = PlaceAt(41.9028, 12.4964);
        var summary = new PlaceCountSummary([(wien, 5), (graz, 2), (rom, 1), (999_999_999, 4)], WithoutLocation: 7, WithoutPlaceName: 1);

        var entries = PlaceFilterEntry.Build(summary, Geocoder);

        entries.Select(e => e.ToString()).Should().Equal(
            "Österreich (7)",
            "  Wien (5)",
            "    Wien (5)",
            "  Steiermark (2)",
            "    Graz (2)",
            "Italien (1)",
            "  Latium (1)",
            "    Rom (1)",
            "ohne Ortsnamen (Koordinaten abseits von Orten) (1)",
            "ohne Ort (7)");
        entries[0].PlaceIds.Should().BeEquivalentTo([wien, graz]);
        entries[0].IsExpanded.Should().BeTrue("wenige Länder: aufgeklappt");
        entries[6].IsExpanded.Should().BeTrue("Land mit nur einer Region zeigt die Orte direkt");
    }

    [Fact]
    public void Flyout_Sichtbarkeit_beim_Aufklappen_und_Suchen()
    {
        var summary = new PlaceCountSummary([(PlaceAt(48.2082, 16.3738), 5), (PlaceAt(47.0707, 15.4395), 2)], 0, 0);
        var entries = PlaceFilterEntry.Build(summary, Geocoder);
        PlaceFilterEntry.UpdateVisibility(entries, null);
        entries.Where(e => e.IsVisible).Select(e => e.Name).Should().Equal("Österreich", "Wien", "Steiermark", "ohne Ort");

        entries.First(e => e.Name == "Steiermark").IsExpanded = true;
        PlaceFilterEntry.UpdateVisibility(entries, null);
        entries.Where(e => e.IsVisible).Select(e => e.Name).Should().Contain("Graz");

        PlaceFilterEntry.UpdateVisibility(entries, "gra");
        entries.Where(e => e.IsVisible).Select(e => e.Name).Should().Equal("Österreich", "Steiermark", "Graz");
    }

    private static SetPlaceViewModel Dialog(FileGeoInfo? current = null, IReadOnlyList<RecentLocation>? recent = null)
        => new(new PlaceDialogRequest("Auswahl", 2, current, recent ?? []), Geocoder);

    [Theory]
    [InlineData("48,2082", "16,3738")]
    [InlineData("48.2082", "16.3738")]
    [InlineData("48,2082 N", "16,3738 O")]
    [InlineData("48°12'29,52\"N", "16°22'25,68\"E")]
    public void Koordinaten_Schreibweisen(string lat, string lon)
    {
        var vm = Dialog();
        vm.LatitudeText = lat;
        vm.LongitudeText = lon;

        vm.CanAccept.Should().BeTrue();
        vm.Coordinate!.Value.Latitude.Should().BeApproximately(48.2082, 1e-4);
        vm.Coordinate.Value.Longitude.Should().BeApproximately(16.3738, 1e-4);
        vm.PreviewText.Should().StartWith("→ Wien, Österreich (48,2082° N, 16,3738° O");
        vm.BuildResult().Should().Be(PlaceDialogResult.Set(vm.Coordinate.Value.Latitude, vm.Coordinate.Value.Longitude));
    }

    [Fact]
    public void Paar_in_einem_Feld_wird_aufgeteilt_ungueltiges_abgelehnt()
    {
        var vm = Dialog();
        vm.LatitudeText = "48.2082, 16.3738";
        vm.LatitudeText.Should().Be("48.2082");
        vm.LongitudeText.Should().Be("16.3738");

        vm.LatitudeText = "95";
        vm.CanAccept.Should().BeFalse();
        vm.ErrorText.Should().Contain("ungültig");
        vm.BuildResult().Should().BeNull();
    }

    [Fact]
    public void Suche_Treffer_Recent_und_Entfernen()
    {
        var rom = new RecentLocation(41.9028, 12.4964, Geocoder.Nearest(41.9028, 12.4964), "Rom, Italien");
        var vm = Dialog(new FileGeoInfo(1, 48.2082, 16.3738, Domain.Enums.GeoSource.Exif, Geocoder.Nearest(48.2082, 16.3738)), [rom]);
        vm.CurrentText.Should().Be("Bisher: Wien, Österreich (48,2082° N, 16,3738° O) · EXIF (erste Datei)");
        vm.LatitudeText.Should().Be("48,2082", "Vorbelegung mit dem bisherigen Ort");

        vm.SearchText = "Graz";
        vm.HasSearchResults.Should().BeTrue();
        vm.SelectedHit = vm.SearchResults[0];
        vm.PreviewText.Should().StartWith("→ Graz, Österreich");

        vm.UseRecentCommand.Execute(rom);
        vm.PreviewText.Should().StartWith("→ Rom, Italien");

        vm.KeepModifiedTime = false;
        vm.BuildResult()!.KeepModifiedTime.Should().BeFalse();

        vm.IsRemove = true;
        vm.AcceptText.Should().Be("Ort entfernen");
        vm.BuildResult().Should().Be(PlaceDialogResult.Clear());
        vm.HasMapPicker.Should().BeFalse("ohne Karte kein Knopf");
    }

    [Fact]
    public void Ergebnistext()
    {
        PlaceActions.Describe("Ort gesetzt", new GeoTagResult(10, 2, 1, ["x: y"]))
            .Should().Be("Ort gesetzt: 12 Datei(en) (2 nur im Katalog), 1 fehlgeschlagen — Rückgängig möglich.");
        PlaceActions.Describe("Ort entfernt", new GeoTagResult(0, 0, 2, ["a", "b"])).Should().Be("Ort entfernt: 0 Datei(en), 2 fehlgeschlagen.");
    }
}
