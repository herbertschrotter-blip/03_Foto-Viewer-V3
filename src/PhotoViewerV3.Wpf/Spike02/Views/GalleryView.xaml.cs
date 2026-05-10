using System.Windows.Controls;
using PhotoViewerV3.Spike02.ViewModels;

namespace PhotoViewerV3.Spike02.Views;

public partial class GalleryView : UserControl
{
    public GalleryView(GalleryViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.EnsureInitializedAsync().ConfigureAwait(true);
    }
}
