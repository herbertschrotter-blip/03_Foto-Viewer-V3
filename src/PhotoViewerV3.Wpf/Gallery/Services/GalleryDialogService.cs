using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace PhotoViewerV3.Gallery.Services;

/// <summary>Wpf.Ui-MessageBox-Implementierung von <see cref="IGalleryDialogService"/>.</summary>
public sealed class GalleryDialogService : IGalleryDialogService
{
    public async Task<bool> ConfirmAsync(string title, string message, string primaryButtonText, bool isDestructive = false)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
            },
            PrimaryButtonText = primaryButtonText,
            PrimaryButtonAppearance = isDestructive ? ControlAppearance.Danger : ControlAppearance.Primary,
            CloseButtonText = "Abbrechen",
            IsSecondaryButtonEnabled = false,
            Owner = ResolveOwner(),
        };

        var result = await box.ShowDialogAsync().ConfigureAwait(true);
        return result == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }

    public async Task ShowErrorsAsync(string title, string message, IReadOnlyList<string> details)
    {
        var panel = new StackPanel { MaxWidth = 520 };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        if (details.Count > 0)
        {
            panel.Children.Add(new ScrollViewer
            {
                MaxHeight = 260,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = string.Join("\n", details),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                },
            });
        }

        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = panel,
            CloseButtonText = "Schließen",
            IsPrimaryButtonEnabled = false,
            IsSecondaryButtonEnabled = false,
            Owner = ResolveOwner(),
        };

        await box.ShowDialogAsync().ConfigureAwait(true);
    }

    private static Window? ResolveOwner()
    {
        var app = Application.Current;
        if (app is null)
        {
            return null;
        }

        return app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app.MainWindow;
    }
}
