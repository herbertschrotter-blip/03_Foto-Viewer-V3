using System.Diagnostics;
using PhotoViewerV3.Services.Places;
using PhotoViewerV3.Services.Places.Clustering;
using Xunit.Abstractions;

namespace PhotoViewerV3.Tests.Places;

/// <summary>
/// Clustering der Orte-Karte (ADR-0051): Gruppen zerfallen beim Hineinzoomen, Summen stimmen auf jeder Stufe, Hierarchie
/// (jede feine Gruppe gehört zu genau einer groben), Ausschnitt-Abfrage, bestes Foto, gleiche Koordinaten, Messung mit
/// 500 000 Punkten (Aufbau &lt; 1 s angestrebt, Abfrage &lt; 10 ms).
/// </summary>
public sealed class GeoClusterIndexTests
{
    private readonly ITestOutputHelper _output;

    public GeoClusterIndexTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static ClusterInput P(int id, double lat, double lon, byte rating = 0, long sortKey = 0) => new(id, lat, lon, rating, sortKey);

    [Fact]
    public void Zwei_Staedte_sind_weit_draussen_eine_Gruppe_und_zerfallen_beim_Hineinzoomen()
    {
        // Wien und Graz (~145 km): Welt-Ansicht eine Gruppe, Landesansicht zwei Einzelfotos
        var index = GeoClusterIndex.Build([P(1, 48.2082, 16.3738), P(2, 47.0707, 15.4395)]);

        var world = index.Query(0, 0, 1, 1, 2);
        world.Should().ContainSingle().Which.Count.Should().Be(2);
        var group = world[0];
        var split = index.GetExpansionZoom(group);
        split.Should().BeInRange(3, 12);

        index.Query(0, 0, 1, 1, split - 1).Should().ContainSingle("eine Stufe vor dem Zerfall noch eine Gruppe");
        var after = index.Query(0, 0, 1, 1, split);
        after.Should().HaveCount(2).And.OnlyContain(c => c.IsSinglePhoto);
        after.Select(c => c.BestFileId).Should().BeEquivalentTo([1, 2]);
        index.GetFileIds(group).Should().BeEquivalentTo([1, 2]);
    }

    [Fact]
    public void Summen_stimmen_auf_jeder_Stufe_und_Gruppen_zerfallen_beim_Hineinzoomen()
    {
        var points = RandomClusters(new Random(7), 20_000, centers: 60, spreadDegrees: 0.5);
        var index = GeoClusterIndex.Build(points);
        index.PhotoCount.Should().Be(points.Count);

        var previous = 0;
        for (var zoom = index.MinZoom; zoom <= index.MaxZoom; zoom++)
        {
            var clusters = index.Query(0, 0, 1, 1, zoom);
            clusters.Sum(c => c.Count).Should().Be(points.Count, $"Zoom {zoom}: jede Datei genau einmal");
            var ids = clusters.SelectMany(index.GetFileIds).ToList();
            ids.Should().OnlyHaveUniqueItems().And.HaveCount(points.Count);
            clusters.Count.Should().BeGreaterThanOrEqualTo(previous, $"Zoom {zoom}: Hineinzoomen macht Gruppen nur feiner");
            previous = clusters.Count;
        }

        // Hineinzoomen bis zum Zerfall: jede Gruppe zerfällt auf ihrer Zerfalls-Stufe in mindestens zwei Marker
        foreach (var zoom in new[] { 2, 5, 8, 11 })
        {
            foreach (var group in index.Query(0, 0, 1, 1, zoom).Where(c => c.Count > 1).Take(15))
            {
                var split = index.GetExpansionZoom(group);
                if (split == int.MaxValue)
                {
                    continue;
                }

                split.Should().BeGreaterThan(zoom);
                var ids = index.GetFileIds(group).ToHashSet();
                var b = index.GetBounds(group);
                var parts = index.Query(b, split).Where(c => index.GetFileIds(c).Any(ids.Contains)).ToList();
                parts.Count.Should().BeGreaterThanOrEqualTo(2, $"Gruppe bei Zoom {zoom} zerfällt bei {split}");
                parts.Sum(c => c.Count).Should().BeGreaterThanOrEqualTo(ids.Count);
            }
        }
    }

