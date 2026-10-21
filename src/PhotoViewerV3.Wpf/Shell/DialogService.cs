using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using PhotoViewerV3.Shell.Dialogs;

namespace PhotoViewerV3.Shell;

/// <summary>
/// Dialoge über das eigene <see cref="AppDialogWindow"/> (Mica, Theme-konform) — immer auf dem UI-Thread aufrufen.
/// Bewusst nicht die Wpf.Ui MessageBox: deren Größenlogik hängt bei umbrechendem Text mit Eingabefeld.
/// </summary>
public sealed class DialogService : IDialogService
{
    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false, string cancelText = "Abbrechen")
    {
        var outcome = AppDialogWindow.Show(GetOwner(), new DialogRequest(title, message)
        {
            PrimaryText = confirmText,
            PrimaryIsDestructive = isDestructive,
            CloseText = cancelText,
        });
        return Task.FromResult(outcome.Choice == DialogChoice.Primary);
    }

    public Task<DialogChoice> ChooseAsync(
        string title,
        string message,
        string primaryText,
        string secondaryText,
        string closeText = "Abbrechen",
        bool secondaryIsDestructive = false)
    {
        var outcome = AppDialogWindow.Show(GetOwner(), new DialogRequest(title, message)
        {
            PrimaryText = primaryText,
            SecondaryText = secondaryText,
            SecondaryIsDestructive = secondaryIsDestructive,
            CloseText = closeText,
        });
        return Task.FromResult(outcome.Choice);
    }

    public Task<string?> PromptTextAsync(string title, string message, string initialValue, string confirmText = "OK")
    {
        var outcome = AppDialogWindow.Show(GetOwner(), new DialogRequest(title, message)
        {
            PrimaryText = confirmText,
            InputInitialValue = initialValue,
        });
        return Task.FromResult(outcome.Choice == DialogChoice.Primary ? outcome.InputText : null);
    }

    public Task ShowErrorAsync(string title, string message)
    {
        _ = AppDialogWindow.Show(GetOwner(), new DialogRequest(title, message) { CloseText = "OK" });
        return Task.CompletedTask;
    }

    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false, AddToRecent = false }; // ADR-0049: nichts unter „Zuletzt verwendet“
        var owner = GetOwner();
        var ok = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return ok == true ? dialog.FolderName : null;
    }

    private static Window? GetOwner()
    {
        var window = Application.Current?.MainWindow;
        return window is { IsVisible: true } ? window : null;
    }
}
