using System.Windows.Controls;

namespace PhotoViewerV3.Sorter.Views;

/// <summary>Bereich „Sortieren“ — reines XAML, DataContext = <see cref="ViewModels.FileSorterViewModel"/> (von der Seite gesetzt).</summary>
public partial class FileSorterView : UserControl
{
    public FileSorterView()
    {
        InitializeComponent();
    }
}
