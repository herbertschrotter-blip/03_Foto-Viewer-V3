using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using UiMessageBox = Wpf.Ui.Controls.MessageBox;
using UiMessageBoxButton = Wpf.Ui.Controls.MessageBoxButton;
using UiMessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;
using UiTextBox = Wpf.Ui.Controls.TextBox;
using WpfTextBlock = System.Windows.Controls.TextBlock;

namespace PhotoViewerV3.Shell;

/// <summary>Dialoge auf Basis der WPF-UI MessageBox (Mica, Theme-konform) — immer auf dem UI-Thread aufrufen.</summary>
public sealed class DialogService : IDialogService
{
    private const double MinDialogWidth = 420;
    private const double MaxDialogWidth = 640;

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false, string cancelText = "Abbrechen")
    {
        var box = new UiMessageBox();
        Configure(box, title, BuildMessage(message), cancelText);
        box.PrimaryButtonText = confirmText;
        box.PrimaryButtonAppearance = isDestructive ? ControlAppearance.Danger : ControlAppearance.Primary;

        var result = await box.ShowDialogAsync().ConfigureAwait(true);
        return result == UiMessageBoxResult.Primary;
    }

    public async Task<DialogChoice> ChooseAsync(
        string title,
        string message,
        string primaryText,
        string secondaryText,
        string closeText = "Abbrechen",
        bool secondaryIsDestructive = false)
    {
        var box = new UiMessageBox();
        Configure(box, title, BuildMessage(message), closeText);
        box.PrimaryButtonText = primaryText;
        box.PrimaryButtonAppearance = ControlAppearance.Primary;
        box.SecondaryButtonText = secondaryText;
        box.SecondaryButtonAppearance = secondaryIsDestructive ? ControlAppearance.Danger : ControlAppearance.Secondary;

        var result = await box.ShowDialogAsync().ConfigureAwait(true);
        return result switch
        {
            UiMessageBoxResult.Primary => DialogChoice.Primary,
            UiMessageBoxResult.Secondary => DialogChoice.Secondary,
            _ => DialogChoice.None,
        };
    }

    public async Task<string?> PromptTextAsync(string title, string message, string initialValue, string confirmText = "OK")
    {
        var input = new UiTextBox { Text = initialValue, Margin = new Thickness(0, 12, 0, 0) };
        var panel = new StackPanel();
        panel.Children.Add(BuildMessage(message));
        panel.Children.Add(input);

        var box = new PromptMessageBox();
        Configure(box, title, panel, "Abbrechen");
        box.PrimaryButtonText = confirmText;

        input.Loaded += (_, _) =>
        {
            _ = input.Focus();
            input.SelectAll();
        };
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                box.Submit();
            }
        };

        var result = await box.ShowDialogAsync().ConfigureAwait(true);
        return result == UiMessageBoxResult.Primary ? input.Text : null;
    }

    public async Task ShowErrorAsync(string title, string message)
    {
        var box = new UiMessageBox();
        Configure(box, title, BuildMessage(message), "OK");
        _ = await box.ShowDialogAsync().ConfigureAwait(true);
    }

    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        var owner = GetOwner();
        var ok = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return ok == true ? dialog.FolderName : null;
    }

    private static void Configure(UiMessageBox box, string title, object content, string closeText)
    {
        box.Title = title;
        box.Content = content;
        box.CloseButtonText = closeText;
        box.MinWidth = MinDialogWidth;
        box.MaxWidth = MaxDialogWidth;

        if (GetOwner() is { } owner)
        {
            box.Owner = owner;
            box.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
    }

    private static Window? GetOwner()
    {
        var window = Application.Current?.MainWindow;
        return window is { IsVisible: true } ? window : null;
    }

    private static WpfTextBlock BuildMessage(string message) => new()
    {
        Text = message,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = MaxDialogWidth - 80,
    };

    /// <summary>Erlaubt das Bestätigen per Enter aus dem Eingabefeld (OnButtonClick ist protected).</summary>
    private sealed class PromptMessageBox : UiMessageBox
    {
        public void Submit() => OnButtonClick(UiMessageBoxButton.Primary);
    }
}
