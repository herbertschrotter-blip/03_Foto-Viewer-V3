using System.Diagnostics;
using PhotoViewerV3.Services.Places;

namespace PhotoViewerV3.Tests.Places;

/// <summary>Ortsnamen offline aus den eingebetteten GeoNames-Daten (ADR-0050): nächster Ort, deutsche Namen, Suche, Tempo.</summary>
public sealed class ReverseGeocoderTests
{
    private static readonly ReverseGeocoder Geocoder = new();

    [Theory]
    [InlineData(48.2082, 16.3738, "Wien", "Wien", "Österreich")]
    [InlineData(47.0707, 15.4395, "Graz", "Steiermark", "Österreich")]
    [InlineData(48.1372, 11.5756, "München", "Bayern", "Deutschland")]
    [InlineData(41.9028, 12.4964, "Rom", "Latium", "Italien")]
    [InlineData(40.7128, -74.0060, "New York", null, "Vereinigte Staaten")]
    public void Naechster_Ort_mit_deutschen_Namen(double lat, double lon, string name, string? region, string country)
    {
        var place = Geocoder.Nearest(lat, lon);

        place.Should().NotBeNull();
        place!.Name.Should().Be(name);
        place.Country.Should().Be(country);
        if (region is not null)
        {
            place.Region.Should().Be(region);
        }
    }

    [Fact]
    public void Mitten_im_Meer_und_ungueltig_kein_Ort()
    {
        Geocoder.Nearest(35.0, -40.0).Should().BeNull("Atlantik");
        Geocoder.Nearest(double.NaN, 10).Should().BeNull();
        Geocoder.Nearest(91, 10).Should().BeNull();
    }

    [Fact]
    public void Ueber_die_Datumsgrenze()
    {
        // Taveuni (Fidschi) liegt bei 179,9° O — von −179,99° aus über die Datumsgrenze am nächsten.
        var place = Geocoder.Nearest(-16.8, -179.99);
        place.Should().NotBeNull();
        place!.CountryCode.Should().Be("FJ");
    }

    [Fact]
    public void Ort_ueber_die_Id_wiederfinden()
    {
        var wien = Geocoder.Nearest(48.2082, 16.3738)!;
        Geocoder.GetPlace(wien.PlaceId).Should().Be(wien);
        Geocoder.GetPlace(-1).Should().BeNull();
        Geocoder.CountryName("AT").Should().Be("Österreich");
    }

    [Theory]
    [InlineData("Wien", "Wien", "AT")]
    [InlineData("vienna", "Wien", "AT")]
    [InlineData("munchen", "München", "DE")]
    [InlineData("Muenchen", "München", "DE")]
    [InlineData("Paris, Frankreich", "Paris", "FR")]
    [InlineData("Paris, US", "Paris", "US")]
    [InlineData("Köln", "Köln", "DE")]
    public void Suche_findet_den_wichtigsten_Treffer_zuerst(string text, string name, string country)
    {
        var hits = Geocoder.Search(text, 5);

        hits.Should().NotBeEmpty();
        hits[0].Place.Name.Should().Be(name);
        hits[0].Place.CountryCode.Should().Be(country);
    }

    [Fact]
    public void Suche_leer_und_Grenze()
    {
        Geocoder.Search("", 5).Should().BeEmpty();
        Geocoder.Search("a", 0).Should().BeEmpty();
        Geocoder.Search("San", 7).Should().HaveCount(7);
    }

    [Fact]
    public void Nearest_bleibt_unter_einer_zehntel_Millisekunde()
    {
        _ = Geocoder.Nearest(48, 16); // Daten laden
        var random = new Random(4711);
        var points = Enumerable.Range(0, 20_000).Select(_ => (Lat: (random.NextDouble() * 120) - 55, Lon: (random.NextDouble() * 360) - 180)).ToList();
        var sw = Stopwatch.StartNew();
        var found = 0;
        foreach (var (lat, lon) in points)
        {
            if (Geocoder.Nearest(lat, lon) is not null)
            {
                found++;
            }
        }

        sw.Stop();
        var perCallMs = sw.Elapsed.TotalMilliseconds / points.Count;
        found.Should().BeGreaterThan(1000);
        perCallMs.Should().BeLessThan(0.1, $"gemessen {perCallMs * 1000:N1} µs je Aufruf");
    }
}
