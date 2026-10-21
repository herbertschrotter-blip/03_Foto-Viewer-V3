using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Lumora.Folders;

namespace Lumora.Libraries;

/// <summary>
/// Bibliotheksseite: „In die Bibliothek übernehmen…“ je Eingang (ADR-0034) — derselbe Ablauf wie im Ordnerbaum
/// (<see cref="FolderActions.AdoptAsync"/>); das Ergebnis erscheint im Ergebnis-Panel der Seite, danach wird die Liste
/// neu geladen (ein ausgetragener Eingang verschwindet).
/// </summary>
public sealed partial class LibraryViewModel
{
    private readonly FolderActions? _folderActions;

    /// <summary>Eine Übernahme läuft — die Wurzel-Aktionen der Seite sind so lange gesperrt.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AdoptInboxCommand), nameof(RemoveRootCommand), nameof(MakeArchiveRootCommand), nameof(RenameRootCommand))]
    private bool _isAdopting;

    [RelayCommand(CanExecute = nameof(CanModifyRoot))]
    private async Task AdoptInboxAsync(LibraryRootListItem? root)
    {
        if (root is null || !root.IsInbox || _folderActions is null)
        {
            return;
        }

        IsAdopting = true;
        try
        {
            ErrorMessage = null;
            var result = await _folderActions.AdoptAsync(root.Path).ConfigureAwait(true);
            if (result is null)
            {
                return;
            }

            ShowSummary(result.IsWarning ? "Übernommen — nicht alles" : "In die Bibliothek übernommen", result.Message, result.IsWarning);
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Übernehmen fehlgeschlagen: {ex.Message}";
            _logger.LogError(ex, "Übernahme von Eingang {Path} fehlgeschlagen", root.Path);
        }
        finally
        {
            IsAdopting = false;
        }
    }
}
