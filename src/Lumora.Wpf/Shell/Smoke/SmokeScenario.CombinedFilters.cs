using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Messaging;
using Lumora.Gallery.Controls;
using Lumora.Gallery.Data;
using Lumora.Gallery.ViewModels;
using Lumora.Gallery.Views;
using Lumora.Services.Messaging;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Kombinierte Filter“ (0.2.43, ADR-0041) — vor der Ordnerüberwachung: Bilder UND nur Favoriten UND
/// Tag UND ab 3 Sternen gleichzeitig (früher schlossen sich Dateiart, Favoriten und Ablage aus). Erwartete Zahlen
/// kommen aus der DB. Danach × an einer Bedingung, „Filter zurücksetzen“ (Gesamtzahl wieder da), und: die Ordnerauswahl
/// bleibt beim Zurücksetzen erhalten. Hinterlässt kein Tag, keine Favoriten/Bewertungen der Testdateien und keine Filter.
/// </summary>
internal sealed partial class SmokeScenario
{
    private async Task CombinedFiltersAsync()
    {
        _navigation.NavigateTo(NavigationTags.Gallery);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Gallery, ShortWait, "Galerie aktiv").ConfigureAwait(true);
        if (_gallery.HasFolderFilter)
        {
            await _gallery.ClearFolderFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
        }

        if (_gallery.HasActiveFilters)
        {
            await _gallery.ResetFiltersCommand.ExecuteAsync(null).ConfigureAwait(true);
        }

        await WaitForGalleryAsync("Galerie ohne Filter").ConfigureAwait(true);
        var baseline = _gallery.TotalCount;
        var dbTotal = await DbScalarAsync<int>("SELECT COUNT(*) FROM Files WHERE LibraryId = $lib", ("$lib", _libraryId)).ConfigureAwait(true);
        Check(baseline == dbTotal && !_gallery.HasActiveFilters && _gallery.ActiveFilterCount == 0,
            $"Ausgangslage: {baseline} Dateien (DB {dbTotal}), kein Filter, Trefferzeile „{baseline:N0}{_gallery.ResultCountText}“");

        // Strg+F gebunden (Kopfzeile, 0.2.43)
        var galleryView = FindVisualChild<GalleryView>(Application.Current.MainWindow);
        var findBindings = galleryView?.InputBindings.OfType<KeyBinding>().Count(b => b.Key == Key.F && b.Modifiers == ModifierKeys.Control) ?? 0;
        Check(findBindings == 1, "Strg+F in der Galerie gebunden (Suchfeld)");

