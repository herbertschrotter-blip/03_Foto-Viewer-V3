using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Services.Library;

namespace PhotoViewerV3.Gallery.ViewModels;

/// <summary>
/// Bibliotheks-Bereich der Galerie (ADR-0048): eine Bibliothek oder die Ansicht „Alle verknüpften“. Abfragen, Zähler,
/// Personen-/Beziehungsfilter und Nachrichten-Prüfungen nehmen ihre Bibliotheks-Ids von hier. Aktionen, die eine
/// Ziel-Bibliothek brauchen (Verschieben, „In die Bibliothek“, Entpacken), sind in „Alle verknüpften“ gesperrt —
/// die Auswahl kann Dateien mehrerer Bibliotheken enthalten, und ein Ziel wäre nicht eindeutig. Was je Datei wirkt
/// (Favorit, Bewertung, Tags, Kategorien, Personen, Datum, Papierkorb), bleibt erlaubt.
/// </summary>
public sealed partial class GalleryViewModel
{
    private readonly ILibraryScope _libraryScope;
    private LibraryScopeInfo _scope = LibraryScopeInfo.None;

    /// <summary>true in der Ansicht „Alle verknüpften“.</summary>
    public bool IsAllLinkedView => _scope.IsAllLinked;

    /// <summary>Auswahl-Aktionen mit Ziel-Bibliothek: nur in der Ansicht einer einzelnen Bibliothek.</summary>
    public bool CanActOnLibrarySelection => CanActOnSelection && !IsAllLinkedView;

    /// <summary>Hinweis für gesperrte Aktionen (Tooltip, Statuszeile).</summary>
    public string AllLinkedActionHint => "In der Ansicht „Alle verknüpften“ nicht möglich — bitte eine einzelne Bibliothek wählen.";

    /// <summary>Bibliotheken des Bereichs (nie leer, solange eine Bibliothek geladen ist).</summary>
    private IReadOnlyList<int> ScopeLibraryIds => _scope.HasLibrary ? _scope.LibraryIds : [_libraryId];

    /// <summary>Für <c>GalleryQuery.LibraryIds</c>: null außer in „Alle verknüpften“ (dann bleibt die SQL wie bisher).</summary>
    private IReadOnlyList<int>? ScopeLibraryIdsForQuery => _scope.IsAllLinked ? _scope.LibraryIds : null;

    /// <summary>Gehört die Bibliothek zum angezeigten Bereich (Nachrichten, Thumbnails)?</summary>
    private bool InScope(int libraryId) => libraryId == _libraryId || _scope.Contains(libraryId);

    private async Task LoadScopeAsync(int libraryId)
    {
        try
        {
            var scope = await _libraryScope.GetCurrentAsync();
            if (scope.LibraryId != libraryId)
            {
                scope = await _libraryScope.GetForLibraryAsync(libraryId);
            }

            _scope = scope;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Galerie: Bibliotheks-Bereich nicht lesbar — nur die aktive Bibliothek");
            _scope = LibraryScopeInfo.ForLibrary(libraryId, 0);
        }

        NotifyScopeChanged();
    }

    private void ClearScope()
    {
        _scope = LibraryScopeInfo.None;
        NotifyScopeChanged();
    }

    /// <summary>Anzeigename: in „Alle verknüpften“ der Gruppenname, sonst „Name · Pfad“.</summary>
    private string ScopeDisplayName(string single)
        => _scope.IsAllLinked ? $"Alle verknüpften ({_scope.GroupName}) · {_scope.LibraryIds.Count} Bibliotheken" : single;

    private void NotifyScopeChanged()
    {
        OnPropertyChanged(nameof(IsAllLinkedView));
        OnPropertyChanged(nameof(CanActOnLibrarySelection));
        OnPropertyChanged(nameof(CanExtractSelection));
        MoveSelectionCommand.NotifyCanExecuteChanged();
        ArchiveSelectionCommand.NotifyCanExecuteChanged();
        ExtractSelectionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>true (mit Hinweis), wenn die Aktion in „Alle verknüpften“ gesperrt ist.</summary>
    private async Task<bool> RejectInAllLinkedViewAsync(string action)
    {
        if (!IsAllLinkedView)
        {
            return false;
        }

        SetResultStatus($"{action}: {AllLinkedActionHint}");
        await _dialogs.ShowErrorsAsync(action, AllLinkedActionHint, []);
        return true;
    }
}
