using System.Windows.Controls;

namespace Lumora.Slideshow;

/// <summary>Einstellungen › Darstellung › Diashow (ADR-0059): lädt die gespeicherten Werte bei jedem Anzeigen neu.</summary>
public partial class SettingsSlideshowSection : UserControl
{
    public SettingsSlideshowSection()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is SlideshowSettingsViewModel viewModel)
            {
                _ = viewModel.LoadAsync();
            }
        };
    }
}
