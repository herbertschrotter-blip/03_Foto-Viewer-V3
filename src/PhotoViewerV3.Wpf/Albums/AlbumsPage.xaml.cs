using System.Windows.Controls;

namespace PhotoViewerV3.Albums;

/// <summary>Seite „Alben“ (ADR-0054); alle Logik im <see cref="AlbumsViewModel"/>.</summary>
public partial class AlbumsPage : UserControl
{
    public AlbumsPage(AlbumsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        ViewModel = viewModel;
        Loaded += (_, _) => _ = viewModel.LoadAsync();
    }

    public AlbumsViewModel ViewModel { get; }
}
