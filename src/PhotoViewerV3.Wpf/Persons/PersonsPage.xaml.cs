using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PhotoViewerV3.Gallery.Controls;
using PhotoViewerV3.Persons.ViewModels;

namespace PhotoViewerV3.Persons;

/// <summary>
/// Seite „Personen“ (ADR-0037). Code-Behind nur als UI-Klebstoff: Klicks auf Kacheln (öffnen bzw. an-/abwählen,
/// Doppelklick → Lightbox), sichtbarer Bereich der virtualisierten Listen ans ViewModel, nach oben scrollen.
/// </summary>
public partial class PersonsPage : UserControl
{
    private readonly PersonsViewModel _viewModel;

    public PersonsPage(PersonsViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => _ = viewModel.ActivateAsync();
        Unloaded += (_, _) => viewModel.Deactivate();
        viewModel.ScrollToTopRequested += OnScrollToTopRequested;
    }

    public PersonsViewModel ViewModel => _viewModel;

    private void OnVisibleRangeChanged(object sender, VisibleRangeChangedEventArgs e)
    {
        PersonsListKind? list = (sender as FrameworkElement)?.Name switch
        {
            nameof(PersonsList) => ViewModels.PersonsListKind.Persons,
            nameof(GroupsList) => ViewModels.PersonsListKind.Groups,
            nameof(SuggestionsList) => ViewModels.PersonsListKind.Suggestions,
            nameof(ConfirmedList) => ViewModels.PersonsListKind.Confirmed,
            nameof(GroupMembersList) => ViewModels.PersonsListKind.GroupMembers,
            nameof(IgnoredList) => ViewModels.PersonsListKind.Ignored,
            _ => null,
        };
        if (list is { } key)
        {
            // Synchron aus dem Measure-Durchlauf gemeldet — das ViewModel fordert nur Ausschnitte an (kein Layout).
            _viewModel.ReportVisibleRange(key, e.FirstIndex, e.LastIndex);
        }
    }

    private void OnOpenTileClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: var data })
        {
            Open(data);
        }
    }

    private void OnOpenTileKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && sender is ListBoxItem { DataContext: var data })
        {
            e.Handled = true;
            Open(data);
        }
    }

    private void Open(object? data)
    {
        switch (data)
        {
            case PersonTileViewModel person:
                _viewModel.OpenPersonCommand.Execute(person);
                break;
            case GroupTileViewModel group:
                _viewModel.OpenGroupCommand.Execute(group);
                break;
        }
    }

    /// <summary>Klick wählt an/ab, Doppelklick öffnet das Foto (die Knöpfe ✓/✗ der Kachel behalten ihren Klick).</summary>
    private void OnFaceTileMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: FaceTileViewModel tile } item || IsInsideButton(e.OriginalSource as DependencyObject, item))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            tile.IsSelected = !tile.IsSelected; // erster Klick hat schon umgeschaltet
            _viewModel.OpenFaceCommand.Execute(tile);
        }
        else
        {
            _viewModel.ToggleTileCommand.Execute(tile);
        }

        item.Focus();
        e.Handled = true;
    }

    /// <summary>Kontextmenü einer Gruppenkachel: „Ganze Gruppe ignorieren…“ (ADR-0040).</summary>
    private void OnIgnoreGroupMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: GroupTileViewModel tile } } })
        {
            _viewModel.IgnoreGroupTileCommand.Execute(tile);
        }
    }

    private void OnFaceTileKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: FaceTileViewModel tile })
        {
            return;
        }

        if (e.Key == Key.Space)
        {
            e.Handled = true;
            _viewModel.ToggleTileCommand.Execute(tile);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _viewModel.OpenFaceCommand.Execute(tile);
        }
    }

    private static bool IsInsideButton(DependencyObject? source, DependencyObject stop)
    {
        for (var node = source; node is not null && !ReferenceEquals(node, stop); node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is ButtonBase)
            {
                return true;
            }
        }

        return false;
    }

    private void OnScrollToTopRequested(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            foreach (var list in new ListBox[] { SuggestionsList, ConfirmedList, GroupMembersList, IgnoredList })
            {
                if (FindPanel(list) is { } panel)
                {
                    panel.SetVerticalOffset(0);
                }
            }
        });

    private static VirtualizingTilePanel? FindPanel(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is VirtualizingTilePanel panel)
            {
                return panel;
            }

            if (FindPanel(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
