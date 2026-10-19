using System.Windows.Controls;
using PhotoViewerV3.Gallery.Views;

namespace PhotoViewerV3.Folders;

/// <summary>
/// Seite „Ordner“: Ordnerbaum links (280 px), rechts die bestehende GalleryView als eigene Instanz
/// (per DI aufgelöst; ihr ViewModel ist Singleton und zeigt denselben Zustand wie die Galerie-Seite).
/// </summary>
public partial class FolderTreePage : UserControl
{
    public FolderTreePage(FolderTreeViewModel viewModel, GalleryView gallery)
    {
        InitializeComponent();
        DataContext = viewModel;
        GalleryHost.Content = gallery;
        Loaded += (_, _) => _ = viewModel.EnsureLoadedAsync();
    }
}
