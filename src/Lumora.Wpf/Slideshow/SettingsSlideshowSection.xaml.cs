using System.Windows.Controls;

namespace Lumora.Slideshow;

/// <summary>Einstellungen › Storys › Diashow (ADR-0059, Reiter seit ADR-0060): lädt die gespeicherten Werte bei jedem Anzeigen neu.</summary>
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
