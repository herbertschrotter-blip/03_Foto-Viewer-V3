using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Gallery.Data;
using Lumora.Gallery.Places;
using Lumora.Services.Messaging;
using Lumora.Services.Places;

namespace Lumora.Gallery.ViewModels;

/// <summary>
/// Orte in der Galerie (ADR-0050): Filter „Ort“ (Chip in der Zeile „Inhalt“, Flyout Land → Region → Ort mit Anzahl,
/// „ohne Ort“), Filter „Kartenausschnitt“ (von der Karte über <see cref="ShowFilesOnMapSelectionMessage"/>), beide als
/// Teil von <see cref="GalleryFilterState"/> UND-verknüpft und in der „Es gilt“-Zeile; „Ort setzen…“ für die Auswahl,
/// Rückgängig gemeinsam mit Datei-Operationen und „Datum setzen“ (jüngster Eintrag gewinnt), Ort je Kachel (Listenspalte
/// „Ort“, Tooltip) und Neuladen nach <see cref="GeoLocationsChangedMessage"/>. Alle Abfragen nur im Bibliotheksbereich.
/// </summary>
public sealed partial class GalleryViewModel
{
    private PlaceActions? _placeActions;
    private GalleryPlaceFilter? _placeFilter;
    private GalleryGeoFilter? _geoFilter;
    private bool _placeEntriesStale = true;
    private long _placesRequestSequence;
    private long _placeItemsSequence;

    /// <summary>Einträge des Flyouts „Ort“ (flach, eingerückt; Sichtbarkeit über <see cref="PlaceFilterEntry.IsVisible"/>).</summary>
    public ObservableCollection<PlaceFilterEntry> PlaceFilterEntries { get; } = new();

    /// <summary>Suchtext im Flyout „Ort“.</summary>
    [ObservableProperty] private string? _placeFilterSearch;

    /// <summary>Flyout lädt gerade die Ortszähler.</summary>
    [ObservableProperty] private bool _isLoadingPlaces;

    public bool HasPlaceFilter => _placeFilter is { IsActive: true };

    public bool HasMapAreaFilter => _geoFilter is { IsActive: true };

    /// <summary>Wert im Chip „Ort: …“ („alle“ ohne Filter).</summary>
    public string PlaceFilterValueText => _placeFilter is { IsActive: true, Label: { } label } ? label : "alle";

    /// <summary>Text der Bedingung „Kartenausschnitt (12 Dateien)“.</summary>
    public string MapAreaText => _geoFilter is { FileIds: { Count: > 0 } ids } ? $"Kartenausschnitt ({ids.Count:N0} Dateien)" : "Kartenausschnitt";

    /// <summary>Bibliotheksbereich der Ortszähler und „zuletzt verwendeten Orte“ (ADR-0048): aktive Bibliothek bzw. alle der Gruppe in „Alle verknüpften“.</summary>
    private IReadOnlyCollection<int> PlaceLibraryIds => HasLibrary ? ScopeLibraryIds : [];

    private void InitPlaces(PlaceActions? actions)
    {
        _placeActions = actions;
        _messenger.Register<GalleryViewModel, GeoLocationsChangedMessage>(this, static (r, m) => r.RunOnUi(() => r.HandleGeoLocationsChangedAsync(m)));
        _messenger.Register<GalleryViewModel, ShowFilesOnMapSelectionMessage>(this, static (r, m) => r.RunOnUi(() => r.ShowMapSelectionAsync(m)));
        PropertyChanged += OnPlaceCommandStateChanged;
    }