    [Fact]
    public void Ausschnitt_liefert_nur_Gruppen_darin()
    {
        var europe = Enumerable.Range(1, 300).Select(i => P(i, 45 + (i % 10 * 0.3), 5 + (i % 17 * 0.4))).ToList();
        var america = Enumerable.Range(1001, 200).Select(i => P(i, 38 + (i % 9 * 0.2), -100 + (i % 13 * 0.5))).ToList();
        var index = GeoClusterIndex.Build([.. europe, .. america]);

        var bounds = new GeoBounds(South: 35, West: -15, North: 60, East: 30);
        for (var zoom = 2; zoom <= 10; zoom++)
        {
            var inEurope = index.Query(bounds, zoom);
            inEurope.Sum(c => c.Count).Should().Be(300, $"Zoom {zoom}: nur die europäischen Fotos");
            inEurope.SelectMany(index.GetFileIds).Should().OnlyContain(id => id <= 300);
        }

        index.Query(new GeoBounds(-50, 100, -10, 150), 5).Should().BeEmpty("in Australien gibt es keine Fotos");
    }

    [Fact]
    public void Bestes_Foto_ist_hoechste_Bewertung_sonst_neuestes()
    {
        var index = GeoClusterIndex.Build(
        [
            P(1, 48.20, 16.37, rating: 2, sortKey: 100),
            P(2, 48.21, 16.38, rating: 5, sortKey: 50),
            P(3, 48.22, 16.36, rating: 5, sortKey: 80),
            P(4, 48.19, 16.39, rating: 0, sortKey: 999),
        ]);
        index.Query(0, 0, 1, 1, 5).Should().ContainSingle().Which.BestFileId.Should().Be(3, "Bewertung 5, davon das neuere");

        var unrated = GeoClusterIndex.Build([P(1, 10, 10, sortKey: 5), P(2, 10.001, 10.001, sortKey: 9)]);
        unrated.Query(0, 0, 1, 1, 3).Single().BestFileId.Should().Be(2, "ohne Bewertung das neueste");
    }

    [Fact]
    public void Gleiche_Koordinaten_bleiben_ein_Marker_der_nie_zerfaellt()
    {
        var index = GeoClusterIndex.Build(Enumerable.Range(1, 5).Select(i => P(i, 47.5, 13.0)).ToList());
        index.SpotCount.Should().Be(1);
        var spot = index.Query(0, 0, 1, 1, index.MaxZoom).Should().ContainSingle().Subject;
        spot.Count.Should().Be(5);
        index.GetExpansionZoom(spot).Should().Be(int.MaxValue);
        index.GetExpansionZoom(index.Query(0, 0, 1, 1, 4).Single()).Should().Be(int.MaxValue);
        index.GetBounds(spot).South.Should().BeApproximately(47.5, 1e-6);
    }

    [Fact]
    public void Einzelfoto_zerfaellt_nie_und_leerer_Index_liefert_nichts()
    {
        var single = GeoClusterIndex.Build([P(42, -33.86, 151.21)]);
        var marker = single.Query(0, 0, 1, 1, 0).Single();
        marker.IsSinglePhoto.Should().BeTrue();
        single.GetExpansionZoom(marker).Should().Be(int.MaxValue);
        marker.Latitude.Should().BeApproximately(-33.86, 1e-6);
        marker.Longitude.Should().BeApproximately(151.21, 1e-6);

        GeoClusterIndex.Empty.PhotoCount.Should().Be(0);
        GeoClusterIndex.Empty.Query(0, 0, 1, 1, 10).Should().BeEmpty();
    }

    [Fact]
    public void Begrenzungsrahmen_einer_Gruppe_umfasst_ihre_Fotos()
    {
        var index = GeoClusterIndex.Build([P(1, 48.0, 16.0), P(2, 48.5, 16.6), P(3, 47.8, 15.9)]);
        var bounds = index.GetBounds(index.Query(0, 0, 1, 1, 3).Single());
        bounds.South.Should().BeApproximately(47.8, 1e-6);
        bounds.North.Should().BeApproximately(48.5, 1e-6);
        bounds.West.Should().BeApproximately(15.9, 1e-6);
        bounds.East.Should().BeApproximately(16.6, 1e-6);
    }

    [Fact]
    public void Mercator_Hin_und_Rueckweg()
    {
        MercatorProjection.LatitudeToY(0).Should().BeApproximately(0.5, 1e-12);
        MercatorProjection.LongitudeToX(-180).Should().Be(0);
        MercatorProjection.LongitudeToX(180).Should().Be(1);
        MercatorProjection.LatitudeToY(MercatorProjection.MaxLatitude).Should().BeApproximately(0, 1e-9);
        foreach (var lat in new[] { -80.0, -33.86, 0, 47.07, 85 })
        {
            MercatorProjection.YToLatitude(MercatorProjection.LatitudeToY(lat)).Should().BeApproximately(lat, 1e-9);
        }
    }

