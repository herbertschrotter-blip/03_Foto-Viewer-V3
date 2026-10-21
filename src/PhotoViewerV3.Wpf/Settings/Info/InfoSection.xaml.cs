using System.Windows.Controls;

namespace PhotoViewerV3.Settings.Info;

/// <summary>Einstellungen › Info (ADR-0042); DataContext = <see cref="InfoViewModel"/>, lädt bei jedem Öffnen des Reiters neu.</summary>
public partial class InfoSection : UserControl
{
    public InfoSection()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is InfoViewModel viewModel)
            {
                _ = viewModel.LoadCommand.ExecuteAsync(null);
            }
        };
    }
}
