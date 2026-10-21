using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Services.Faces.Assignment;
using PhotoViewerV3.Shell;

namespace PhotoViewerV3.Settings.PersonRelations;

/// <summary>Zeile der Beziehungsliste in den Einstellungen.</summary>
public sealed record PersonRelationRow(int Id, string Name, int PersonCount)
{
    public string CountText => PersonCount == 1 ? "1 Person" : $"{PersonCount:N0} Personen";

    public override string ToString() => Name;
}

/// <summary>
/// Einstellungen › Gesichter › „Personen &amp; Beziehungen“ (0.2.47, ADR-0045): die selbst eingetippten Beziehungen
/// umbenennen (wirkt bei allen Personen, Suche und Filter), zusammenführen (z. B. Tippfehler „Schwster“ → „Schwester“) und
/// löschen (Personen behalten eine leere Beziehung). Umbenennen auf einen vorhandenen Namen bietet das Zusammenführen an.
/// Dialoge über <see cref="IDialogService"/>; neu geladen bei jeder <see cref="PersonsChangedMessage"/>.
/// </summary>
public sealed partial class PersonRelationsSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IPersonDetailsService _details;
    private readonly IDialogService _dialogs;
    private readonly ILogger<PersonRelationsSettingsViewModel> _logger;
    private readonly IMessenger _messenger;

    public PersonRelationsSettingsViewModel(
        IPersonDetailsService details,
        IDialogService dialogs,
        ILogger<PersonRelationsSettingsViewModel> logger,
        IMessenger? messenger = null)
    {
        _details = details;
        _dialogs = dialogs;
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _messenger.Register<PersonRelationsSettingsViewModel, PersonsChangedMessage>(this, static (r, m) => UiThread.Post(() => _ = r.LoadAsync()));
    }

    public ObservableCollection<PersonRelationRow> Relations { get; } = new();

    public bool HasRelations => Relations.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand), nameof(MergeCommand), nameof(DeleteCommand))]
    [NotifyPropertyChangedFor(nameof(MergeTargets))]
    private PersonRelationRow? _selectedRelation;

    /// <summary>Mögliche Ziele für „Zusammenführen mit“ (alle außer der gewählten).</summary>
    public PersonRelationRow[] MergeTargets => Relations.Where(r => r.Id != SelectedRelation?.Id).ToArray();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MergeCommand))]
    private PersonRelationRow? _mergeTarget;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand), nameof(MergeCommand), nameof(DeleteCommand))]
    private bool _isBusy;

    [ObservableProperty] private string? _message;

    [ObservableProperty] private bool _messageIsError;

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            var rows = await _details.GetRelationsAsync().ConfigureAwait(true);
            var selectedId = SelectedRelation?.Id;
            var targetId = MergeTarget?.Id;
            Relations.Clear();
            foreach (var row in rows)
            {
                Relations.Add(new PersonRelationRow(row.Id, row.Name, row.PersonCount));
            }

            SelectedRelation = Relations.FirstOrDefault(r => r.Id == selectedId);
            OnPropertyChanged(nameof(MergeTargets));
            MergeTarget = MergeTargets.FirstOrDefault(r => r.Id == targetId);
            OnPropertyChanged(nameof(HasRelations));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Beziehungen konnten nicht geladen werden");
            Show("Beziehungen konnten nicht geladen werden: " + ex.Message, error: true);
        }
    }

    private bool CanActOnSelection() => !IsBusy && SelectedRelation is not null;

    private bool CanMerge() => CanActOnSelection() && MergeTarget is not null && MergeTarget.Id != SelectedRelation!.Id;

    /// <summary>„Umbenennen…“; ein vorhandener Name (ohne Groß-/Kleinschreibung) führt nach Rückfrage zusammen.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private async Task RenameAsync()
    {
        if (SelectedRelation is not { } relation)
        {
            return;
        }

        var name = await _dialogs.PromptTextAsync(
            "Beziehung umbenennen",
            $"Neuer Name für „{relation.Name}“ — gilt für alle {relation.CountText}:",
            relation.Name,
            "Umbenennen").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == relation.Name)
        {
            return;
        }

        await ApplyRenameAsync(relation, name).ConfigureAwait(true);
    }

    /// <summary>Umbenennen ohne Eingabedialog (nach der Eingabe; Rauchtest). Vergebener Name → Rückfrage „zusammenführen“.</summary>
    public async Task ApplyRenameAsync(PersonRelationRow relation, string name)
    {
        var trimmed = name.Trim();
        await RunAsync(async () =>
        {
            try
            {
                await _details.RenameRelationAsync(relation.Id, trimmed).ConfigureAwait(true);
                Show($"„{relation.Name}“ heißt jetzt „{trimmed}“.");
            }
            catch (RelationNameTakenException taken)
            {
                var target = Relations.FirstOrDefault(r => r.Id == taken.ExistingRelationId);
                var ok = await _dialogs.ConfirmAsync(
                    "Beziehung zusammenführen",
                    $"Es gibt schon die Beziehung „{target?.Name ?? trimmed}“. „{relation.Name}“ mit ihr zusammenführen? Die {relation.CountText} bekommen „{target?.Name ?? trimmed}“.",
                    "Zusammenführen").ConfigureAwait(true);
                if (ok)
                {
                    await _details.MergeRelationsAsync(relation.Id, taken.ExistingRelationId).ConfigureAwait(true);
                    Show($"„{relation.Name}“ mit „{target?.Name ?? trimmed}“ zusammengeführt.");
                }
            }
        }).ConfigureAwait(true);
    }

    /// <summary>„Zusammenführen“: alle Personen der gewählten Beziehung bekommen das Ziel; die gewählte verschwindet.</summary>
    [RelayCommand(CanExecute = nameof(CanMerge))]
    private async Task MergeAsync()
    {
        if (SelectedRelation is not { } source || MergeTarget is not { } target)
        {
            return;
        }

        var ok = await _dialogs.ConfirmAsync(
            "Beziehungen zusammenführen",
            $"Die {source.CountText} mit „{source.Name}“ bekommen „{target.Name}“; „{source.Name}“ wird gelöscht.",
            "Zusammenführen").ConfigureAwait(true);
        if (!ok)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _details.MergeRelationsAsync(source.Id, target.Id).ConfigureAwait(true);
            Show($"„{source.Name}“ mit „{target.Name}“ zusammengeführt.");
        }).ConfigureAwait(true);
    }

    /// <summary>„Löschen…“: die Personen behalten eine leere Beziehung.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private async Task DeleteAsync()
    {
        if (SelectedRelation is not { } relation)
        {
            return;
        }

        var ok = await _dialogs.ConfirmAsync(
            "Beziehung löschen",
            relation.PersonCount == 0
                ? $"„{relation.Name}“ löschen? Keine Person trägt sie."
                : $"„{relation.Name}“ löschen? Die {relation.CountText} behalten ihre Gesichter und Namen, nur die Beziehung wird leer.",
            "Löschen",
            isDestructive: true).ConfigureAwait(true);
        if (!ok)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _details.DeleteRelationAsync(relation.Id).ConfigureAwait(true);
            Show($"„{relation.Name}“ gelöscht.");
        }).ConfigureAwait(true);
    }

    private async Task RunAsync(Func<Task> action)
    {
        IsBusy = true;
        Message = null;
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Beziehung: Aktion fehlgeschlagen");
            Show(ex.Message, error: true);
        }
        finally
        {
            IsBusy = false;
        }

        await LoadAsync().ConfigureAwait(true);
    }

    private void Show(string message, bool error = false)
    {
        Message = message;
        MessageIsError = error;
    }

    public void Dispose() => _messenger.UnregisterAll(this);
}
