using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Faces.Assignment;

namespace PhotoViewerV3.Persons.ViewModels;

/// <summary>
/// Ansicht „Ignorierte Gesichter“ (ADR-0040): manuell und automatisch (zu klein) ignorierte Gesichter der aktiven
/// Bibliothek, seitenweise geladen (<see cref="IgnoredPageSize"/>, nachgeladen, sobald der sichtbare Bereich das Ende
/// erreicht), Kacheln UI-virtualisiert wie die übrigen. Auswahl → „Zurückholen“ (wieder unbestätigt, danach Vorschläge
/// neu), Doppelklick → Lightbox. Außerdem die Zeile „zeigt … von …“ über den Listen (sichtbarer Bereich).
/// </summary>
public sealed partial class PersonsViewModel
{
    /// <summary>So viele ignorierte Gesichter je Abruf.</summary>
    public const int IgnoredPageSize = 500;

    private readonly IFaceIgnoreService? _ignore;
    private int _ignoredTotal;
    private bool _ignoredLoadingMore;

    public TileCollection<FaceTileViewModel> Ignored { get; } = [];

    public bool IsIgnoredView => Mode == PersonsPageMode.Ignored;

    /// <summary>Ansicht verfügbar (Dienst registriert).</summary>
    public bool CanShowIgnored => _ignore is not null;

    /// <summary>Knopf in der Übersicht: „Ignorierte Gesichter (12)“.</summary>
    [ObservableProperty] private string _ignoredButtonText = "Ignorierte Gesichter";

    [ObservableProperty] private string _ignoredSummary = string.Empty;