    /// <summary>
    /// Messung (Bericht/ADR-0051): 500 000 Fotos in 400 Orten mit Streuung (wie eine echte Bibliothek) und gleichmäßig über
    /// Europa verteilt (ungünstiger Fall). Gefordert: Aufbau &lt; 1 s (hier mit Puffer &lt; 2 s geprüft, weil Testrechner
    /// schwanken), Abfrage je Ausschnitt &lt; 10 ms.
    /// </summary>
    [Fact]
    public void Messung_500000_Punkte_Aufbau_und_Abfrage()
    {
        var clustered = RandomClusters(new Random(1), 500_000, centers: 400, spreadDegrees: 0.2);
        var uniform = Uniform(new Random(2), 500_000);
        foreach (var (name, points) in new[] { ("gehäuft (400 Orte)", clustered), ("gleichmäßig Europa", uniform) })
        {
            GeoClusterIndex.Build(points.Take(20_000).ToList()); // JIT aufwärmen
            var watch = Stopwatch.StartNew();
            var index = GeoClusterIndex.Build(points);
            var build = watch.Elapsed;

            var random = new Random(3);
            var queries = 0;
            var maxQuery = TimeSpan.Zero;
            var markers = 0;
            watch.Restart();
            for (var zoom = 2; zoom <= 19; zoom++)
            {
                for (var i = 0; i < 20; i++)
                {
                    var p = points[random.Next(points.Count)];
                    var span = 1200.0 / MercatorProjection.WorldSize(zoom); // Bildschirm 1200 × 800 px
                    var cx = MercatorProjection.LongitudeToX(p.Longitude);
                    var cy = MercatorProjection.LatitudeToY(p.Latitude);
                    var q = Stopwatch.StartNew();
                    markers += index.Query(cx - (span / 2), cy - (span / 3), cx + (span / 2), cy + (span / 3), zoom).Count;
                    maxQuery = q.Elapsed > maxQuery ? q.Elapsed : maxQuery;
                    queries++;
                }
            }

            var perQuery = watch.Elapsed.TotalMilliseconds / queries;
            var levels = string.Join(" ", new[] { 0, 6, 12, 18 }.Select(z => $"z{z}={index.Query(0, 0, 1, 1, z).Count:N0}"));
            _output.WriteLine($"{name}: Aufbau {build.TotalMilliseconds:N0} ms ({index.SpotCount:N0} Orte) · Abfrage Ø {perQuery:N2} ms, max {maxQuery.TotalMilliseconds:N2} ms ({queries} Abfragen, Ø {markers / queries} Marker) · Marker je Stufe {levels}");

            build.Should().BeLessThan(TimeSpan.FromSeconds(2), "Ziel < 1 s (gemessen im Debug-Build ~0,3 s); Puffer für parallel laufende Testläufe");
            perQuery.Should().BeLessThan(10);
            index.Query(0, 0, 1, 1, 0).Sum(c => c.Count).Should().Be(500_000);
        }
    }

    internal static List<ClusterInput> RandomClusters(Random random, int count, int centers, double spreadDegrees)
    {
        var hubs = Enumerable.Range(0, centers).Select(_ => (Lat: (random.NextDouble() * 120) - 55, Lon: (random.NextDouble() * 340) - 170)).ToArray();
        var points = new List<ClusterInput>(count);
        for (var i = 0; i < count; i++)
        {
            var hub = hubs[(int)(Math.Pow(random.NextDouble(), 2) * centers)]; // wenige Orte mit sehr vielen Fotos
            var lat = hub.Lat + (Gauss(random) * spreadDegrees);
            var lon = hub.Lon + (Gauss(random) * spreadDegrees);
            points.Add(new ClusterInput(i + 1, lat, lon, (byte)random.Next(0, 6), random.Next()));
        }

        return points;
    }

    private static List<ClusterInput> Uniform(Random random, int count)
        => Enumerable.Range(1, count)
            .Select(i => new ClusterInput(i, 36 + (random.NextDouble() * 34), -10 + (random.NextDouble() * 40), 0, i))
            .ToList();

    private static double Gauss(Random random)
        => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
}
