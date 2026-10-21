using System.Windows.Controls;
using PhotoViewerV3.Places.Styles;

namespace PhotoViewerV3.Settings.Sections;

/// <summary>
/// Einstellungen › Darstellung › Karte (ADR-0055): Kartenstil und die Karten „Kartendienst“ (<see cref="SettingsMapServiceCard"/>).
/// Lädt beim Anzeigen Stil, Schlüssel, Angaben und Lumoras Zählung neu.
/// </summary>
public partial class SettingsMapSection : UserControl
{
    public SettingsMapSection()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is MapStyleSettingsViewModel viewModel)
            {
                _ = viewModel.LoadAsync();
            }
        };
    }
}
