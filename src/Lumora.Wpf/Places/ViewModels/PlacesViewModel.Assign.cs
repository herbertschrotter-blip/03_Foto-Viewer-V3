using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Lumora.Places.Dialogs;

namespace Lumora.Places.ViewModels;

/// <summary>
/// Seitenleiste „Ohne Ort“, Verorten und Ortssuche der Seite „Orte“ (ADR-0051).
/// </summary>
public sealed partial class PlacesViewModel
{
    private const int RangeBuffer = 24;

    /// <summary>Fotos ohne Koordinaten (neueste zuerst), virtualisiert angezeigt; Name/Bild kommen für den sichtbaren Bereich.</summary>
    [ObservableProperty] private IReadOnlyList<UnlocatedTile> _unlocated = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionText), nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(ClearSelectionCommand))]
    private int _selectedCount;

    public bool HasSelection => SelectedCount > 0;

    public string SelectionText => SelectedCount == 0
        ? "Fotos auswählen und auf die Karte ziehen — oder Rechtsklick auf die Karte › „Hier verorten“."
        : $"{PlacesText.N(SelectedCount)} ausgewählt — auf die Karte ziehen oder Rechtsklick › „Hier verorten“.";

    private void ApplyUnlocated(List<int> fileIds)
    {
        var selected = Unlocated.Where(t => t.IsSelected).Select(t => t.FileId).ToHashSet();
        _unlocatedById.Clear();
        var tiles = new List<UnlocatedTile>(fileIds.Count);
        foreach (var id in fileIds)
        {
            var tile = new UnlocatedTile(id) { IsSelected = selected.Contains(id) };
            if (_infos.TryGetValue(id, out var info))
            {
                tile.Name = info.Name;
            }

            tile.Image = _images.TryGet(id);
            _unlocatedById[id] = tile;
            tiles.Add(tile);
        }

        Unlocated = tiles;
        UnlocatedCount = tiles.Count;
        SelectedCount = tiles.Count(t => t.IsSelected);
    }

    /// <summary>Die Seitenleiste meldet den sichtbaren Bereich — nur dafür Namen und Vorschaubilder holen.</summary>
    public void ReportUnlocatedVisibleRange(int first, int last)
    {
        var tiles = Unlocated;
        if (tiles.Count == 0 || first < 0)
        {
            return;
        }

        var from = Math.Max(0, first - RangeBuffer);
        var to = Math.Min(tiles.Count - 1, last + RangeBuffer);
        var ids = new List<int>();
        for (var i = from; i <= to; i++)
        {
            if (tiles[i].Image is null || tiles[i].Name.Length == 0)
            {
                ids.Add(tiles[i].FileId);
            }
        }

        if (ids.Count > 0)
        {
            _ = LoadUnlocatedDetailsAsync(ids);
        }
    }

    private async Task LoadUnlocatedDetailsAsync(IReadOnlyList<int> fileIds)
    {
        try
        {
            var infos = await EnsureInfosAsync(fileIds).ConfigureAwait(true);
            foreach (var info in infos)
            {
                if (_unlocatedById.TryGetValue(info.FileId, out var tile) && tile.Name.Length == 0)
                {
                    tile.Name = info.Name;
                }
            }

            _images.Request(infos.Where(i => _images.TryGet(i.FileId) is null).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Orte: Angaben zu „Ohne Ort“ nicht geladen");
        }
    }

    /// <summary>Klick auf eine Kachel: allein auswählen; mit Strg umschalten; mit Umschalt den Bereich ab dem Anker.</summary>
    public void SelectTile(UnlocatedTile tile, bool toggle, bool range)
    {
        var tiles = Unlocated;
        if (range && _anchor is { } anchor && IndexOf(anchor) is var a && a >= 0 && IndexOf(tile) is var b && b >= 0)
        {
            if (!toggle)
            {
                ClearSelectionCore();
            }

            for (var i = Math.Min(a, b); i <= Math.Max(a, b); i++)
            {
                tiles[i].IsSelected = true;
            }
        }
        else if (toggle)
        {
            tile.IsSelected = !tile.IsSelected;
            _anchor = tile;
        }
        else
        {
            ClearSelectionCore();
            tile.IsSelected = true;
            _anchor = tile;
        }

        SelectedCount = tiles.Count(t => t.IsSelected);
    }

    private UnlocatedTile? _anchor;

    private int IndexOf(UnlocatedTile tile)
    {
        var tiles = Unlocated;
        for (var i = 0; i < tiles.Count; i++)
        {
            if (ReferenceEquals(tiles[i], tile))
            {
                return i;
            }
        }

        return -1;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ClearSelection()
    {
        ClearSelectionCore();
        SelectedCount = 0;
    }

    private void ClearSelectionCore()
    {
        foreach (var tile in Unlocated.Where(t => t.IsSelected))
        {
            tile.IsSelected = false;
        }
    }

    /// <summary>Doppelklick auf eine Kachel: Lightbox mit den Fotos ohne Ort ringsum (in Listenreihenfolge).</summary>
    public Task OpenUnlocatedAsync(UnlocatedTile tile)
    {
        var tiles = Unlocated;
        var index = IndexOf(tile);
        if (index < 0)
        {
            return Task.CompletedTask;
        }

        var from = Math.Max(0, index - (LightboxLimit / 2));
        var ids = new List<int>();
        for (var i = from; i < tiles.Count && ids.Count < LightboxLimit; i++)
        {
            ids.Add(tiles[i].FileId);
        }

        return OpenLightboxAsync(ids, tile.FileId);
    }

    /// <summary>Was beim Ziehen einer Kachel mitkommt: die Auswahl, wenn die Kachel dazugehört, sonst nur sie.</summary>
    public int[] DragFileIds(UnlocatedTile tile)
        => tile.IsSelected ? Unlocated.Where(t => t.IsSelected).Select(t => t.FileId).ToArray() : [tile.FileId];

    /// <summary>Ausgewählte Fotos ohne Ort.</summary>
    public IReadOnlyList<int> SelectedFileIds() => Unlocated.Where(t => t.IsSelected).Select(t => t.FileId).ToList();

    /// <summary>Rechtsklick auf die Karte › „Hier verorten“ mit der Auswahl aus „Ohne Ort“.</summary>
    public Task AssignSelectedAtAsync(double latitude, double longitude) => AssignAsync(SelectedFileIds(), latitude, longitude);

    /// <summary>
    /// Fotos an einem Punkt verorten: Bestätigung mit Ortsname (nächster Ort, offline) und „Änderungsdatum beibehalten“,
    /// dann <c>IGeoTagService.SetLocationAsync</c> und neu laden. Nur Dateien des aktuellen Bereichs.
    /// </summary>
    public async Task AssignAsync(IReadOnlyCollection<int> fileIds, double latitude, double longitude)
    {
        var ids = fileIds.Where(_media.ContainsKey).Distinct().ToList(); // Bibliotheks-Trennung: nur Medien des Bereichs
        if (ids.Count == 0 || IsBusy)
        {
            return;
        }

        var place = SafeNearest(latitude, longitude);
        var placeName = place is null ? null : PlacesText.Describe(place);
        var answer = await _dialogs.ConfirmAssignAsync(
            new AssignLocationRequest(ids.Count, latitude, longitude, placeName),
            System.Windows.Application.Current?.MainWindow).ConfigureAwait(true);
        if (answer is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            _ownChangeUntil = DateTime.UtcNow.AddSeconds(3);
            var result = await _tagger.SetLocationAsync(ids, latitude, longitude, answer.KeepModifiedTime, CancellationToken.None).ConfigureAwait(true);
            _ownChangeUntil = DateTime.UtcNow.AddSeconds(2); // Meldung kommt ggf. erst nach dem Schreiben
            var done = result.Written + result.CatalogOnly;
            var where = place is null ? string.Empty : $" ({place.Name})";
            if (result.Failed == 0)
            {
                ShowMessage($"{PlacesText.N(done)} Foto{(done == 1 ? string.Empty : "s")} verortet{where}.", isError: false);
            }
            else
            {
                var first = result.Errors.Count > 0 ? " — " + result.Errors[0] : string.Empty;
                ShowMessage($"{PlacesText.N(done)} verortet{where}, {PlacesText.N(result.Failed)} fehlgeschlagen{first}", isError: true);
            }

            _logger.LogInformation("Orte: {Count} Fotos verortet ({Written} in Datei, {CatalogOnly} nur Katalog, {Failed} Fehler)",
                ids.Count, result.Written, result.CatalogOnly, result.Failed);
            _anchor = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orte: Verorten fehlgeschlagen");
            ShowMessage("Verorten fehlgeschlagen: " + ex.Message, isError: true);
        }
        finally
        {
            IsBusy = false;
        }

        await ReloadAsync().ConfigureAwait(true);
    }

    private Services.Places.PlaceInfo? SafeNearest(double latitude, double longitude)
    {
        try
        {
            return _geocoder.Nearest(latitude, longitude);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Orte: nächster Ort nicht bestimmt");
            return null;
        }
    }

    // ---- Ortssuche -----------------------------------------------------------------------------------------------------

    [ObservableProperty] private string _searchText = string.Empty;

    public ObservableCollection<PlaceSearchResult> SearchResults { get; } = [];

    [ObservableProperty] private bool _isSearchOpen;

    partial void OnSearchTextChanged(string value)
    {
        SearchResults.Clear();
        var text = value.Trim();
        if (text.Length >= 2)
        {
            try
            {
                foreach (var (place, lat, lon) in _geocoder.Search(text, 8))
                {
                    SearchResults.Add(new PlaceSearchResult(place, lat, lon));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Orte: Ortssuche fehlgeschlagen");
            }
        }

        IsSearchOpen = SearchResults.Count > 0;
    }

    /// <summary>Karte springt zum gewählten Ort (Zoom 12 — Stadt).</summary>
    [RelayCommand]
    private void GoToSearchResult(PlaceSearchResult? result)
    {
        result ??= SearchResults.FirstOrDefault();
        if (result is null)
        {
            return;
        }

        IsSearchOpen = false;
        Viewport.CenterOn(result.Latitude, result.Longitude, 12);
    }
}
