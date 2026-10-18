using System.Threading.Tasks;
using System.Windows;
using Wpf.Ui.Controls;
using UiMessageBox = Wpf.Ui.Controls.MessageBox;
using UiMessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;
using WpfTextBlock = System.Windows.Controls.TextBlock;

namespace PhotoViewerV3.Libraries;

/// <summary>Drei-Knopf-Dialog auf Basis der WPF-UI MessageBox (Primär/Sekundär/Schließen), Theme-konform wie Shell/DialogService.</summary>
public sealed class LibraryDialogService : ILibraryDialogService
{
    private const double MinDialogWidth = 460;
    private const double MaxDialogWidth = 640;

    public async Task<LibraryDialogChoice> ShowChoiceAsync(
        string title,
        string message,
        string primaryText,
        string secondaryText,
        string cancelText = "Abbrechen",
        bool secondaryIsDestructive = false)
    {
        var box = new UiMessageBox
        {
            Title = title,
            Content = new WpfTextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = MaxDialogWidth - 80,
            },
            PrimaryButtonText = primaryText,
            PrimaryButtonAppearance = ControlAppearance.Primary,
            SecondaryButtonText = secondaryText,
            SecondaryButtonAppearance = secondaryIsDestructive ? ControlAppearance.Danger : ControlAppearance.Secondary,
            CloseButtonText = cancelText,
            MinWidth = MinDialogWidth,
            MaxWidth = MaxDialogWidth,
        };

        if (Application.Current?.MainWindow is { IsVisible: true } owner)
        {
            box.Owner = owner;
            box.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        var result = await box.ShowDialogAsync().ConfigureAwait(true);
        return result switch
        {
            UiMessageBoxResult.Primary => LibraryDialogChoice.Primary,
            UiMessageBoxResult.Secondary => LibraryDialogChoice.Secondary,
            _ => LibraryDialogChoice.Cancel,
        };
    }
}