    private void OnPlaceCommandStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SelectedCount) or nameof(IsOperationRunning))
        {
            SetSelectionPlaceCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnPlaceFilterSearchChanged(string? value) => PlaceFilterEntry.UpdateVisibility(PlaceFilterEntries, value);

    // ---- Filter „Ort“ -------------------------------------------------------------------------------

    /// <summary>Flyout geöffnet: Ortszähler des Bereichs laden (nur wenn veraltet), Auswahl bleibt erhalten.</summary>
    [RelayCommand]
    private async Task LoadPlaceFilterAsync()
    {
        if (!_placeEntriesStale && PlaceFilterEntries.Count > 0)
        {
            return;
        }

        await ReloadPlaceEntriesAsync();
    }

    private async Task ReloadPlaceEntriesAsync()
    {
        if (_placeActions is null || !HasLibrary)
        {
            return;
        }

        var sequence = Interlocked.Increment(ref _placesRequestSequence);
        IsLoadingPlaces = true;
        try
        {
            var summary = await _placeActions.Reader.GetPlaceCountsAsync(PlaceLibraryIds);
            var entries = await Task.Run(() => PlaceFilterEntry.Build(summary, _placeActions.Places));
            if (sequence != Volatile.Read(ref _placesRequestSequence))
            {
                return;
            }

            var selected = PlaceFilterEntries.Where(e => e.IsSelected).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
            var expanded = PlaceFilterEntries.Where(e => e.IsExpanded).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
            var hadEntries = PlaceFilterEntries.Count > 0;
            PlaceFilterEntries.Clear();
            foreach (var entry in entries)
            {
                entry.IsSelected = selected.Contains(entry.Key);
                if (hadEntries && entry.HasChildren)
                {
                    entry.IsExpanded = expanded.Contains(entry.Key);
                }

                PlaceFilterEntries.Add(entry);
            }

            PlaceFilterEntry.UpdateVisibility(PlaceFilterEntries, PlaceFilterSearch);
            _placeEntriesStale = false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ortszähler konnten nicht geladen werden");
        }
        finally
        {
            IsLoadingPlaces = false;
        }
    }

    /// <summary>Land/Region auf- oder zuklappen.</summary>
    [RelayCommand]
    private void TogglePlaceEntry(PlaceFilterEntry? entry)
    {
        if (entry is { HasChildren: true })
        {
            entry.IsExpanded = !entry.IsExpanded;
            PlaceFilterEntry.UpdateVisibility(PlaceFilterEntries, PlaceFilterSearch);
        }
    }

    /// <summary>Häkchen im Flyout geändert → Filter anwenden.</summary>
    [RelayCommand]
    private Task ApplyPlaceFilterAsync()
    {
        _placeFilter = BuildPlaceFilter();

        NotifyPlaceFilterChanged();
        return _suppressReload || !_isInitialized ? Task.CompletedTask : ChangeQueryAsync();
    }

    /// <summary>„Leeren“ im Flyout bzw. × an der Bedingung.</summary>
    [RelayCommand]
    private Task ClearPlaceFilterAsync()
    {
        var had = HasPlaceFilter;
        foreach (var entry in PlaceFilterEntries)
        {
            entry.IsSelected = false;
        }

        _placeFilter = null;
        NotifyPlaceFilterChanged();
        return had && !_suppressReload && _isInitialized ? ChangeQueryAsync() : Task.CompletedTask;
    }

    /// <summary>× an „Kartenausschnitt“.</summary>
    [RelayCommand]
    private Task ClearMapAreaFilterAsync()
    {
        if (!HasMapAreaFilter)
        {
            return Task.CompletedTask;
        }

        _geoFilter = null;
        NotifyPlaceFilterChanged();
        return ChangeQueryAsync();
    }

    /// <summary>Filter aus der Auswahl: gewählte Länder/Regionen/Orte → PlaceIds, dazu „ohne Ort“/„ohne Ortsnamen“.</summary>
    private GalleryPlaceFilter? BuildPlaceFilter()
    {
        var chosen = PlaceFilterEntries.Where(e => e.IsSelected).ToList();
        if (chosen.Count == 0)
        {
            return null;
        }

        // Ein gewähltes Land deckt seine Regionen und Orte ab — im Text nur die oberste Wahl nennen.
        var top = chosen.Where(e => !HasSelectedAncestor(e)).ToList();
        var ids = top.SelectMany(e => e.PlaceIds).Distinct().ToList();
        var labels = top.Select(e => e.Kind switch
        {
            PlaceFilterEntryKind.WithoutLocation => "ohne Ort",
            PlaceFilterEntryKind.WithoutPlaceName => "ohne Ortsnamen",
            _ => e.Name,
        }).ToList();
        var label = labels.Count <= 3 ? string.Join(" oder ", labels) : $"{string.Join(", ", labels.Take(2))} und {labels.Count - 2} weitere";
        return new GalleryPlaceFilter(
            ids,
            top.Any(e => e.Kind == PlaceFilterEntryKind.WithoutLocation),
            top.Any(e => e.Kind == PlaceFilterEntryKind.WithoutPlaceName),
            label);

        static bool HasSelectedAncestor(PlaceFilterEntry e)
        {
            for (var p = e.Parent; p is not null; p = p.Parent)
            {
                if (p.IsSelected)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Ort- und Kartenfilter zurücksetzen (aus <c>ResetFileFilter</c>; ohne Neuladen).</summary>
    private void ResetPlaceFilterCore()
    {
        foreach (var entry in PlaceFilterEntries)
        {
            entry.IsSelected = false;
        }

        _placeFilter = null;
        _geoFilter = null;
        _placeEntriesStale = true;
    }

    /// <summary>Aus dem Setter von <c>FilterState</c> (Rauchtest, Tests).</summary>
    private void SetPlaceFiltersFromState(GalleryFilterState value)
    {
        _placeFilter = value.Place is { IsActive: true } ? value.Place : null;
        _geoFilter = value.Geo is { IsActive: true } ? value.Geo : null;
        if (_placeFilter is null)
        {
            foreach (var entry in PlaceFilterEntries)
            {
                entry.IsSelected = false;
            }
        }
    }

    private void NotifyPlaceFilterChanged()
    {
        OnPropertyChanged(nameof(HasPlaceFilter));
        OnPropertyChanged(nameof(HasMapAreaFilter));
        OnPropertyChanged(nameof(PlaceFilterValueText));
        OnPropertyChanged(nameof(MapAreaText));
        OnPropertyChanged(nameof(FilterState));
        OnPropertyChanged(nameof(EmptyMessage));
        RefreshFilterIndicators();
    }

    /// <summary>Bedingungen für die „Es gilt“-Zeile (aus <c>BuildConditions</c>).</summary>
    private IEnumerable<(GalleryFilterConditionKind Kind, string Label)> PlaceConditions()
    {
        if (_placeFilter is { IsActive: true, Label: { } label })
        {
            yield return (GalleryFilterConditionKind.Place, "Ort: " + label);
        }

        if (HasMapAreaFilter)
        {
            yield return (GalleryFilterConditionKind.MapArea, MapAreaText);
        }
    }

    /// <summary>Karte → Galerie: Dateien der Kartengruppe zeigen (Filter „Kartenausschnitt“).</summary>
    private Task ShowMapSelectionAsync(ShowFilesOnMapSelectionMessage message)
    {
        _geoFilter = new GalleryGeoFilter(message.Bounds, message.FileIds is { Count: > 0 } ids ? ids.ToList() : null);
        _logger.LogInformation("Galerie: Kartenausschnitt mit {Count} Datei(en)", message.FileIds?.Count ?? 0);
        NotifyPlaceFilterChanged();
        return HasLibrary && _isInitialized ? ChangeQueryAsync() : Task.CompletedTask;
    }

    // ---- Ort je Kachel ------------------------------------------------------------------------------

    /// <summary>Koordinaten und Orte der Dateien (Batch) laden und den Items der Seite zuordnen.</summary>
    private async Task ApplyPlacesToItemsAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct)
    {
        if (_placeActions is null || fileIds.Count == 0)
        {
            return;
        }

        var sequence = Interlocked.Increment(ref _placeItemsSequence);
        try
        {
            var rows = await _placeActions.Reader.GetForFilesAsync(fileIds, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            var byFile = rows.ToDictionary(r => r.FileId);
            var lookup = _placeActions.Places;
            await DispatchAsync(() =>
            {
                foreach (var id in fileIds)
                {
                    if (!_currentItems.TryGetValue(id, out var item) || item.PlaceSequence > sequence)
                    {
                        continue;
                    }

                    item.PlaceSequence = sequence;
                    item.Place = byFile.TryGetValue(id, out var row)
                        ? new GalleryItemPlace(row.Latitude, row.Longitude, row.PlaceId is { } p ? lookup.GetPlace(p) : null)
                        : null;
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orte der Seite konnten nicht geladen werden");
        }
    }

    private async Task HandleGeoLocationsChangedAsync(GeoLocationsChangedMessage m)
    {
        if (!HasLibrary || !InScope(m.LibraryId))
        {
            return;
        }

        _placeEntriesStale = true;
        var onPage = m.FileIds.Where(_currentItems.ContainsKey).ToList();
        if (onPage.Count > 0)
        {
            await ApplyPlacesToItemsAsync(onPage, CancellationToken.None);
        }

        if (HasPlaceFilter || HasMapAreaFilter)
        {
            if (HasPlaceFilter)
            {
                await ReloadPlaceEntriesAsync(); // neue Orte eines gewählten Landes gehören dazu
                _placeFilter = BuildPlaceFilter() ?? _placeFilter;
                NotifyPlaceFilterChanged();
            }

            ScheduleReload();
        }

        await RefreshCanUndoAsync();
    }

    // ---- „Ort setzen…“ und Rückgängig ----------------------------------------------------------------

    private bool CanSetSelectionPlace() => CanActOnSelection && _placeActions is not null;

    /// <summary>„Ort setzen…“ für die Auswahl.</summary>
    [RelayCommand(CanExecute = nameof(CanSetSelectionPlace))]
    private Task SetSelectionPlaceAsync()
        => RunOperationAsync("Ort setzen", async () =>
        {
            var ids = SelectedIdsSnapshot();
            if (_placeActions is null || ids.Count == 0)
            {
                return;
            }

            var hasVideos = ids.Any(id => _currentItems.TryGetValue(id, out var item) && item.IsVideo);
            var result = await _placeActions.SetForFilesAsync(ids, PlaceLibraryIds, hasVideos, OwnerWindowResolver?.Invoke() ?? ActiveWindow());
            if (result is null)
            {
                return;
            }

            SetResultStatus(result.Text);
            if (result.Result.Failed > 0)
            {
                await _dialogs.ShowErrorsAsync(
                    $"Ort setzen: {result.Result.Failed:N0} Fehler",
                    $"{result.Result.Failed:N0} Datei(en) konnten nicht geändert werden — sie bleiben unverändert.",
                    result.Result.Errors);
            }
        });

    /// <summary>
    /// Rückgängig: nimmt „Ort setzen“ zurück, wenn das der jüngste rückgängig machbare Eintrag ist (vor Datei-Operationen
    /// und „Datum setzen“; gemeinsame Reihenfolge über die Batch-Id).
    /// </summary>
    private async Task<bool> TryUndoGeoTagAsync()
    {
        if (_placeActions?.Undo is not { } undo || await undo.GetUndoableBatchIdAsync() is not { } batchId)
        {
            return false;
        }

        var batches = await _fileOps.GetRecentBatchesAsync(UndoBatchWindow);
        if (batches.Where(b => b.CanUndo).Select(b => (int?)b.Id).Max() is { } newestFileOperation && newestFileOperation > batchId)
        {
            return false;
        }

        if (_captureDateActions?.Service is { } capture && await capture.GetUndoableBatchIdAsync() is { } captureBatch && captureBatch > batchId)
        {
            return false;
        }

        var result = await undo.UndoAsync(batchId);
        if (result is null)
        {
            return false;
        }

        _logger.LogInformation("Rückgängig: Ort setzen (Batch {BatchId}) — {Count} Datei(en)", batchId, result.Restored);
        SetResultStatus(result.Failed == 0
            ? $"Rückgängig: {result.Description} — {result.Restored:N0} Datei(en) zurückgesetzt."
            : $"Rückgängig: {result.Description} — {result.Restored:N0} zurückgesetzt, {result.Failed:N0} nicht möglich.");
        if (result.Failed > 0)
        {
            await _dialogs.ShowErrorsAsync("Rückgängig: Ort setzen", $"{result.Failed:N0} Datei(en) wurden inzwischen verändert und bleiben, wie sie sind.", result.Errors);
        }

        return true;
    }

    private async Task<bool> HasGeoTagUndoAsync()
        => _placeActions?.Undo is { } undo && await undo.GetUndoableBatchIdAsync() is not null;
}
