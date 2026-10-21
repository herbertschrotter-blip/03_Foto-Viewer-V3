using System.Windows.Controls;

namespace Lumora.Sorter.Views;

/// <summary>Bereich „Flatten &amp; Move“ — reines XAML, DataContext = <see cref="ViewModels.FlattenViewModel"/> (von der Seite gesetzt).</summary>
public partial class FlattenView : UserControl
{
    public FlattenView()
    {
        InitializeComponent();
    }
}
