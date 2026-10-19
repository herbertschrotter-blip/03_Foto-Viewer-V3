using System.Threading.Tasks;

namespace PhotoViewerV3.Shell;

/// <summary>Antwort eines Drei-Knopf-Dialogs (<see cref="IDialogService.ChooseAsync"/>).</summary>
public enum DialogChoice
{
    /// <summary>Schließen-Knopf („Abbrechen“), Esc oder Titelleiste — keine der beiden Aktionen.</summary>
    None = 0,

    /// <summary>Primärknopf.</summary>
    Primary = 1,

    /// <summary>Sekundärknopf.</summary>
    Secondary = 2,
}

/// <summary>Bestätigungen, Auswahl, Eingaben und Fehlermeldungen für ViewModels (WPF-UI MessageBox, Ordnerauswahl).</summary>
public interface IDialogService
{
    /// <summary>Ja/Nein-Bestätigung; true, wenn der Benutzer die Aktion bestätigt hat.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false, string cancelText = "Abbrechen");

    /// <summary>
    /// Drei-Knopf-Dialog (Primär / Sekundär / Schließen), z. B. der Mass-Missing-Dialog aus PLAN Kap. 12
    /// ([Erneut prüfen] [Als fehlend markieren] [Abbrechen]). Schließen liefert <see cref="DialogChoice.None"/>.
    /// </summary>
    Task<DialogChoice> ChooseAsync(
        string title,
        string message,
        string primaryText,
        string secondaryText,
        string closeText = "Abbrechen",
        bool secondaryIsDestructive = false);

    /// <summary>Einzeilige Texteingabe; null bei Abbruch.</summary>
    Task<string?> PromptTextAsync(string title, string message, string initialValue, string confirmText = "OK");

    Task ShowErrorAsync(string title, string message);

    /// <summary>Ordnerauswahl (Microsoft.Win32.OpenFolderDialog); null bei Abbruch.</summary>
    string? PickFolder(string title);
}
