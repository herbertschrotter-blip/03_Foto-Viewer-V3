using System.Windows.Controls;

namespace PhotoViewerV3.Settings;

/// <summary>Seite „Einstellungen“; lädt Pfade, Cache-Größe und Status bei jedem Anzeigen neu.</summary>
public partial class SettingsPage : UserControl
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => _ = viewModel.LoadCommand.ExecuteAsync(null);
    }
}
