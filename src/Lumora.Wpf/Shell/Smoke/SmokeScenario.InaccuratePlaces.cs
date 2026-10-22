using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Lumora.Places;
using Lumora.Places.Suggestions;
using Lumora.Places.TrackSettings;
using Lumora.Places.ViewModels;
using Lumora.Services.Library;
using Lumora.Services.Places.Accuracy;
using Lumora.Services.Places.Suggestions;
using Lumora.Services.Places.Tracks;
using Wpf.Ui.Appearance;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Ungenaue Orte“ (ADR-0062, vor „Versteckte Bibliothek“): Genauigkeit prüfen, für zwei Fotos ohne Ort
/// im Lauf eine synthetische GPX-Spur passend zu ihrer Aufnahmezeit erzeugen (im isolierten AppData, nie im Repo, kein
/// Netz), über Einstellungen › Orte einlesen, Vorschläge „Garmin-Spur“ und Zähler prüfen, Karte mit Spur und Korrektur,
/// einen Vorschlag „nur im Katalog“ übernehmen (Datei unverändert), rückgängig machen, Bereich „Ungenau“, Reiter „Orte“.
/// Danach aufgeräumt (Spuren entfernt, Speicherart zurück, Bereich „Ohne Ort“).
/// </summary>
internal sealed partial class SmokeScenario
{
    private async Task InaccuratePlacesAsync()
    {
        var services = ((App)Application.Current).Services;
        var vm = services.GetRequiredService<PlacesViewModel>();
        var page = services.GetRequiredService<PlacesPage>();
        var settingsVm = services.GetRequiredService<PlacesSettingsViewModel>();
        var suggestions = services.GetRequiredService<IGeoSuggestionService>();
        var accuracy = services.GetRequiredService<IGeoAccuracyService>();
        var store = services.GetRequiredService<IGpsTrackStore>();
        var trackSettings = services.GetRequiredService<IGeoTrackSettingsStore>();
        var scopeService = services.GetRequiredService<ILibraryScope>();
        var saveBefore = (await trackSettings.LoadAsync().ConfigureAwait(true)).SaveToFile;
        var gpxDir = Path.Combine(_paths.AppDataRoot, "smoke-spuren");
        int? groupId = null;
        try
        {
            // (1) Genauigkeit nachtragen (wie im Hintergrund)
            await accuracy.RunPassAsync().ConfigureAwait(true);
            var open = await DbScalarAsync<long>("SELECT COUNT(*) FROM Files WHERE LibraryId = $lib AND GeoSource IS NOT NULL AND GeoAccuracy IS NULL AND Missing = 0", ("$lib", _libraryId)).ConfigureAwait(true);
            Check(open == 0, $"Genauigkeit für alle gelesenen Orte bewertet (offen: {open})");
            var estimatedDb = await DbScalarAsync<long>(
                "SELECT COUNT(*) FROM Files WHERE LibraryId = $lib AND GeoSource = 1 AND GeoAccuracy = 2 AND Missing = 0", ("$lib", _libraryId)).ConfigureAwait(true);

            // (2) Zwei Fotos ohne Ort mit Aufnahmezeit (ohne genaues Datum bekommen sie eines „von Hand“)
            var scope = await scopeService.GetCurrentAsync().ConfigureAwait(true);
            groupId = scope.GroupId;
            var ids = (await DbRowsAsync(
                "SELECT Id FROM Files WHERE LibraryId = $lib AND Missing = 0 AND MediaType = 0 AND GeoSource = 0 AND Latitude IS NULL AND GeoSuggestionState = 0 ORDER BY Id LIMIT 2",
                ("$lib", _libraryId)).ConfigureAwait(true)).Select(int.Parse).ToList();
            Check(ids.Count == 2, $"2 Fotos ohne Ort ({ids.Count})");
            var before = (await suggestions.LoadAsync(scope, GeoSuggestionListKind.Unlocated).ConfigureAwait(true)).Items.Where(i => ids.Contains(i.FileId)).ToList();
            foreach (var id in ids.Where(id => before.All(i => i.FileId != id || i.Utc is null)))
            {
                await DbScalarAsync<long>(
                    "UPDATE Files SET CapturedAt = 1584280000 + $id * 3600, CapturedAtSource = 4, CapturedAtPrecision = 0, CapturedAtOffset = 60 WHERE Id = $id; SELECT changes();",
                    ("$id", id)).ConfigureAwait(true);
            }

            var items = (await suggestions.LoadAsync(scope, GeoSuggestionListKind.Unlocated).ConfigureAwait(true)).Items.Where(i => ids.Contains(i.FileId)).ToList();
            Check(items.Count == 2 && items.All(i => i.Utc is not null), $"Aufnahmezeit nach dem Zeitabgleich ({string.Join(", ", items.Select(i => i.TimeBasis))})");

            // (3) Synthetische GPX-Spur (je Foto eine Aktivität, ±5 min im 10-s-Takt) im isolierten AppData
            Directory.CreateDirectory(gpxDir);
            var gpx = Path.Combine(gpxDir, "spur.gpx");
            await File.WriteAllTextAsync(gpx, BuildSmokeGpx(items.Select(i => i.Utc!.Value).ToList())).ConfigureAwait(true);
            var report = await settingsVm.ImportAsync([gpx]).ConfigureAwait(true);
            Check(report.Imported == 2 && report.Failed == 0, $"GPX eingelesen: {report.Text}");
            Check((await settingsVm.ImportAsync([gpx]).ConfigureAwait(true)).Duplicates == 2, "zweites Einlesen: Doppelte übersprungen");
            await settingsVm.LoadAsync().ConfigureAwait(true);
            Check(settingsVm.ActivityCount == 2 && settingsVm.SummaryText.StartsWith("2 Aktivitäten", StringComparison.Ordinal), $"Einstellungen: „{settingsVm.SummaryText}“");

            // (4) Seite „Orte“: Zähler, Hinweis unter „Ohne Ort“, Vorschläge „Garmin-Spur“
            _navigation.NavigateTo(NavigationTags.Places);
            await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Places, ShortWait, "Seite „Orte“ aktiv").ConfigureAwait(true);
            await vm.ReloadAsync().ConfigureAwait(true);
            await vm.LastSuggestionLoad.ConfigureAwait(true);
            Check(vm.EstimatedCount == estimatedDb, $"Kopf: {vm.EstimatedCount} mit geschätzter Position (Katalog {estimatedDb})");
            Check(vm.UnlocatedSuggestionCount >= 2 && vm.HasUnlocatedHint, $"Hinweis unter „Ohne Ort“: {vm.UnlocatedHint}");
            vm.ShowUnlocatedSuggestionsCommand.Execute(null);
            var rows = vm.SuggestionRows.OfType<SuggestionItemRow>().Where(r => ids.Contains(r.FileId)).ToList();
            Check(rows.Count == 2 && rows.All(r => r.Level == GeoSuggestionLevel.Track && r.Item.Track is not null),
                $"Vorschläge „Garmin-Spur“: {string.Join(", ", rows.Select(r => r.BadgeText))}");
            var row = rows[0];
            vm.SelectRow(row);
            await WaitUntilAsync(() => page.MapView.LastDrawnTrackPoints > 10 && page.MapView.LastDrawnCorrections > 0, ShortWait, "Karte: Spur und Vorschlag").ConfigureAwait(true);
            Check(vm.MapMarkers.Count == 0 && vm.MapOverlay is { } overlay && overlay.Corrections.Any(c => c.Id == row.FileId && c.Suggested is not null),
                "Karte zeigt die Korrektur statt der Foto-Gruppen");
            await WaitUntilAsync(() => _statusBar.GeoTracksText is not null, ShortWait, "Statusleiste Orte").ConfigureAwait(true);
            Check(_statusBar.GeoTracksText!.Contains("2 Garmin-Aktivitäten", StringComparison.Ordinal), $"Statusleiste: {_statusBar.GeoTracksText}");
            _settings.IsThemeLight = true;
            await WaitUntilAsync(() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light, ShortWait, "Theme Hell").ConfigureAwait(true);
            await SnapshotAsync("66-orte-ungenau-hell").ConfigureAwait(true);
            _settings.IsThemeDark = true;
            await WaitUntilAsync(() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark, ShortWait, "Theme Dunkel").ConfigureAwait(true);
            await SnapshotAsync("67-orte-ungenau-dunkel").ConfigureAwait(true);

            // (5) Übernehmen „nur im Katalog“ — die Datei bleibt Byte für Byte, der Katalog zeigt „aus GPS-Spur“
            var path = (await DbRowsAsync("SELECT d.Path || '\\' || f.Name FROM Files f JOIN Folders d ON d.Id = f.FolderId WHERE f.Id = $id", ("$id", row.FileId)).ConfigureAwait(true)).Single();
            var hashBefore = SHA256.HashData(await File.ReadAllBytesAsync(path).ConfigureAwait(true));
            vm.SaveToFile = false;
            await vm.ApplyRowsAsync([row], "1 Ort übernommen").ConfigureAwait(true);
            var applied = (await DbRowsAsync("SELECT GeoSource, Latitude, Longitude FROM Files WHERE Id = $id", ("$id", row.FileId)).ConfigureAwait(true)).Single().Split(" | ");
            Check(applied[0] == "4"
                  && Math.Abs(double.Parse(applied[1], CultureInfo.InvariantCulture) - row.Latitude!.Value) < 1e-6
                  && Math.Abs(double.Parse(applied[2], CultureInfo.InvariantCulture) - row.Longitude!.Value) < 1e-6,
                $"Katalog: Herkunft „aus GPS-Spur“ und der Vorschlag ({string.Join(", ", applied)})");
            Check(SHA256.HashData(await File.ReadAllBytesAsync(path).ConfigureAwait(true)).SequenceEqual(hashBefore), "nur im Katalog: Datei unverändert");
            Check(vm.HasToast && vm.ToastText!.EndsWith("nur im Katalog", StringComparison.Ordinal) && row.IsApplied, $"Hinweis: {vm.ToastText}");

            // (6) Rückgängig über den Hinweis
            await vm.UndoToastCommand.ExecuteAsync(null).ConfigureAwait(true);
            var undone = (await DbRowsAsync("SELECT GeoSource, Latitude FROM Files WHERE Id = $id", ("$id", row.FileId)).ConfigureAwait(true)).Single();
            Check(undone == "0 | NULL", $"Rückgängig: {undone}");
            await vm.LastSuggestionLoad.ConfigureAwait(true);

            // (7) Bereich „Ungenau“: Chips zählen wie der Katalog
            vm.ShowInaccurateModeCommand.Execute(null);
            var all = vm.FilterChips.First(c => c.Filter == SuggestionFilter.All).Count;
            var rejectedEstimated = await DbScalarAsync<long>(
                "SELECT COUNT(*) FROM Files WHERE LibraryId = $lib AND GeoSource = 1 AND GeoAccuracy = 2 AND Missing = 0 AND GeoSuggestionState <> 0", ("$lib", _libraryId)).ConfigureAwait(true);
            Check(all == estimatedDb - rejectedEstimated, $"„Ungenau“: {all} Fotos (Katalog {estimatedDb})");

            // (8) Einstellungen › Orte
            await OpenSettingsSectionAsync(NavigationTags.SettingsPlaces, "Einstellungen › Orte").ConfigureAwait(true);
            await SnapshotAsync("68-einstellungen-orte-dunkel").ConfigureAwait(true);
            _settings.IsThemeLight = true;
            await WaitUntilAsync(() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light, ShortWait, "Theme Hell").ConfigureAwait(true);
            await SnapshotAsync("69-einstellungen-orte-hell").ConfigureAwait(true);
        }
        finally
        {
            vm.ShowUnlocatedModeCommand.Execute(null);
            if (groupId is { } g)
            {
                await store.DeleteAllAsync(g).ConfigureAwait(true);
            }

            var settings = await trackSettings.LoadAsync().ConfigureAwait(true);
            await trackSettings.SaveAsync(settings with { SaveToFile = saveBefore }).ConfigureAwait(true);
            vm.SaveToFile = saveBefore;
            _settings.IsThemeSystem = true;
            try
            {
                Directory.Delete(gpxDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // isoliertes AppData — wird mit dem Lauf entfernt
            }
        }
    }

    /// <summary>GPX mit je einer Aktivität ±5 min um jede Zeit (10-s-Takt, Weinebene-Gegend, nach Osten).</summary>
    private static string BuildSmokeGpx(IReadOnlyList<long> utcTimes)
    {
        var sb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8"?><gpx version="1.1" creator="Lumora-Rauchtest" xmlns="http://www.topografix.com/GPX/1/1">""");
        for (var t = 0; t < utcTimes.Count; t++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<trk><name>Rauchtest {t + 1}</name><type>hiking</type><trkseg>");
            for (var i = -30; i <= 30; i++)
            {
                var time = DateTimeOffset.FromUnixTimeSeconds(utcTimes[t] + (i * 10)).UtcDateTime;
                sb.Append(CultureInfo.InvariantCulture, $"<trkpt lat=\"{46.95 + (t * 0.05):0.000000}\" lon=\"{14.95 + ((i + 30) * 0.0002):0.000000}\"><ele>{1800 + i}</ele><time>{time:yyyy-MM-ddTHH:mm:ssZ}</time></trkpt>");
            }

            sb.Append("</trkseg></trk>");
        }

        return sb.Append("</gpx>").ToString();
    }
}
