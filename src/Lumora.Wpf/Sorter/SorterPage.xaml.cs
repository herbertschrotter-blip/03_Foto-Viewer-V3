using System.Windows.Controls;
using Lumora.Sorter.ViewModels;

namespace Lumora.Sorter;

/// <summary>
/// Seite „Sortieren“ (Tabs „Sortieren“ und „Flatten &amp; Move“). Code-Behind enthält nur UI-Glue:
/// DataContext setzen und die ViewModels beim ersten Anzeigen initialisieren.
/// </summary>
public partial class SorterPage : UserControl
{
    public SorterPage(SorterPageViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.EnsureInitializedAsync().ConfigureAwait(true);
    }
}
