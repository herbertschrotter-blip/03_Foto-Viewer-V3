using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lumora.Albums;
using Lumora.Gallery.Views;

namespace Lumora.Folders;

/// <summary>
/// Seite „Ordner“: Ordnerbaum links (280 px), rechts die bestehende GalleryView als eigene Instanz
/// (per DI aufgelöst; ihr ViewModel ist Singleton und zeigt denselben Zustand wie die Galerie-Seite).
/// Unter dem Baum die Alben; Galerie-Dateien lassen sich auf Ordner und Alben ziehen (ADR-0054, <see cref="GalleryDragData"/>).
/// </summary>
public partial class FolderTreePage : UserControl
{
    private readonly FolderPageAlbumsViewModel _albums;

    public FolderTreePage(FolderTreeViewModel viewModel, GalleryView gallery, FolderPageAlbumsViewModel albums)
    {
        InitializeComponent();
        DataContext = viewModel;
        _albums = albums;
        AlbumsPanel.DataContext = albums;
        GalleryHost.Content = gallery;
        Loaded += (_, _) => _ = viewModel.EnsureLoadedAsync();
    }

    /// <summary>Ordner unter dem Mauszeiger (kein Bibliotheks-Kopf, kein Platzhalter) oder null.</summary>
    private static FolderNode? NodeAt(object? source)
    {
        var current = source as DependencyObject;
        while (current is not null && current is not TreeViewItem)
        {
            current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }

        return (current as TreeViewItem)?.DataContext is FolderNode { IsFolder: true, IsLibraryHeader: false, IsPlaceholder: false } node ? node : null;
    }

    private void OnTreeDragOver(object sender, DragEventArgs e)
    {
        e.Effects = GalleryDragData.Read(e.Data) is { Length: > 0 } && NodeAt(e.OriginalSource) is not null
            ? GalleryDragData.EffectFor(e.KeyStates, e.AllowedEffects)
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnTreeDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (GalleryDragData.Read(e.Data) is not { Length: > 0 } ids || NodeAt(e.OriginalSource) is not { } node)
        {
            return;
        }

        var move = GalleryDragData.IsMove(e.KeyStates);
        // Nicht im OLE-Drop-Rückruf einen Dialog öffnen (DoDragDrop liefe sonst bis zum Schließen weiter).
        Dispatcher.BeginInvoke(() => _ = _albums.DropOnFolderAsync(node.Id, node.Name, node.Path, ids, move));
    }
}
