using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumora.Services.Faces.Assignment;

namespace Lumora.Persons.ViewModels;

/// <summary>Detailansicht einer Person: Vorschläge (bestätigen/ablehnen) und bestätigte Gesichter (entfernen, Titelbild).</summary>
public sealed partial class PersonsViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PersonTitle), nameof(PersonDetails), nameof(HiddenToggleText), nameof(PersonInfoText))]
    private PersonSummary? _currentPerson;

    public string PersonTitle => CurrentPerson?.Name ?? string.Empty;

    public string PersonDetails => CurrentPerson is null
        ? string.Empty
        : $"{N(CurrentPerson.FaceCount)} bestätigte Gesichter auf {N(CurrentPerson.FileCount)} Fotos · {N(Suggestions.Count)} Vorschläge"
          + (CurrentPerson.IsHidden ? " · ausgeblendet" : string.Empty);

    public string HiddenToggleText => CurrentPerson?.IsHidden == true ? "Einblenden" : "Ausblenden";

    /// <summary>0 = Vorschläge, 1 = Bestätigt.</summary>
    [ObservableProperty] private int _detailTab;

    [ObservableProperty] private int _selectedSuggestionCount;

    [ObservableProperty] private int _selectedConfirmedCount;

    [ObservableProperty] private bool _hasSuggestions;

    [ObservableProperty] private bool _hasConfirmed;

    [RelayCommand]
    private async Task OpenPersonAsync(PersonTileViewModel? tile)
    {
        if (tile is null)
        {
            return;
        }

        await LoadPersonAsync(tile.PersonId, keepState: false).ConfigureAwait(true);
    }

    /// <summary>Lädt die Person; <paramref name="keepState"/> = Auswahl und Bilder vorhandener Kacheln behalten (Aktualisieren).</summary>
    public async Task LoadPersonAsync(int personId, bool keepState)
    {
        try
        {
            IsLoading = true;
            _libraryId = await _service.GetActiveLibraryIdAsync().ConfigureAwait(true);
            var faces = await _service.GetPersonFacesAsync(personId).ConfigureAwait(true);
            if (faces is null)
            {
                // Person gelöscht oder zusammengeführt — zurück zur Übersicht
                IsLoading = false;
                await BackAsync().ConfigureAwait(true);
                return;
            }

            Fill(Suggestions, faces.Suggested, keepState);
            Fill(Confirmed, faces.Confirmed, keepState);
            HasSuggestions = Suggestions.Count > 0;
            HasConfirmed = Confirmed.Count > 0;
            CurrentPerson = faces.Person;
            OnPropertyChanged(nameof(PersonDetails));
            if (!keepState)
            {
                DetailTab = HasSuggestions ? 0 : 1;
                Mode = PersonsPageMode.Person;
                RaiseScrollToTop();
            }

            UpdateSelectionState();
            RequestVisible(PersonsListKind.Suggestions);
            RequestVisible(PersonsListKind.Confirmed);
            UpdateRangeText(PersonsListKind.Suggestions);
        }
        catch (System.Exception ex)
        {
            ShowError("Person konnte nicht geladen werden: " + ex.Message);
        }
        finally
        {
            IsLoading = false;
            RunPendingRefresh();
        }

        EditPersonCommand.NotifyCanExecuteChanged();
        MergePersonCommand.NotifyCanExecuteChanged();
        ToggleHiddenCommand.NotifyCanExecuteChanged();
        DeletePersonCommand.NotifyCanExecuteChanged();
        ConfirmVisibleSuggestionsCommand.NotifyCanExecuteChanged();
        RejectVisibleSuggestionsCommand.NotifyCanExecuteChanged();
        ConfirmAllSuggestionsCommand.NotifyCanExecuteChanged();
        RejectAllSuggestionsCommand.NotifyCanExecuteChanged();
    }

    private void Fill(TileCollection<FaceTileViewModel> target, IReadOnlyList<PersonFaceInfo> faces, bool keepState)
    {
        var old = keepState ? target.ToDictionary(t => t.FaceId) : [];
        var tiles = faces.Select(f =>
        {
            var tile = new FaceTileViewModel(f.FaceId, f.FileId, f.State, f.SuggestionScore);
            if (old.TryGetValue(f.FaceId, out var previous))
            {
                tile.IsSelected = previous.IsSelected;
                tile.Image = previous.Image;
            }

            return tile;
        }).ToList();
        foreach (var previous in target)
        {
            previous.PropertyChanged -= OnTilePropertyChanged;
        }

        target.ReplaceAll(tiles);
        SubscribeSelection(tiles);
    }

    private bool CanActOnPerson() => !IsBusy && CurrentPerson is not null;

    private bool CanConfirmSelected() => !IsBusy && CurrentPerson is not null && SelectedSuggestionCount > 0;

    private bool CanActOnVisible() => !IsBusy && CurrentPerson is not null && Suggestions.Count > 0;

    private bool CanRemoveSelected() => !IsBusy && CurrentPerson is not null && SelectedConfirmedCount > 0;

    private bool CanSetCover() => !IsBusy && CurrentPerson is not null && SelectedConfirmedCount == 1;

    /// <summary>Einzelner Vorschlag (Knopf auf der Kachel).</summary>
    [RelayCommand]
    private Task ConfirmSuggestionAsync(FaceTileViewModel? tile)
        => tile is null ? Task.CompletedTask : ConfirmSuggestionsAsync([tile]);

    [RelayCommand]
    private Task RejectSuggestionAsync(FaceTileViewModel? tile)
        => tile is null ? Task.CompletedTask : RejectSuggestionsAsync([tile]);

    [RelayCommand(CanExecute = nameof(CanConfirmSelected))]
    private Task ConfirmSelectedSuggestionsAsync() => ConfirmSuggestionsAsync(Suggestions.Where(t => t.IsSelected).ToList());

    [RelayCommand(CanExecute = nameof(CanConfirmSelected))]
    private Task RejectSelectedSuggestionsAsync() => RejectSuggestionsAsync(Suggestions.Where(t => t.IsSelected).ToList());

    /// <summary>„Sichtbare bestätigen“: nur die Vorschläge, die gerade auf dem Bildschirm stehen (auch angeschnittene Reihe).</summary>
    [RelayCommand(CanExecute = nameof(CanActOnVisible))]
    private Task ConfirmVisibleSuggestionsAsync() => ConfirmSuggestionsAsync(VisibleTiles(Suggestions, PersonsListKind.Suggestions));

    [RelayCommand(CanExecute = nameof(CanActOnVisible))]
    private Task RejectVisibleSuggestionsAsync() => RejectSuggestionsAsync(VisibleTiles(Suggestions, PersonsListKind.Suggestions));

    /// <summary>„Alle Vorschläge bestätigen…“: die ganze Liste (auch nicht sichtbare) — Rückfrage mit Anzahl, danach Ausreißer-Prüfung.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnVisible))]
    private async Task ConfirmAllSuggestionsAsync()
    {
        if (CurrentPerson is not { } person || Suggestions.Count == 0)
        {
            return;
        }

        var tiles = Suggestions.ToList();
        var ok = await _dialogs.ConfirmAsync(
            "Alle Vorschläge bestätigen",
            $"Alle {N(tiles.Count)} Vorschläge als „{person.Name}“ bestätigen — auch die, die gerade nicht zu sehen sind? "
            + "Gesichter, die nicht sicher passen, werden danach noch einmal nachgefragt.",
            "Alle bestätigen").ConfigureAwait(true);
        if (ok)
        {
            await ConfirmSuggestionsAsync(tiles).ConfigureAwait(true);
        }
    }

    /// <summary>„Alle Vorschläge ablehnen…“: die ganze Liste — Rückfrage mit Anzahl.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnVisible))]
    private async Task RejectAllSuggestionsAsync()
    {
        if (CurrentPerson is not { } person || Suggestions.Count == 0)
        {
            return;
        }

        var tiles = Suggestions.ToList();
        var ok = await _dialogs.ConfirmAsync(
            "Alle Vorschläge ablehnen",
            $"Alle {N(tiles.Count)} Vorschläge für „{person.Name}“ ablehnen — auch die, die gerade nicht zu sehen sind? "
            + $"„{person.Name}“ wird für diese Gesichter nicht mehr vorgeschlagen.",
            "Alle ablehnen").ConfigureAwait(true);
        if (ok)
        {
            await RejectSuggestionsAsync(tiles).ConfigureAwait(true);
        }
    }

    private Task ConfirmSuggestionsAsync(IReadOnlyList<FaceTileViewModel> tiles)
    {
        if (CurrentPerson is not { } person || tiles.Count == 0)
        {
            return Task.CompletedTask;
        }

        return RunAsync("Bestätigen", async () =>
        {
            var count = await ConfirmWithCheckAsync(tiles.Select(t => t.FaceId).ToList(), person.Id, person.Name).ConfigureAwait(true);
            if (count > 0)
            {
                ShowInfo(count == 1 ? $"1 Gesicht als „{person.Name}“ bestätigt." : $"{N(count)} Gesichter als „{person.Name}“ bestätigt.");
            }
        });
    }

    private Task RejectSuggestionsAsync(IReadOnlyList<FaceTileViewModel> tiles)
    {
        if (CurrentPerson is not { } person || tiles.Count == 0)
        {
            return Task.CompletedTask;
        }

        return RunAsync("Ablehnen", async () =>
        {
            await _service.RejectSuggestionsAsync(tiles.Select(t => t.FaceId).ToList()).ConfigureAwait(true);
            ShowInfo(tiles.Count == 1 ? $"Vorschlag abgelehnt — „{person.Name}“ wird für dieses Gesicht nicht mehr vorgeschlagen." : $"{N(tiles.Count)} Vorschläge abgelehnt.");
        });
    }

    /// <summary>Bestätigte Gesichter aus der Person nehmen (z. B. falsch zugeordnet) — danach wird neu gelernt.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveSelected))]
    private Task RemoveSelectedConfirmedAsync()
    {
        var ids = Confirmed.Where(t => t.IsSelected).Select(t => t.FaceId).ToList();
        if (CurrentPerson is not { } person || ids.Count == 0)
        {
            return Task.CompletedTask;
        }

        return RunAsync("Entfernen", async () =>
        {
            await _service.RemoveFromPersonAsync(ids).ConfigureAwait(true);
            ShowInfo(ids.Count == 1 ? $"1 Gesicht aus „{person.Name}“ entfernt." : $"{N(ids.Count)} Gesichter aus „{person.Name}“ entfernt.");
        });
    }

    [RelayCommand(CanExecute = nameof(CanSetCover))]
    private Task SetCoverAsync()
    {
        var tile = Confirmed.FirstOrDefault(t => t.IsSelected);
        if (CurrentPerson is not { } person || tile is null)
        {
            return Task.CompletedTask;
        }

        return RunAsync("Titelbild setzen", async () =>
        {
            await _service.SetCoverAsync(person.Id, tile.FaceId).ConfigureAwait(true);
            tile.IsSelected = false;
            ShowInfo("Titelbild gesetzt.");
        });
    }

    // „Bearbeiten…“ (früher „Umbenennen…“): PersonsViewModel.Details.cs (0.2.47, ADR-0045)

    /// <summary>„Zusammenführen mit…“: diese Person geht in der gewählten auf (alle Gesichter, Vorschläge, Ablehnungen).</summary>
    [RelayCommand(CanExecute = nameof(CanActOnPerson))]
    private async Task MergePersonAsync()
    {
        if (CurrentPerson is not { } person)
        {
            return;
        }

        var persons = await _service.GetPersonsAsync(default).ConfigureAwait(true);
        var target = await _dialogs.PickPersonAsync($"„{person.Name}“ zusammenführen mit…", persons, null, person.Id).ConfigureAwait(true);
        if (target is null)
        {
            return;
        }

        var ok = await _dialogs.ConfirmAsync(
            "Personen zusammenführen",
            $"Alle Gesichter von „{person.Name}“ werden „{target.Name}“ zugeordnet; „{person.Name}“ wird gelöscht.",
            "Zusammenführen").ConfigureAwait(true);
        if (!ok)
        {
            return;
        }

        await RunAsync("Zusammenführen", async () =>
        {
            await _service.MergePersonsAsync(person.Id, target.Id).ConfigureAwait(true);
            await LoadPersonAsync(target.Id, keepState: false).ConfigureAwait(true);
            ShowInfo($"„{person.Name}“ mit „{target.Name}“ zusammengeführt.");
        }, reload: false).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanActOnPerson))]
    private Task ToggleHiddenAsync()
    {
        if (CurrentPerson is not { } person)
        {
            return Task.CompletedTask;
        }

        return RunAsync("Ausblenden", () => _service.SetPersonHiddenAsync(person.Id, !person.IsHidden));
    }

    [RelayCommand(CanExecute = nameof(CanActOnPerson))]
    private async Task DeletePersonAsync()
    {
        if (CurrentPerson is not { } person)
        {
            return;
        }

        var ok = await _dialogs.ConfirmAsync(
            "Person löschen",
            $"„{person.Name}“ löschen? Die {N(person.FaceCount)} Gesichter bleiben erhalten und werden wieder unbenannt.",
            "Löschen",
            isDestructive: true).ConfigureAwait(true);
        if (!ok)
        {
            return;
        }

        await RunAsync("Löschen", async () =>
        {
            await _service.DeletePersonAsync(person.Id).ConfigureAwait(true);
            CurrentPerson = null;
            Mode = PersonsPageMode.Overview;
            ShowInfo($"„{person.Name}“ gelöscht.");
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private void SelectAllSuggestions()
    {
        BatchSelection(() =>
        {
            foreach (var tile in Suggestions)
            {
                tile.IsSelected = true;
            }
        });
    }

    [RelayCommand]
    private void ClearDetailSelection()
    {
        BatchSelection(() =>
        {
            foreach (var tile in Suggestions.Concat(Confirmed))
            {
                tile.IsSelected = false;
            }
        });
    }
}
