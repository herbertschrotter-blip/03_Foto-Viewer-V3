using System.Threading.Tasks;

namespace PhotoViewerV3.Shell;

/// <summary>Bestätigungen, Eingaben und Fehlermeldungen für ViewModels (WPF-UI MessageBox, Ordnerauswahl).</summary>
public interface IDialogService
{
    /// <summary>Ja/Nein-Bestätigung; true, wenn der Benutzer die Aktion bestätigt hat.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false, string cancelText = "Abbrechen");

    /// <summary>Einzeilige Texteingabe; null bei Abbruch.</summary>
    Task<string?> PromptTextAsync(string title, string message, string initialValue, string confirmText = "OK");

    Task ShowErrorAsync(string title, string message);

    /// <summary>Ordnerauswahl (Microsoft.Win32.OpenFolderDialog); null bei Abbruch.</summary>
    string? PickFolder(string title);
}
