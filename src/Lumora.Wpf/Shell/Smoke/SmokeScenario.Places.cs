using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Lumora.Gallery.ViewModels;
using Lumora.Places;
using Lumora.Places.Dialogs;
using Lumora.Places.Map.Tiles;
using Lumora.Places.ViewModels;
using Lumora.Services.Messaging;
using Lumora.Settings;
using Wpf.Ui.Appearance;

namespace Lumora.Shell.Smoke;

/// <summary>Rauchtest-Fake für „Fotos verorten“: vorgegebene Antwort, Anfragen werden gemerkt.</summary>
internal sealed class SmokePlacesDialogs : IPlacesDialogs
{
    public AssignLocationAnswer? Next { get; set; } = new(KeepModifiedTime: true);

    public List<AssignLocationRequest> Requests { get; } = [];

    public Task<AssignLocationAnswer?> ConfirmAssignAsync(AssignLocationRequest request, Window? owner)
    {
        Requests.Add(request);
        return Task.FromResult(Next);
    }
}

/// <summary>
/// Rauchtest-Schritt „Orte-Karte“ (0.2.54, ADR-0051) — Kachelquelle abgeschaltet (kein Netz, keine OSM-Abrufe):
/// Seite öffnen, drei Fotos aus „Ohne Ort“ per Auswahl + „Hier verorten“ nach Wien und zwei per Ablegen nach Graz
/// verorten (Fake-Dialog, <c>IGeoTagService</c>), Weltansicht = eine Gruppe, Klick zoomt hinein bis sie zerfällt,
/// „In der Galerie zeigen“ → Nachricht mit genau den Dateien + Galerie (gefiltert, sobald Agent V den Filterbaustein
/// <c>GalleryGeoFilter</c> geliefert hat), Screenshots hell/dunkel.
/// </summary>
internal sealed partial class SmokeScenario
{
    private async Task PlacesMapAsync()
    {
        var services = ((App)Application.Current).Services;
        var vm = services.GetRequiredService<PlacesViewModel>();
        var dialogs = services.GetService<SmokePlacesDialogs>();
        Check(dialogs is not null, "Fake-Dialog „Fotos verorten“ registriert");
        Check(services.GetRequiredService<IMapTileSource>() is { State: MapTileSourceState.Disabled } tileSource
              && (tileSource as Lumora.Places.Styles.StyledMapTileSource)?.Inner is DisabledMapTileSource,
            "Kachelquelle im Rauchtest abgeschaltet (kein Netz, Stil umschaltbar, ADR-0055)");

        // (1) Seite öffnen
        _navigation.NavigateTo(NavigationTags.Places);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Places, ShortWait, "Seite „Orte“ aktiv").ConfigureAwait(true);
        await WaitUntilAsync(() => !vm.IsLoading && vm.HasLibrary && vm.UnlocatedCount + vm.LocatedCount > 0, ShortWait, "Orte geladen").ConfigureAwait(true);
        await vm.LastLoad.ConfigureAwait(true);
        var page = services.GetRequiredService<PlacesPage>();
        Check(page.IsLoaded && page.MapView.ActualWidth > 200, $"Karte sichtbar ({page.MapView.ActualWidth:N0}×{page.MapView.ActualHeight:N0})");
        Check(vm.TileStatusText.Contains("abgeschaltet", StringComparison.Ordinal) && page.MapView.LastDrawnTileImages == 0, $"graue Karte ohne Kacheln: „{vm.TileStatusText}“");
        Check(vm.Attribution == OsmTileSource.OsmAttribution, "Zuordnung „© OpenStreetMap-Mitwirkende“");
        var locatedBefore = vm.LocatedCount;
        var unlocatedBefore = vm.UnlocatedCount;
        Check(unlocatedBefore >= 5, $"mindestens 5 Fotos ohne Ort ({unlocatedBefore}, verortet {locatedBefore})");
        Check(vm.SummaryText == PlacesText.Summary(locatedBefore, unlocatedBefore), $"Trefferzeile „{vm.SummaryText}“");

