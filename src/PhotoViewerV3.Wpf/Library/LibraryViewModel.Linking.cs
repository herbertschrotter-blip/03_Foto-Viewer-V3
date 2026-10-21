using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Libraries.Linking;
using PhotoViewerV3.Services.Library;

namespace PhotoViewerV3.Libraries;

/// <summary>
/// „Bibliotheken verknüpfen“ als eigene Einstellung je Bibliothek (ADR-0048): „Verknüpfen mit…“ (Dialog mit Partner,
/// Zusammenfassung und Rückfrage für gleichnamige Personen), „Verknüpfung lösen“ (Bestätigung mit Zusammenfassung) und
/// „Gruppe umbenennen…“. In einem versteckten Katalog ist alles ausgeblendet (<see cref="ILibraryLinkService.IsLinkingAllowed"/>).
/// Ohne angebundene Dienste (Tests) bleibt die Einstellung unsichtbar.
/// </summary>
public sealed partial class LibraryViewModel
{
    private readonly ILibraryLinkService? _linking;
    private readonly ILibraryScope? _libraryScope;
    private readonly ILibraryLinkDialogService? _linkDialogs;

    /// <summary>Verknüpfen ist angebunden und in diesem Katalog erlaubt.</summary>
    public bool IsLinkingAvailable => _linking is { IsLinkingAllowed: true } && _libraryScope is not null;

    private async Task<IReadOnlyList<LibraryGroupInfo>> LoadGroupsAsync()
    {
        if (_libraryScope is null)
        {
            return [];
        }

        try
        {
            return await _libraryScope.GetGroupsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Verknüpfungen konnten nicht gelesen werden");
            return [];
        }
    }

    private void ApplyGroup(LibraryListItem item, IReadOnlyList<LibraryInfo> all, IReadOnlyList<LibraryGroupInfo> groups)
    {
        var group = groups.FirstOrDefault(g => g.LibraryIds.Contains(item.Id));
        var names = all.ToDictionary(l => l.Id, l => l.Name);
        var outside = all.Any(l => l.Id != item.Id && (group is null || !group.LibraryIds.Contains(l.Id)));
        item.ApplyGroup(group, names, IsLinkingAvailable, outside);
    }

    private bool CanLinkItem(LibraryListItem? item) => !IsScanning && item is { CanLink: true } && IsLinkingAvailable;

    private bool CanUnlinkItem(LibraryListItem? item) => !IsScanning && item is { IsLinked: true } && IsLinkingAvailable;

    /// <summary>„Verknüpfen mit…“: Partner wählen, Zusammenfassung prüfen, gleichnamige Personen entscheiden, verknüpfen.</summary>
    [RelayCommand(CanExecute = nameof(CanLinkItem))]
    private async Task LinkAsync(LibraryListItem? item)
    {
        if (item is null || _linking is null || _linkDialogs is null)
        {
            return;
        }

        var candidates = Libraries
            .Where(l => l.Id != item.Id && (item.GroupId == 0 || l.GroupId != item.GroupId))
            .Select(l => new LibraryLinkCandidate(l.Id, l.Name))
            .ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        var choice = await _linkDialogs.ShowLinkAsync(new LibraryLinkDialogRequest(item.Id, item.Name, candidates)).ConfigureAwait(true);
        if (choice is null)
        {
            return;
        }

        try
        {
            ErrorMessage = null;
            var result = await _linking.LinkAsync(item.Id, choice.OtherLibraryId, choice.KeepApartSourcePersonIds).ConfigureAwait(true);
            _logger.LogInformation("Einstellungen: Bibliothek {Library} mit {Other} verknüpft (Gruppe {Group})", item.Id, choice.OtherLibraryId, result.GroupId);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Verknüpfen fehlgeschlagen: {ex.Message}";
            _logger.LogError(ex, "Verknüpfen von {Library} mit {Other} fehlgeschlagen", item.Id, choice.OtherLibraryId);
        }

        await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>„Verknüpfung lösen“: Bestätigung mit Zusammenfassung, dann eigene Gruppe mit Kopien aller Begriffe.</summary>
    [RelayCommand(CanExecute = nameof(CanUnlinkItem))]
    private async Task UnlinkAsync(LibraryListItem? item)
    {
        if (item is null || _linking is null)
        {
            return;
        }

        try
        {
            ErrorMessage = null;
            var preview = await _linking.PreviewUnlinkAsync(item.Id).ConfigureAwait(true);
            var others = string.Join(", ", preview.RemainingLibraryNames.Select(n => $"„{n}“"));
            var message =
                $"„{preview.LibraryName}“ wird von {others} gelöst und wieder einzeln angezeigt.\n\n"
                + $"Damit nichts verloren geht, bekommt „{preview.LibraryName}“ eine eigene Kopie aller gemeinsamen Begriffe: "
                + $"{preview.Tags} Tags, {preview.Categories} Kategorien, {preview.Relations} Beziehungen und {preview.Persons} Personen. "
                + "Gesichter, Zuordnungen, Tags und Kategorien der Dateien bleiben bei ihrer Bibliothek.";
            if (!await _dialogs.ConfirmAsync("Verknüpfung lösen", message, "Lösen").ConfigureAwait(true))
            {
                return;
            }

            var result = await _linking.UnlinkAsync(item.Id).ConfigureAwait(true);
            _logger.LogInformation("Einstellungen: Verknüpfung von {Library} gelöst (neue Gruppe {Group})", item.Id, result.NewGroupId);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Verknüpfung lösen fehlgeschlagen: {ex.Message}";
            _logger.LogError(ex, "Verknüpfung von {Library} lösen fehlgeschlagen", item.Id);
        }

        await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>„Gruppe umbenennen…“ (Anzeige „Alle verknüpften (Name)“).</summary>
    [RelayCommand(CanExecute = nameof(CanUnlinkItem))]
    private async Task RenameGroupAsync(LibraryListItem? item)
    {
        if (item is null || _libraryScope is null || item.GroupId == 0)
        {
            return;
        }

        var name = await _dialogs.PromptTextAsync("Gruppe umbenennen", "Name der verknüpften Bibliotheken („Alle verknüpften (Name)“):", item.GroupName, "Umbenennen").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == item.GroupName)
        {
            return;
        }

        try
        {
            ErrorMessage = null;
            await _libraryScope.RenameGroupAsync(item.GroupId, name.Trim()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Gruppe konnte nicht umbenannt werden: {ex.Message}";
            _logger.LogError(ex, "Gruppe {Group} umbenennen fehlgeschlagen", item.GroupId);
        }

        await LoadAsync().ConfigureAwait(true);
    }
}
