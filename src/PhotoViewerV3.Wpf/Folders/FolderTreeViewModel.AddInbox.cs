using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace PhotoViewerV3.Folders;

/// <summary>
/// „Eingang hinzufügen…“ im Kopf des Ordnerbaums (ADR-0042, Mockup 06): derselbe Ablauf wie in Einstellungen ›
/// Bibliotheken &amp; Eingänge, für die aktive Bibliothek. Der Ordnerbaum lädt danach über LibraryRootsChangedMessage bzw.
/// ScanCompletedMessage selbst neu.
/// </summary>
public sealed partial class FolderTreeViewModel
{
    private readonly Libraries.IInboxActions? _inboxActions;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddInboxCommand))]
    private bool _isAddingInbox;

    /// <summary>Knopf nur, wenn die Bibliotheksverwaltung angebunden ist (in Tests ohne sie ausgeblendet).</summary>
    public bool CanShowAddInbox => _inboxActions is not null;

    // Ein laufender Scan sperrt den Knopf nicht (kein Änderungsereignis dafür) — der Ablauf meldet ihn per Dialog
    private bool CanAddInbox() => _inboxActions is not null && !IsAddingInbox;

    [RelayCommand(CanExecute = nameof(CanAddInbox))]
    private async Task AddInboxAsync()
    {
        if (_inboxActions is null)
        {
            return;
        }

        IsAddingInbox = true;
        try
        {
            var added = await _inboxActions.AddInboxToActiveLibraryAsync().ConfigureAwait(true);
            _logger.LogInformation("Ordner-Seite: Eingang hinzufügen — {Result}", added ? "hinzugefügt" : "nicht hinzugefügt");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Eingang konnte nicht hinzugefügt werden: {ex.Message}";
            _logger.LogError(ex, "Ordner-Seite: Eingang hinzufügen fehlgeschlagen");
        }
        finally
        {
            IsAddingInbox = false;
        }
    }
}