        // (2) Drei JPEG-Fotos auswählen (Strg+Klick) und per Rechtsklick „Hier verorten“ nach Wien
        var jpegs = (await DbRowsAsync(
            "SELECT Id FROM Files WHERE LibraryId = $lib AND Missing = 0 AND MediaType = 0 AND (lower(Name) LIKE '%.jpg' OR lower(Name) LIKE '%.jpeg')",
            ("$lib", _libraryId)).ConfigureAwait(true)).Select(int.Parse).ToHashSet();
        var tiles = vm.Unlocated.Where(t => jpegs.Contains(t.FileId)).Take(5).ToList();
        Check(tiles.Count == 5, $"5 JPEG-Fotos ohne Ort ({tiles.Count})");
        vm.SelectTile(tiles[0], toggle: false, range: false);
        vm.SelectTile(tiles[1], toggle: true, range: false);
        vm.SelectTile(tiles[2], toggle: true, range: false);
        Check(vm.SelectedCount == 3, $"3 Fotos ausgewählt ({vm.SelectedCount})");
        var menu = page.BuildContextMenu(new Lumora.Places.Map.MapPointEventArgs(48.2082, 16.3738, null, new Point(10, 10)));
        Check(menu.Items.OfType<System.Windows.Controls.MenuItem>().Any(i => i.IsEnabled && (string)i.Header == "Hier verorten (3 Fotos)"), "Kontextmenü „Hier verorten (3 Fotos)“");
        var vienna = tiles.Take(3).Select(t => t.FileId).ToList();
        await vm.AssignSelectedAtAsync(48.2082, 16.3738).ConfigureAwait(true);
        Check(dialogs!.Requests.LastOrDefault() is { FileCount: 3 }, $"Bestätigung mit 3 Fotos ({dialogs.Requests.LastOrDefault()})");
        await WaitUntilAsync(() => !vm.IsLoading && vm.LocatedCount == locatedBefore + 3, ShortWait, "3 Fotos verortet").ConfigureAwait(true);
        Check(vm.UnlocatedCount == unlocatedBefore - 3 && !vm.Unlocated.Any(t => vienna.Contains(t.FileId)), $"„Ohne Ort“ ohne die verorteten ({vm.UnlocatedCount})");

        // (3) Zwei Fotos per Ablegen (gleicher Weg wie Ziehen auf die Karte) nach Graz
        var graz = tiles.Skip(3).Take(2).Select(t => t.FileId).ToList();
        await vm.AssignAsync(graz, 47.0707, 15.4395).ConfigureAwait(true);
        await WaitUntilAsync(() => !vm.IsLoading && vm.LocatedCount == locatedBefore + 5, ShortWait, "5 Fotos verortet").ConfigureAwait(true);
        Check(vm.Message?.Contains("verortet", StringComparison.Ordinal) == true, $"Meldung „{vm.Message}“");

        // (4) Weltansicht: Wien + Graz sind eine Gruppe; Klick zoomt hinein, bis sie zerfällt
        vm.ShowWorldCommand.Execute(null);
        var austria = vm.Markers.Where(m => vm.Index.GetFileIds(m.Cluster).Intersect(vienna.Concat(graz)).Any()).ToList();
        Check(austria.Count == 1 && austria[0].IsGroup && austria[0].Count >= 5, $"Weltansicht: eine Gruppe mit {austria.FirstOrDefault()?.Count} Fotos");
        await Task.Delay(100).ConfigureAwait(true);
        Check(page.MapView.LastDrawnMarkers > 0, $"Karte zeichnet {page.MapView.LastDrawnMarkers} Marker");
        var zoomBefore = vm.Viewport.Zoom;
        for (var i = 0; i < 20; i++)
        {
            var group = vm.Markers.FirstOrDefault(m => m.IsGroup && vm.Index.GetFileIds(m.Cluster).Intersect(vienna).Any() && vm.Index.GetFileIds(m.Cluster).Intersect(graz).Any());
            if (group is null)
            {
                break;
            }

            await vm.ActivateMarkerAsync(group).ConfigureAwait(true);
        }

