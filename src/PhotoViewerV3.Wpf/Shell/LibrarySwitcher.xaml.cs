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
        if (vm.Hidden is { IsNormalSession: true } hidden)
        {
            await hidden.RefreshIfStaleAsync(); // versteckte Bibliotheken neu suchen (ADR-0049)
        }

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

        AddHiddenLibraryItems(menu, vm.Hidden); // nur mit „_hidden“ (ADR-0049)
        // Verknüpfte Bibliotheken gemeinsam ansehen (ADR-0048) — nur für Gruppen mit mindestens zwei Bibliotheken
        if (vm.LinkedGroups.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Gemeinsam ansehen", IsEnabled = false });
            foreach (var group in vm.LinkedGroups)
            {
                menu.Items.Add(new MenuItem
                {
                    Header = group.Header,
                    InputGestureText = group.DetailText,
                    IsChecked = group.IsActive,
                    Icon = new SymbolIcon { Symbol = SymbolRegular.Link24 },
                    Command = vm.ShowAllLinkedCommand,
                    CommandParameter = group,
                });
            }
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
