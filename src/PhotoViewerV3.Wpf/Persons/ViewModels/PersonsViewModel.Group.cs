using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoViewerV3.Services.Faces.Assignment;

namespace PhotoViewerV3.Persons.ViewModels;

/// <summary>
/// Gruppenansicht: Gesichter nach Nähe zum Zentrum sortiert, Ausreißer am Ende deutlich markiert und standardmäßig
/// ABGEWÄHLT (Herberts Sorge „lernt es falsch“, ADR-0036/0037). Benennen/Hinzufügen nur für die Auswahl, mit
/// Ausreißer-Rückfrage.
/// </summary>
public sealed partial class PersonsViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupTitle), nameof(GroupHint))]
    private FaceGroupInfo? _currentGroup;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupSelectionText))]
    private int _groupSelectedCount;

    public string GroupTitle => CurrentGroup is null ? string.Empty : $"Unbenannte Gruppe · {N(CurrentGroup.Count)} Gesichter";

    /// <summary>Hinweis auf Ausreißer und Namensvorschlag.</summary>
    public string? GroupHint
    {
        get
        {
            if (CurrentGroup is null)
            {
                return null;
            }

            var parts = new List<string>();
            if (CurrentGroup.OutlierCount > 0)
            {
                parts.Add(CurrentGroup.OutlierCount == 1
                    ? "1 Gesicht passt nicht sicher dazu — es steht am Ende, ist markiert und abgewählt."
                    : $"{N(CurrentGroup.OutlierCount)} Gesichter passen nicht sicher dazu — sie stehen am Ende, sind markiert und abgewählt.");
            }

            if (CurrentGroup.SuggestedPersonName is { } name)
            {
                parts.Add($"Ähnelt „{name}“ — „Zu Person hinzufügen…“ schlägt sie vor.");
            }

            parts.Add("Klick wählt ein Gesicht ab oder an; Doppelklick öffnet das Foto.");
            return string.Join(" ", parts);
        }
    }

    public string GroupSelectionText => $"{N(GroupSelectedCount)} von {N(GroupMembers.Count)} ausgewählt";

    [RelayCommand]
    private void OpenGroup(GroupTileViewModel? tile)
    {
        if (tile is null)
        {
            return;
        }

        ShowGroup(tile.Group);
    }

    /// <summary>Zeigt die Gruppe: Reihenfolge wie geliefert (nächste zuerst, Ausreißer am Ende), Ausreißer abgewählt.</summary>
    public void ShowGroup(FaceGroupInfo group)
    {
        foreach (var previous in GroupMembers)
        {
            previous.PropertyChanged -= OnTilePropertyChanged;
        }

        var groupTile = Groups.FirstOrDefault(g => g.Group.GroupId == group.GroupId);
        var tiles = group.Members.Select(m => new FaceTileViewModel(m.FaceId, m.FileId, Domain.Enums.FaceState.Unconfirmed, null, m.IsOutlier, m.Similarity)
        {
            IsSelected = !m.IsOutlier,
        }).ToList();
        if (groupTile is { Image: { } image } && tiles.Count > 0 && groupTile.ImageFaceId == tiles[0].FaceId)
        {
            tiles[0].Image = image; // Titelbild der Gruppe = dasselbe Gesicht, schon geladen
        }

        GroupMembers.ReplaceAll(tiles);
        SubscribeSelection(tiles);
        CurrentGroup = group;
        Mode = PersonsPageMode.Group;
        IgnoreWholeGroupCommand.NotifyCanExecuteChanged();
        Message = null;
        UpdateSelectionState();
        OnPropertyChanged(nameof(GroupSelectionText));
        RaiseScrollToTop();
        RequestVisible(PersonsListKind.GroupMembers);
    }

    private bool CanActOnGroup() => !IsBusy && CurrentGroup is not null && GroupSelectedCount > 0;

    private IReadOnlyList<long> SelectedGroupFaces() => GroupMembers.Where(t => t.IsSelected).Select(t => t.FaceId).ToList();

    /// <summary>
    /// „Als neue Person benennen…“: Vor-/Nachname, Rufname, Beziehung, Geburtsdatum abfragen (0.2.47, ADR-0045),
    /// Ausreißer-Prüfung, anlegen, bestätigen. Gibt es den Anzeigenamen schon, werden die Gesichter nach Rückfrage der
    /// vorhandenen Person hinzugefügt (ihre Details bleiben unverändert).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanActOnGroup))]
    private async Task NameGroupAsync()
    {
        var ids = SelectedGroupFaces();
        var input = await PromptPersonAsync(
            "Als neue Person benennen",
            $"Wer ist auf den {N(ids.Count)} ausgewählten Gesichtern? Angezeigt wird der Rufname, sonst Vor- und Nachname.",
            "Benennen",
            null,
            null,
            ExistingPersonHintForGroup).ConfigureAwait(true);
        if (input is null || input.DisplayName.Length == 0)
        {
            return;
        }

        var name = input.DisplayName;
        await RunAsync("Benennen", async () =>
        {
            var existing = await _service.FindPersonByNameAsync(name).ConfigureAwait(true);
            if (existing is { } existingId
                && !await _dialogs.ConfirmAsync("Name vergeben", $"Es gibt schon eine Person „{name}“. Die Gesichter zu ihr hinzufügen?", "Hinzufügen").ConfigureAwait(true))
            {
                return;
            }

            var count = await ConfirmWithCheckAsync(ids, existing, name, () => CreatePersonAsync(input)).ConfigureAwait(true);
            if (count > 0)
            {
                ShowInfo($"{N(count)} Gesichter als „{name}“ benannt. Ähnliche Gesichter werden jetzt vorgeschlagen.");
                CurrentGroup = null;
                Mode = PersonsPageMode.Overview;
            }
        }).ConfigureAwait(true);
    }

    /// <summary>„Zu Person hinzufügen…“: Auswahl mit Vorschlag (Gruppenzentrum), Ausreißer-Prüfung, bestätigen.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnGroup))]
    private async Task AddGroupToPersonAsync()
    {
        var ids = SelectedGroupFaces();
        var persons = await _service.GetPersonsAsync(default).ConfigureAwait(true);
        if (persons.Count == 0)
        {
            ShowError("Es gibt noch keine Personen — „Als neue Person benennen…“ verwenden.");
            return;
        }

        var target = await _dialogs.PickPersonAsync("Zu Person hinzufügen", persons, CurrentGroup?.SuggestedPersonId, null).ConfigureAwait(true);
        if (target is null)
        {
            return;
        }

        await RunAsync("Hinzufügen", async () =>
        {
            var count = await ConfirmWithCheckAsync(ids, target.Id, target.Name).ConfigureAwait(true);
            if (count > 0)
            {
                ShowInfo($"{N(count)} Gesichter zu „{target.Name}“ hinzugefügt.");
                CurrentGroup = null;
                Mode = PersonsPageMode.Overview;
            }
        }).ConfigureAwait(true);
    }

    /// <summary>„Kein Gesicht / ignorieren“ für die Auswahl.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnGroup))]
    private async Task IgnoreGroupSelectionAsync()
    {
        var ids = SelectedGroupFaces();
        var ok = await _dialogs.ConfirmAsync(
            "Ignorieren",
            $"{N(ids.Count)} Gesichter als „kein Gesicht / ignorieren“ markieren? Sie erscheinen nicht mehr in Gruppen und Vorschlägen.",
            "Ignorieren").ConfigureAwait(true);
        if (!ok)
        {
            return;
        }

        await RunAsync("Ignorieren", async () =>
        {
            await _service.IgnoreFacesAsync(ids).ConfigureAwait(true);
            var remaining = GroupMembers.Where(t => !ids.Contains(t.FaceId)).ToList();
            ShowInfo($"{N(ids.Count)} Gesichter ignoriert.");
            if (remaining.Count == 0 || CurrentGroup is null)
            {
                CurrentGroup = null;
                Mode = PersonsPageMode.Overview;
                return;
            }

            // Rest der Gruppe bleibt offen (z. B. nur die Fehlerkennungen ignoriert)
            var rest = CurrentGroup with { Members = CurrentGroup.Members.Where(m => !ids.Contains(m.FaceId)).ToList() };
            ShowGroup(rest);
        }, reload: false).ConfigureAwait(true);
    }

    private bool CanIgnoreWholeGroup() => !IsBusy && _ignore is not null && CurrentGroup is not null;

    /// <summary>„Ganze Gruppe ignorieren…“ (ADR-0040): alle Gesichter der Gruppe — auch abgewählte Ausreißer —, mit Rückfrage.</summary>
    [RelayCommand(CanExecute = nameof(CanIgnoreWholeGroup))]
    private Task IgnoreWholeGroupAsync() => CurrentGroup is { } group ? IgnoreGroupAsync(group) : Task.CompletedTask;

    /// <summary>Rechtsklick auf eine Gruppenkachel der Übersicht → „Ganze Gruppe ignorieren…“.</summary>
    [RelayCommand]
    private Task IgnoreGroupTileAsync(GroupTileViewModel? tile) => tile is null || IsBusy ? Task.CompletedTask : IgnoreGroupAsync(tile.Group);

    private async Task IgnoreGroupAsync(FaceGroupInfo group)
    {
        if (_ignore is null)
        {
            return;
        }

        var outliers = group.OutlierCount > 0
            ? $" — auch die {N(group.OutlierCount)} als Ausreißer markierten"
            : string.Empty;
        var ok = await _dialogs.ConfirmAsync(
            "Ganze Gruppe ignorieren",
            $"Alle {N(group.Count)} Gesichter dieser Gruppe{outliers} als „kein Gesicht / ignorieren“ markieren? Sie erscheinen nicht mehr in "
            + "Gruppen und Vorschlägen; zurückholen lassen sie sich unter „Ignorierte Gesichter“.",
            "Gruppe ignorieren").ConfigureAwait(true);
        if (!ok)
        {
            return;
        }

        await RunAsync("Gruppe ignorieren", async () =>
        {
            var count = await _ignore.IgnoreGroupAsync(group.GroupId).ConfigureAwait(true);
            if (count == 0)
            {
                ShowError("Die Gruppe hat sich inzwischen geändert — bitte die Übersicht aktualisieren und erneut öffnen.");
            }
            else
            {
                ShowInfo($"{N(count)} Gesichter ignoriert.");
            }

            CurrentGroup = null;
            Mode = PersonsPageMode.Overview;
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private void SelectAllGroup() => SetGroupSelection(_ => true);

    [RelayCommand]
    private void SelectNoneGroup() => SetGroupSelection(_ => false);

    /// <summary>Zurück zur Vorauswahl: alle außer den Ausreißern.</summary>
    [RelayCommand]
    private void SelectWithoutOutliers() => SetGroupSelection(t => !t.IsOutlier);

    private void SetGroupSelection(System.Func<FaceTileViewModel, bool> selected)
    {
        BatchSelection(() =>
        {
            foreach (var tile in GroupMembers)
            {
                tile.IsSelected = selected(tile);
            }
        });
    }
}
