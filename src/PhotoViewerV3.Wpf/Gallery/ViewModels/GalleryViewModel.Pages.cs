using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Gallery.Data;
using PhotoViewerV3.Gallery.ListView;
using PhotoViewerV3.Shell;

namespace PhotoViewerV3.Gallery.ViewModels;

/// <summary>
/// Gemeinsamer Filter, seitengebundene Teile (ADR-0055, Herbert 2026-10-21): Galerie, Ordner und Orte teilen EINEN
/// Filterzustand — die Filterkarte bindet an dieses Singleton-ViewModel, und jede neu geladene Abfrage wird ohne Ordner und
/// Sortierung in <see cref="ISharedGalleryFilter"/> veröffentlicht (die Orte-Karte filtert damit). Seitengebunden bleiben
/// nur die Ordnerauswahl, die Sortierung und die Ansicht (Kacheln/Collage/Liste) der Seiten „Galerie“ und „Ordner“: beim
/// Seitenwechsel (<see cref="INavigationService.Navigated"/>) merkt sich das ViewModel diese Teile der verlassenen Seite
/// und stellt die der neuen wieder her. Ein Bibliothekswechsel behält den Filter; was es im neuen Bereich nicht gibt
/// (Tags, Personen, Kategorien, Motive, Album einer anderen Gruppe), fällt beim Neuladen der Listen heraus, der
/// Kartenausschnitt (Datei-Ids des alten Bereichs) immer.
/// </summary>
public sealed partial class GalleryViewModel
{
    /// <summary>Ansicht der Ordner-Seite (die der Galerie bleibt unter <see cref="ListView.GalleryViewSettingsStore.ViewModeKey"/>).</summary>
    public const string FoldersViewModeKey = "ui.folders.viewMode";

    private readonly Dictionary<GalleryPageKind, GalleryPageState> _pageStates = new();
    private ISharedGalleryFilter? _sharedFilter;
    private GalleryViewMode? _storedFoldersViewMode;

    /// <summary>Seitengebundene Teile einer Seite.</summary>
    private sealed record GalleryPageState(int? FolderId, string? FolderName, bool IncludeSubfolders, GallerySortOption Sort, GalleryViewMode ViewMode);

    /// <summary>Seite, deren Ordner/Sortierung/Ansicht gerade gelten (Galerie oder Ordner).</summary>
    public GalleryPageKind ActivePage { get; private set; } = GalleryPageKind.Gallery;

    /// <summary>Gemeinsamer Filter (Tests, Rauchtest); null ohne Dienst.</summary>
    public ISharedGalleryFilter? SharedFilter => _sharedFilter;

    private void InitPages(ISharedGalleryFilter? sharedFilter, INavigationService? navigation)
    {
        _sharedFilter = sharedFilter;
        if (navigation is not null)
        {
            navigation.Navigated += (_, tag) =>
            {
                if (tag == NavigationTags.Gallery)
                {
                    FireAndForget(() => ActivatePageAsync(GalleryPageKind.Gallery));
                }
                else if (tag == NavigationTags.Folders)
                {
                    FireAndForget(() => ActivatePageAsync(GalleryPageKind.Folders));
                }
            };
        }
    }

