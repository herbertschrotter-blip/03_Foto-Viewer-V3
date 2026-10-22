using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Lumora.Places.ViewModels;

namespace Lumora.Places.Suggestions;

/// <summary>Seitenleiste mit Vorschlägen (ADR-0062). Code-Behind nur Klebstoff: Zeile anklicken wählt aus, Doppelklick öffnet.</summary>
public partial class SuggestionPanel : UserControl
{
    public SuggestionPanel()
    {
        InitializeComponent();
    }

    /// <summary>Die Liste (Tests, Rauchtest).</summary>
    public ListBox RowList => Rows;

    private PlacesViewModel? ViewModel => DataContext as PlacesViewModel;

    private void OnRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: SuggestionItemRow row } item || IsInsideButton(e.OriginalSource as DependencyObject, item))
        {
            return;
        }

        item.Focus();
        if (e.ClickCount == 2)
        {
            _ = ViewModel?.OpenSuggestionLightboxAsync(row);
        }
        else
        {
            ViewModel?.SelectRow(row);
        }

        e.Handled = true;
    }

    private void OnRowKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: SuggestionItemRow row })
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                ViewModel?.SelectRow(row);
                e.Handled = true;
                break;
            case Key.Enter:
                _ = ViewModel?.OpenSuggestionLightboxAsync(row);
                e.Handled = true;
                break;
        }
    }

    private static bool IsInsideButton(DependencyObject? source, DependencyObject stop)
    {
        for (var d = source; d is not null && !ReferenceEquals(d, stop); d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is ButtonBase)
            {
                return true;
            }
        }

        return false;
    }
}