        var parts = vm.Markers.Where(m => vm.Index.GetFileIds(m.Cluster).Intersect(vienna.Concat(graz)).Any()).ToList();
        Check(vm.Viewport.Zoom > zoomBefore && parts.Count >= 2, $"Gruppe zerfällt beim Hineinzoomen: Zoom {zoomBefore:N1} → {vm.Viewport.Zoom:N1}, {parts.Count} Marker");

        // (5) Wien-Gruppe in der Galerie zeigen
        vm.Viewport.CenterOn(48.2082, 16.3738, 12);
        var viennaMarker = vm.Markers.FirstOrDefault(m => vm.Index.GetFileIds(m.Cluster).Intersect(vienna).Count() == 3);
        Check(viennaMarker is { IsGroup: true, Count: >= 3 }, $"Wien-Gruppe mit {viennaMarker?.Count} Fotos");
        await SnapshotAsync("39-orte-karte-hell").ConfigureAwait(true);
        _settings.IsThemeDark = true;
        await WaitUntilAsync(() => _theme.Mode == AppThemeMode.Dark && ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark, ShortWait, "Theme Dunkel").ConfigureAwait(true);
        vm.ShowWorldCommand.Execute(null);
        vm.ShowAllPhotosCommand.Execute(null);
        await SnapshotAsync("40-orte-karte-dunkel").ConfigureAwait(true);
        _settings.IsThemeSystem = true;
        await WaitUntilAsync(() => _theme.Mode == AppThemeMode.System, ShortWait, "Theme System").ConfigureAwait(true);

        vm.Viewport.CenterOn(48.2082, 16.3738, 12);
        viennaMarker = vm.Markers.First(m => vm.Index.GetFileIds(m.Cluster).Intersect(vienna).Count() == 3);
        ShowFilesOnMapSelectionMessage? sent = null;
        var recipient = new object();
        _messenger.Register<ShowFilesOnMapSelectionMessage>(recipient, (_, m) => sent = m);
        try
        {
            vm.ShowInGallery(viennaMarker);
        }
        finally
        {
            _messenger.Unregister<ShowFilesOnMapSelectionMessage>(recipient);
        }

        var expected = vm.Index.GetFileIds(viennaMarker.Cluster);
        Check(sent is not null && sent.FileIds.OrderBy(i => i).SequenceEqual(expected.OrderBy(i => i)) && vienna.All(sent.FileIds.Contains),
            $"Nachricht „In der Galerie zeigen“ mit {sent?.FileIds.Count} Dateien (erwartet {expected.Count})");
        Check(sent!.Bounds.South <= 48.2082 + 1e-6 && sent.Bounds.North >= 48.2082 - 1e-6, $"Begrenzungsrahmen umfasst Wien ({sent.Bounds})");
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Gallery, ShortWait, "Galerie aktiv").ConfigureAwait(true);

        // Galerie gefiltert: Empfänger + Filterbaustein liefert Agent V (Vertrag). Bis zum Zusammenbau gibt es ihn nicht.
        var geoFilterAvailable = typeof(GalleryViewModel).Assembly.GetTypes().Any(t => t.Name == "GalleryGeoFilter");
        if (geoFilterAvailable)
        {
            await WaitUntilAsync(() => !_gallery.IsLoading && _gallery.TotalCount == expected.Count, ShortWait, $"Galerie zeigt genau die {expected.Count} Dateien der Gruppe").ConfigureAwait(true);
            await SharedFilterAndMapStylesAsync(vm, page, vienna[0], locatedBefore + 5).ConfigureAwait(true); // ADR-0055
            await _gallery.ResetFiltersCommand.ExecuteAsync(null).ConfigureAwait(true);
            await WaitForGalleryAsync("Filter zurückgesetzt").ConfigureAwait(true);
        }
        else
        {
            _logger.LogInformation("Rauchtest   Galerie-Filter „Kartenausschnitt“ (GalleryGeoFilter, Agent V) noch nicht vorhanden — Prüfung der Trefferzahl folgt beim Zusammenbau");
        }
    }
}
