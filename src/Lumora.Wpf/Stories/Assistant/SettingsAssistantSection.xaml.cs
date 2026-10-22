using System.Windows;
using System.Windows.Controls;

namespace Lumora.Stories.Assistant;

/// <summary>Einstellungen › Assistent (ADR-0063): lädt Werte und Zustand bei jedem Anzeigen neu.</summary>
public partial class SettingsAssistantSection : UserControl
{
    public SettingsAssistantSection()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is AssistantSettingsViewModel viewModel)
            {
                _ = viewModel.LoadAsync();
            }
        };
    }

    private void OnModelLostFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is AssistantSettingsViewModel viewModel)
        {
            _ = viewModel.ApplyModelCommand.ExecuteAsync(null);
        }
    }
}