        // Nur Dateien ohne Favorit/Bewertung aus früheren Schritten — sonst zählten sie zu den erwarteten Treffern mit.
        var images = _gallery.CurrentPage.Where(i => !i.IsVideo && !i.IsArchive && !i.IsFavorite && i.Rating is null).Take(4).ToList();
        var video = _gallery.CurrentPage.FirstOrDefault(i => i.IsVideo && !i.IsFavorite && i.Rating is null);
        Check(images.Count == 4, $"Testdateien: {images.Count} Bilder ohne Favorit/Bewertung, Video {(video is null ? "— (alle schon bewertet)" : "dazu")}");
        var extra = video is null ? images : images.Append(video).ToList();
        var touched = extra.Select(i => i.Id).ToHashSet();
        var tag = await _tags.CreateAsync("Smoketag Kombi").ConfigureAwait(true);
        try
        {
            // Tag auf 4 Bilder (+ ein Video, falls unbewertet); Favorit + 4 Sterne auf Bild 1–2 (+ Video); Favorit + 2 Sterne auf Bild 3.
            await _tags.AssignAsync(touched.ToList(), [tag.Id]).ConfigureAwait(true);
            await SetFavoriteAndRatingAsync(video is null ? new[] { images[0], images[1] } : new[] { images[0], images[1], video }, 4).ConfigureAwait(true);
            await SetFavoriteAndRatingAsync(new[] { images[2] }, 2).ConfigureAwait(true);
            await WaitUntilAsync(() => _gallery.TagFilterEntries.Any(e => e.Tag.Id == tag.Id), ShortWait, "Tag „Smoketag Kombi“ im Tag-Filter").ConfigureAwait(true);

            // Bilder UND Favoriten UND Tag UND ab 3 Sternen
            _gallery.MediaFilter = GalleryMediaKind.Images;
            await WaitForGalleryAsync("Filter Bilder").ConfigureAwait(true);
            _gallery.FavoritesOnly = true;
            await WaitForGalleryAsync("Filter Bilder + Favoriten").ConfigureAwait(true);
            _gallery.TagFilterEntries.First(e => e.Tag.Id == tag.Id).IsSelected = true;
            await _gallery.ApplyTagFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
            await WaitForGalleryAsync("Filter Bilder + Favoriten + Tag").ConfigureAwait(true);
            _gallery.MinRating = 3;
            await WaitForGalleryAsync("Filter Bilder + Favoriten + Tag + ab 3 Sternen").ConfigureAwait(true);

            const string combinedSql = """
                SELECT COUNT(*) FROM Files f
                WHERE f.LibraryId = $lib AND f.MediaType = 0 AND f.IsFavorite = 1 AND f.Rating >= $min
                  AND f.Id IN (SELECT ft.FileId FROM FileTags ft WHERE ft.TagId = $t)
                """;
            var expected = await DbScalarAsync<int>(combinedSql, ("$lib", _libraryId), ("$min", 3), ("$t", tag.Id)).ConfigureAwait(true);
            Check(expected == 2 && _gallery.TotalCount == expected && _gallery.CurrentPage.All(i => !i.IsVideo && i.IsFavorite && i.Rating >= 3),
                $"Bilder UND Favoriten UND Tag UND ab 3 Sternen: {_gallery.TotalCount} Treffer (DB {expected}, erwartet 2)");
            Check(_gallery.ActiveFilterCount == 4 && _gallery.HasActiveFilters && _gallery.UnfilteredCount == baseline,
                $"Zähler „Filter zurücksetzen“ = {_gallery.ActiveFilterCount}, Trefferzeile „{_gallery.TotalCount}{_gallery.ResultCountText}“");
            var labels = string.Join(" UND ", _gallery.ActiveConditions.Select(c => c.Label));
            Check(labels == "Bilder UND Favoriten UND ab 3 Sternen UND Tag „Smoketag Kombi“", $"Es gilt: {labels}");
            await SnapshotAsync("12-galerie-filter-kombiniert").ConfigureAwait(true);

            // × an „ab 3 Sternen“ → Bild 3 (2 Sterne) kommt dazu
            await _gallery.RemoveFilterConditionCommand.ExecuteAsync(_gallery.ActiveConditions.First(c => c.Kind == GalleryFilterConditionKind.Rating)).ConfigureAwait(true);
            await WaitForGalleryAsync("Bedingung „ab 3 Sternen“ entfernt").ConfigureAwait(true);
            var withoutRating = await DbScalarAsync<int>(combinedSql, ("$lib", _libraryId), ("$min", 0), ("$t", tag.Id)).ConfigureAwait(true);
            Check(_gallery.MinRating == 0 && _gallery.TotalCount == withoutRating && withoutRating == 3 && _gallery.ActiveFilterCount == 3,
                $"× „ab 3 Sternen“: {_gallery.TotalCount} Treffer (DB {withoutRating}), {_gallery.ActiveFilterCount} Bedingungen");

            // Ordnerauswahl bleibt beim Zurücksetzen; Suche wird mit zurückgesetzt
            var folderId = images[0].FolderId;
            _messenger.Send(new FolderSelectedMessage(_libraryId, folderId, true));
            await WaitUntilAsync(() => _gallery.FolderId == folderId && !_gallery.IsLoading, ShortWait, "Ordnerfilter gesetzt").ConfigureAwait(true);
            _gallery.SearchTerm = "img";
            await _gallery.SearchCommand.ExecuteAsync(null).ConfigureAwait(true);
            await WaitForGalleryAsync("Suche zusätzlich").ConfigureAwait(true);
            Check(_gallery.ActiveConditions.Any(c => c.Kind == GalleryFilterConditionKind.Search), $"Suche als Bedingung: {string.Join(" UND ", _gallery.ActiveConditions.Select(c => c.Label))}");

            await _gallery.ResetFiltersCommand.ExecuteAsync(null).ConfigureAwait(true);
            await WaitForGalleryAsync("Filter zurückgesetzt").ConfigureAwait(true);
            var inFolder = await DbScalarAsync<int>(
                "SELECT COUNT(*) FROM Files f JOIN Folders d ON d.Id = f.FolderId WHERE f.LibraryId = $lib AND (d.Id = $fid OR d.Path LIKE (SELECT Path FROM Folders WHERE Id = $fid) || '\\%')",
                ("$lib", _libraryId), ("$fid", folderId)).ConfigureAwait(true);
            Check(!_gallery.HasActiveFilters && _gallery.FilterState == GalleryFilterState.None && !_gallery.HasTagFilter && _gallery.SearchTerm is null,
                "Filter zurücksetzen: Datei-Filter, Tag-Filter und Suche aufgehoben");
            Check(_gallery.FolderId == folderId && _gallery.TotalCount == inFolder,
                $"Ordnerauswahl bleibt: {_gallery.FolderChipText}, {_gallery.TotalCount} Dateien (DB {inFolder})");
            Check(!_gallery.ResetFiltersCommand.CanExecute(null), "„Filter zurücksetzen“ ohne aktive Filter deaktiviert");

            await _gallery.ClearFolderFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
            await WaitForGalleryAsync("Ordnerfilter aufgehoben").ConfigureAwait(true);
            Check(_gallery.TotalCount == baseline, $"Gesamtzahl wieder da: {_gallery.TotalCount} (erwartet {baseline})");
        }
        finally
        {
            if (_gallery.HasActiveFilters)
            {
                await _gallery.ResetFiltersCommand.ExecuteAsync(null).ConfigureAwait(true);
            }

            if (_gallery.HasFolderFilter)
            {
                await _gallery.ClearFolderFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
            }

            await _tags.DeleteAsync(tag.Id).ConfigureAwait(true);
            await SetFavoriteAndRatingAsync(_gallery.CurrentPage.Where(i => touched.Contains(i.Id)).ToList(), null).ConfigureAwait(true);
            await WaitForGalleryAsync("Aufgeräumt").ConfigureAwait(true);
        }
    }

    /// <summary>Favorit + Bewertung über die Aktionsleiste der Galerie (wie der Benutzer); <paramref name="rating"/> null = beides entfernen.</summary>
    private async Task SetFavoriteAndRatingAsync(System.Collections.Generic.IReadOnlyList<GalleryItem> items, byte? rating)
    {
        if (items.Count == 0)
        {
            return;
        }

        _gallery.ClearSelectionCommand.Execute(null);
        foreach (var item in items)
        {
            item.IsSelected = true;
        }

        if (rating is null)
        {
            await _gallery.UnfavoriteSelectionCommand.ExecuteAsync(null).ConfigureAwait(true);
            await _gallery.ClearSelectionRatingCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
        else
        {
            await _gallery.FavoriteSelectionCommand.ExecuteAsync(null).ConfigureAwait(true);
            await _gallery.SetSelectionRatingCommand.ExecuteAsync(new RatingRequest(null, rating)).ConfigureAwait(true);
        }

        _gallery.ClearSelectionCommand.Execute(null);
    }
}
