using System.Windows.Controls;

namespace Lumora.Places.TrackSettings;

/// <summary>Einstellungen › Orte (ADR-0062): lädt die gespeicherten Werte und den Stand der Spuren bei jedem Anzeigen neu.</summary>
public partial class SettingsPlacesSection : UserControl
{
    public SettingsPlacesSection()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is PlacesSettingsViewModel viewModel)
            {
                _ = viewModel.LoadAsync();
            }
        };
    }
}
