using System.Linq;
using System.Windows.Controls;
using Lumora.Settings.Sections;

namespace Lumora.Shell.HiddenLibraries;

/// <summary>Einstellungs-Reiter der versteckten Bibliotheken (ADR-0049).</summary>
public static class HiddenLibrariesUi
{
    /// <summary>Fügt den Reiter „Versteckte Bibliotheken“ vor „Info“ ein — nur mit Startschalter <c>_hidden</c>.</summary>
    public static void AddSettingsTab(TabControl tabs, HiddenLibrariesViewModel? viewModel)
    {
        if (viewModel is not { IsHiddenModeEnabled: true })
        {
            return;
        }

        var tab = new TabItem
        {
            Header = "Versteckte Bibliotheken",
            Tag = NavigationTags.SettingsHiddenLibraries,
            Content = new SettingsHiddenLibrariesSection { DataContext = viewModel },
        };
        var info = tabs.Items.OfType<TabItem>().FirstOrDefault(t => Equals(t.Tag, NavigationTags.SettingsInfo));
        var index = info is null ? tabs.Items.Count : tabs.Items.IndexOf(info);
        tabs.Items.Insert(index, tab);
        tab.Loaded += (_, _) => _ = viewModel.RefreshAsync();
    }
}