    /// <summary>
    /// Seite wechseln: Ordnerauswahl, Sortierung und Ansicht der bisherigen Seite merken, die der neuen herstellen und —
    /// wenn sich Ordner oder Sortierung dadurch ändern — neu laden. Der Filter bleibt unverändert.
    /// </summary>
    public Task ActivatePageAsync(GalleryPageKind page)
    {
        if (page == ActivePage)
        {
            return Task.CompletedTask;
        }

        var current = CapturePageState();
        _pageStates[ActivePage] = current;
        ActivePage = page;
        var target = _pageStates.TryGetValue(page, out var stored) ? stored : DefaultPageState(page, current);

        var suppress = _suppressReload;
        var suppressView = _suppressViewSettings;
        _suppressReload = true;
        _suppressViewSettings = true; // die Ansicht der Seite ist schon gespeichert
        try
        {
            FolderId = target.FolderId;
            FolderName = target.FolderName;
            IncludeSubfolders = target.IncludeSubfolders;
            SelectedSort = target.Sort;
            ViewMode = target.ViewMode;
        }
        finally
        {
            _suppressReload = suppress;
            _suppressViewSettings = suppressView;
        }

        _logger.LogInformation(
            "Galerie-Seite: {Page} (Ordner {Folder}, Sortierung {Sort}, Ansicht {View}) — Filter bleibt",
            page, target.FolderId?.ToString() ?? "-", target.Sort.Sort, target.ViewMode);
        var queryChanged = current.FolderId != target.FolderId || current.IncludeSubfolders != target.IncludeSubfolders || current.Sort != target.Sort;
        return queryChanged && _isInitialized && HasLibrary ? ChangeQueryAsync() : Task.CompletedTask;
    }

    private GalleryPageState CapturePageState() => new(FolderId, FolderName, IncludeSubfolders, SelectedSort, ViewMode);

    /// <summary>Erster Besuch einer Seite: kein Ordner, Standard-Sortierung, gespeicherte Ansicht der Seite (sonst die bisherige).</summary>
    private GalleryPageState DefaultPageState(GalleryPageKind page, GalleryPageState current)
        => new(null, null, true, s_sortOptions[0], page == GalleryPageKind.Folders ? _storedFoldersViewMode ?? current.ViewMode : current.ViewMode);

    /// <summary>Bibliothekswechsel: Ordner und Sortierung aller Seiten zurück (die Ansicht bleibt).</summary>
    private void ResetPageStates()
    {
        foreach (var page in new List<GalleryPageKind>(_pageStates.Keys))
        {
            _pageStates[page] = _pageStates[page] with { FolderId = null, FolderName = null, IncludeSubfolders = true, Sort = s_sortOptions[0] };
        }
    }

    /// <summary>Schlüssel der Ansicht der aktiven Seite in der Settings-Tabelle.</summary>
    private string ViewModeSettingKey => ActivePage == GalleryPageKind.Folders ? FoldersViewModeKey : ListView.GalleryViewSettingsStore.ViewModeKey;

    /// <summary>Ansicht der Ordner-Seite lesen (beim Start; die der Galerie liest <c>LoadViewSettingsAsync</c>).</summary>
    private async Task LoadPageViewSettingsAsync()
    {
        var mode = await _viewSettings.GetAsync(FoldersViewModeKey);
        _storedFoldersViewMode = Enum.TryParse<GalleryViewMode>(mode, ignoreCase: true, out var parsed) ? parsed : null;
    }

    /// <summary>
    /// Gespeicherte Galerie-Ansicht beim Start anwenden: auf der Galerie-Seite direkt; ist beim Start schon die
    /// Ordner-Seite aktiv, bekommt sie ihre eigene Ansicht und die Galerie-Seite die gespeicherte für später.
    /// </summary>
    private void ApplyStoredGalleryViewMode(GalleryViewMode galleryMode)
    {
        if (ActivePage == GalleryPageKind.Gallery)
        {
            ViewMode = galleryMode;
            return;
        }

        var gallery = _pageStates.TryGetValue(GalleryPageKind.Gallery, out var stored) ? stored : DefaultPageState(GalleryPageKind.Gallery, CapturePageState());
        _pageStates[GalleryPageKind.Gallery] = gallery with { ViewMode = galleryMode };
        if (_storedFoldersViewMode is { } folders)
        {
            ViewMode = folders;
        }
    }

    /// <summary>Filter-Teil der Abfrage für Ordner- und Orte-Seite veröffentlichen.</summary>
    private void PublishSharedFilter(GalleryQuery query)
    {
        try
        {
            _sharedFilter?.Publish(query);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gemeinsamer Filter konnte nicht veröffentlicht werden");
        }
    }
}
