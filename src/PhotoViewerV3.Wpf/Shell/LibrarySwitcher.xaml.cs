using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace PhotoViewerV3.Shell;

/// <summary>
/// Ansicht des Bibliotheks-Umschalters (PaneHeader der NavigationView). Das Menü wird bei jedem Klick aus dem frisch
/// gelesenen <see cref="LibrarySwitcherViewModel"/> gebaut (ein ContextMenu liegt außerhalb des visuellen Baums).
/// </summary>
public partial class LibrarySwitcher : UserControl
{
    public LibrarySwitcher()
    {
        InitializeComponent();
    }

    private LibrarySwitcherViewModel? ViewModel => DataContext as LibrarySwitcherViewModel;

    private async void OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        await vm.RefreshAsync();
        if (!vm.HasChoices)
        {
            vm.ManageCommand.Execute(null);
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = SwitchButton,
            Placement = PlacementMode.Bottom,
            MinWidth = SwitchButton.ActualWidth,
        };
        menu.Items.Add(new MenuItem { Header = "Bibliothek wechseln", IsEnabled = false });
        foreach (var item in vm.Libraries)
        {
            menu.Items.Add(new MenuItem
            {
                Header = item.Name,
                InputGestureText = item.DetailText,
                IsChecked = item.IsActive,
                Command = vm.SwitchToCommand,
                CommandParameter = item,
            });
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = "Bibliotheken verwalten…",
            Icon = new SymbolIcon { Symbol = SymbolRegular.Settings24 },
            Command = vm.ManageCommand,
        });
        menu.IsOpen = true;
    }
}