    [ObservableProperty] private bool _hasIgnored;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreIgnoredCommand))]
    private int _selectedIgnoredCount;

    /// <summary>„Zeigt 1–14 von 30 — nach unten scrollen für weitere“ (Vorschläge).</summary>
    [ObservableProperty] private string _suggestionsRangeText = string.Empty;

    /// <summary>Wie <see cref="SuggestionsRangeText"/> für die ignorierten Gesichter.</summary>
    [ObservableProperty] private string _ignoredRangeText = string.Empty;

    private async Task LoadIgnoredCountAsync()
    {
        if (_ignore is null)
        {
            return;
        }

        try
        {
            var page = await _ignore.GetIgnoredFacesAsync(0, 0).ConfigureAwait(true);
            IgnoredButtonText = page.Total == 0 ? "Ignorierte Gesichter" : $"Ignorierte Gesichter ({N(page.Total)})";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Personen-Seite: Zahl der ignorierten Gesichter nicht gelesen");
        }
    }

    [RelayCommand]
    private async Task ShowIgnoredAsync()
    {
        if (_ignore is null)
        {
            return;
        }

        Message = null;
        Mode = PersonsPageMode.Ignored;
        await LoadIgnoredAsync(keepState: false).ConfigureAwait(true);
        RaiseScrollToTop();
    }

    /// <summary>Lädt die erste Seite (bzw. beim Aktualisieren so viele, wie schon geladen waren).</summary>
    public async Task LoadIgnoredAsync(bool keepState)
    {
        if (_ignore is null)
        {
            return;
        }

        try
        {
            IsLoading = true;
            _libraryId = await _service.GetActiveLibraryIdAsync().ConfigureAwait(true);
            var count = keepState ? Math.Max(IgnoredPageSize, Ignored.Count) : IgnoredPageSize;
            var page = await _ignore.GetIgnoredFacesAsync(0, count).ConfigureAwait(true);
            var old = keepState ? Ignored.ToDictionary(t => t.FaceId) : [];
            FillIgnored(page.Faces.Select(f => ToTile(f, old)).ToList());
            ApplyIgnoredPage(page);
            UpdateSelectionState();
            RequestVisible(PersonsListKind.Ignored);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Personen-Seite: ignorierte Gesichter nicht geladen");
            ShowError("Ignorierte Gesichter konnten nicht geladen werden: " + ex.Message);
        }
        finally
        {
            IsLoading = false;
            RunPendingRefresh();
        }
    }

    /// <summary>Nächste Seite anhängen (sichtbarer Bereich am Ende der geladenen Kacheln).</summary>
    private async Task LoadMoreIgnoredAsync()
    {
        if (_ignore is null || _ignoredLoadingMore || Ignored.Count >= _ignoredTotal)
        {
            return;
        }

        _ignoredLoadingMore = true;
        try
        {
            // Gemeldet wird aus dem Measure-Durchlauf des Panels — die Liste erst danach verlängern.
            await Task.Yield();
            var page = await _ignore.GetIgnoredFacesAsync(Ignored.Count, IgnoredPageSize).ConfigureAwait(true);
            var known = Ignored.Select(t => t.FaceId).ToHashSet();
            var more = page.Faces.Where(f => !known.Contains(f.FaceId)).Select(f => ToTile(f, null)).ToList();
            if (more.Count > 0)
            {
                FillIgnored([.. Ignored, .. more], keepSubscriptions: true);
            }

            ApplyIgnoredPage(page);
            RequestVisible(PersonsListKind.Ignored);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Personen-Seite: weitere ignorierte Gesichter nicht geladen");
        }
        finally
        {
            _ignoredLoadingMore = false;
        }
    }

    private static FaceTileViewModel ToTile(IgnoredFaceInfo face, Dictionary<long, FaceTileViewModel>? old)
    {
        var tile = new FaceTileViewModel(face.FaceId, face.FileId, FaceState.Ignored, null)
        {
            IsAutoIgnored = face.IsAutomatic,
            FaceSize = face.Size,
        };
        if (old is not null && old.TryGetValue(face.FaceId, out var previous))
        {
            tile.IsSelected = previous.IsSelected;
            tile.Image = previous.Image;
        }

        return tile;
    }

    private void FillIgnored(IReadOnlyList<FaceTileViewModel> tiles, bool keepSubscriptions = false)
    {
        var subscribed = keepSubscriptions ? Ignored.ToHashSet() : [];
        foreach (var previous in Ignored.Where(t => !subscribed.Contains(t)))
        {
            previous.PropertyChanged -= OnTilePropertyChanged;
        }

        Ignored.ReplaceAll(tiles);
        SubscribeSelection(tiles.Where(t => !subscribed.Contains(t)));
    }

    private void ApplyIgnoredPage(IgnoredFacesPage page)
    {
        _ignoredTotal = page.Total;
        HasIgnored = page.Total > 0;
        IgnoredButtonText = page.Total == 0 ? "Ignorierte Gesichter" : $"Ignorierte Gesichter ({N(page.Total)})";
        IgnoredSummary = page.Total == 0
            ? "Keine ignorierten Gesichter."
            : $"{N(page.Total)} ignorierte Gesichter"
              + (page.AutomaticCount > 0 ? $", davon {N(page.AutomaticCount)} automatisch (kleiner als die Mindestgröße, markiert „zu klein“)" : string.Empty)
              + ". Klick wählt aus, Doppelklick öffnet das Foto.";
        UpdateRangeText(PersonsListKind.Ignored);
    }

    private void ClearIgnored()
    {
        FillIgnored([]);
        _ignoredTotal = 0;
        IgnoredRangeText = string.Empty;
    }

    private bool CanRestoreIgnored() => !IsBusy && _ignore is not null && SelectedIgnoredCount > 0;

    /// <summary>„Zurückholen“: ausgewählte ignorierte Gesichter wieder unbestätigt — danach werden Vorschläge neu berechnet.</summary>
    [RelayCommand(CanExecute = nameof(CanRestoreIgnored))]
    private Task RestoreIgnoredAsync()
    {
        var ids = Ignored.Where(t => t.IsSelected).Select(t => t.FaceId).ToList();
        if (_ignore is null || ids.Count == 0)
        {
            return Task.CompletedTask;
        }

        return RunAsync("Zurückholen", async () =>
        {
            var count = await _ignore.UnignoreFacesAsync(ids).ConfigureAwait(true);
            ShowInfo(count == 1
                ? "1 Gesicht zurückgeholt — es erscheint wieder in Gruppen und Vorschlägen."
                : $"{N(count)} Gesichter zurückgeholt — sie erscheinen wieder in Gruppen und Vorschlägen.");
        });
    }

    [RelayCommand]
    private void SelectAllIgnored() => BatchSelection(() =>
    {
        foreach (var tile in Ignored)
        {
            tile.IsSelected = true;
        }
    });

    [RelayCommand]
    private void ClearIgnoredSelection() => BatchSelection(() =>
    {
        foreach (var tile in Ignored)
        {
            tile.IsSelected = false;
        }
    });

    /// <summary>Sichtbarer Bereich gemeldet: Zeile „zeigt … von …“, am Ende der ignorierten nachladen.</summary>
    private void OnVisibleRangeReported(PersonsListKind list)
    {
        UpdateRangeText(list);
        if (list == PersonsListKind.Ignored && _visible.TryGetValue(list, out var range)
            && range.Last >= Ignored.Count - TileCollection<FaceTileViewModel>.Buffer && Ignored.Count < _ignoredTotal)
        {
            _ = LoadMoreIgnoredAsync();
        }
    }

    private void UpdateRangeText(PersonsListKind list)
    {
        if (list == PersonsListKind.Suggestions)
        {
            SuggestionsRangeText = RangeText(PersonsListKind.Suggestions, Suggestions.Count, Suggestions.Count, "Vorschläge", "Vorschlägen");
        }
        else if (list == PersonsListKind.Ignored)
        {
            IgnoredRangeText = RangeText(PersonsListKind.Ignored, Ignored.Count, _ignoredTotal, "ignorierte Gesichter", "ignorierten Gesichtern");
        }
    }

    /// <summary>„Zeigt 1–14 von 30 Vorschlägen — nach unten scrollen für weitere.“ bzw. „Alle 6 Vorschläge sichtbar.“</summary>
    private string RangeText(PersonsListKind list, int loaded, int total, string noun, string nounDative)
    {
        if (total == 0)
        {
            return string.Empty;
        }

        if (!_visible.TryGetValue(list, out var range) || range.First < 0 || loaded == 0)
        {
            return $"{N(total)} {noun}";
        }

        var first = Math.Min(range.First, loaded - 1) + 1;
        var last = Math.Min(range.Last, loaded - 1) + 1;
        return first == 1 && last >= total
            ? $"Alle {N(total)} {noun} sichtbar."
            : $"Zeigt {N(first)}–{N(last)} von {N(total)} {nounDative}" + (last < total ? " — nach unten scrollen für weitere." : ".");
    }

    partial void OnDetailTabChanged(int value)
    {
        // Reiterwechsel: Ausschnitte des jetzt sichtbaren Reiters anfordern, auch wenn sein Panel denselben Bereich
        // wie zuvor meldet (dann kommt kein VisibleRangeChanged).
        RequestVisible(value == 0 ? PersonsListKind.Suggestions : PersonsListKind.Confirmed);
    }
}
