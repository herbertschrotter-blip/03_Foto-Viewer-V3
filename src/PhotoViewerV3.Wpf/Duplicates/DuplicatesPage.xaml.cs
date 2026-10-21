using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PhotoViewerV3.Duplicates;

/// <summary>
/// Seite „Doppelte“ (ADR-0053). Code-Behind nur als UI-Klebstoff: Laden beim Sichtbarwerden, Vorschaubilder für sichtbare
/// (auch recycelte) Gruppen anfordern, Doppelklick auf ein Bild öffnet die Lightbox.
/// </summary>
public partial class DuplicatesPage : UserControl
{
    private readonly DuplicatesViewModel _viewModel;

    public DuplicatesPage(DuplicatesViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => _ = viewModel.ActivateAsync();
        Unloaded += (_, _) => viewModel.Deactivate();
    }

    public DuplicatesViewModel ViewModel => _viewModel;

    private void OnGroupLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DuplicateGroupViewModel group })
        {
            _viewModel.EnsureImages(group);
        }
    }

    private void OnGroupDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is DuplicateGroupViewModel group && sender is FrameworkElement { IsLoaded: true })
        {
            _viewModel.EnsureImages(group);
        }
    }

    private void OnImageMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: DuplicateMemberViewModel member })
        {
            _viewModel.Open(member);
            e.Handled = true;
        }
    }
}
