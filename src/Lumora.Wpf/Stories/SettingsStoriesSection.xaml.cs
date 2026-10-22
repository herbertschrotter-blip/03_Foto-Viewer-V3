using System.Windows.Controls;

namespace Lumora.Stories;

/// <summary>Einstellungen › Storys (ADR-0060): lädt die gespeicherten Werte bei jedem Anzeigen neu.</summary>
public partial class SettingsStoriesSection : UserControl
{
    public SettingsStoriesSection()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is StorySettingsViewModel viewModel)
            {
                _ = viewModel.LoadAsync();
            }
        };
    }
}
