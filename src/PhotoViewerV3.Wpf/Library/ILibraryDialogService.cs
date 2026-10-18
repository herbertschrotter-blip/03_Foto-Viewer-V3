using System.Threading.Tasks;

namespace PhotoViewerV3.Libraries;

/// <summary>Antwort eines Drei-Knopf-Dialogs: Primär, Sekundär oder Abbruch/Schließen.</summary>
public enum LibraryDialogChoice
{
    Cancel = 0,
    Primary = 1,
    Secondary = 2,
}

/// <summary>
/// Dialoge der Bibliotheksseite, die das Shell-<c>IDialogService</c> (Ja/Nein) nicht abdeckt: der
/// Mass-Missing-Dialog aus PLAN Kap. 12 braucht drei Knöpfe ([Erneut prüfen] [Als fehlend markieren] [Abbrechen]).
/// Liegt in Wpf/Library, bis das Shell-IDialogService eine Drei-Knopf-Variante bekommt.
/// </summary>
public interface ILibraryDialogService
{
    /// <summary>Dialog mit Primär-, Sekundär- und Schließen-Knopf; immer auf dem UI-Thread aufrufen.</summary>
    Task<LibraryDialogChoice> ShowChoiceAsync(
        string title,
        string message,
        string primaryText,
        string secondaryText,
        string cancelText = "Abbrechen",
        bool secondaryIsDestructive = false);
}
