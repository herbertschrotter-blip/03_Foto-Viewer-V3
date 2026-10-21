using System;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Lumora.Services.Albums;
using Lumora.Services.Stories;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Storys“ (ADR-0058, vor „Versteckte Bibliothek“), ohne UI (die Story-Seite folgt): Mindestgröße für
/// die kleine Testbibliothek auf 5, Erkennung über alle Gruppen → mindestens eine automatische Story mit Dateien nur aus
/// der Bibliothek, Übersicht und Story-Ansicht laden; „Story aus Auswahl“ aus „Urlaub Italien“ (Tagesabschnitte,
/// Titelbild), anheften, „Als Album speichern“; zweite Erkennung lässt die eigene Story und den Zustand unberührt.
/// Danach aufgeräumt (eigene Story, Album, Einstellungen).
/// </summary>
internal sealed partial class SmokeScenario
{
    private IStoryIndexService? _storyIndex;
    private IStoryService? _stories;
    private IStorySettingsStore? _storySettings;

    private void InitStoriesSmoke(IServiceProvider services)
    {
        _storyIndex = services.GetService<IStoryIndexService>();
        _stories = services.GetService<IStoryService>();
        _storySettings = services.GetService<IStorySettingsStore>();
    }

    private async Task StoriesAsync()
    {
        Check(_storyIndex is not null && _stories is not null && _storySettings is not null, "Story-Dienste registriert");
        var settingsBefore = await _storySettings!.LoadAsync().ConfigureAwait(true);
        int? custom = null;
        AlbumInfo? album = null;
        try
        {
            await _storySettings.SaveAsync(settingsBefore with { MinFiles = 5 }).ConfigureAwait(true);
            var report = await _storyIndex!.RunAllAsync().ConfigureAwait(true);
            var auto = await DbScalarAsync<long>("SELECT COUNT(*) FROM Stories WHERE Kind IN (0, 1, 3, 4)").ConfigureAwait(true);
            Check(report.Groups >= 1 && auto >= 1, $"Erkennung: {auto} automatische Storys aus {report.Files} Dateien in {report.Total.TotalMilliseconds:N0} ms");
            var foreign = await DbScalarAsync<long>(
                "SELECT COUNT(*) FROM StoryItems si JOIN Stories s ON s.Id = si.StoryId JOIN Files f ON f.Id = si.FileId JOIN Libraries l ON l.Id = f.LibraryId WHERE l.GroupId <> s.GroupId").ConfigureAwait(true);
            Check(foreign == 0, "Storys enthalten nur Dateien ihrer Bibliotheks-Gruppe");

            var overview = await _stories!.GetOverviewAsync().ConfigureAwait(true);
            Check(overview.Counts.All >= 1 && overview.TotalFiles > 0, $"Übersicht: {overview.Counts.All} Storys, {overview.EventsAndTrips.Count} Ereignisse/Reisen, {overview.TotalFiles} Fotos/Videos");
            var first = overview.EventsAndTrips.Concat(overview.Persons).Concat(overview.Places).FirstOrDefault();
            if (first is not null)
            {
                var details = await _stories.GetDetailsAsync(first.Id).ConfigureAwait(true);
                Check(details is not null && details.Days.Count >= 1 && details.FileIds.Count == first.FileCount,
                    $"Story-Ansicht „{first.Title}“: {details?.Days.Count} Tage, {details?.FileIds.Count} Dateien");
            }

            // „Story aus Auswahl“ aus „Urlaub Italien“
            var urlaub = await ImageIdsInAsync(Path.Combine(_root, "2024", "Urlaub Italien"), 6).ConfigureAwait(true);
            Check(urlaub.Length == 6, $"6 Testfotos in „Urlaub Italien“ ({urlaub.Length})");
            custom = await _stories.CreateFromSelectionAsync(urlaub, "Smoke-Story").ConfigureAwait(true);
            var card = await _stories.GetAsync(custom.Value).ConfigureAwait(true);
            Check(card is { Kind: Domain.Enums.StoryKind.Custom, FileCount: 6, Title: "Smoke-Story", CoverFileId: not null },
                $"Eigene Story: {card?.FileCount} Fotos, Titelbild {card?.CoverFileId}, sachliche Überschrift „{card?.AutoTitle}“");
            await _stories.SetPinnedAsync(custom.Value, true).ConfigureAwait(true);
            album = await _stories.SaveAsAlbumAsync(custom.Value).ConfigureAwait(true);
            var albumItems = await DbScalarAsync<long>("SELECT COUNT(*) FROM AlbumItems WHERE AlbumId = $a", ("$a", album.Id)).ConfigureAwait(true);
            Check(album.Name == "Smoke-Story" && albumItems == 6, $"Als Album gespeichert: „{album.Name}“ mit {albumItems} Fotos");

            await _storyIndex.RunAllAsync().ConfigureAwait(true);
            var after = await _stories.GetAsync(custom.Value).ConfigureAwait(true);
            Check(after is { IsPinned: true, FileCount: 6 }, "Neuberechnung lässt die eigene Story und ihren Zustand unberührt");
            Check((await _stories.GetOverviewAsync().ConfigureAwait(true)).Pinned.Any(c => c.Id == custom.Value), "Übersicht: unter „Angeheftet“");
        }
        finally
        {
            if (custom is { } id)
            {
                await _stories!.DeleteAsync(id).ConfigureAwait(true);
            }

            if (album is not null)
            {
                await _albumActions!.Albums.DeleteAsync(album.Id).ConfigureAwait(true);
            }

            await _storySettings.SaveAsync(settingsBefore).ConfigureAwait(true);
        }

        var left = await DbScalarAsync<long>("SELECT (SELECT COUNT(*) FROM Stories WHERE Kind = 5) + (SELECT COUNT(*) FROM Albums)").ConfigureAwait(true);
        Check(left == 0, "aufgeräumt: keine eigene Story, kein Album übrig");
    }
}
