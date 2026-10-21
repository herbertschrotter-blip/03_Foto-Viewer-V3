using System;
using System.Windows;
using System.Windows.Controls;
using PhotoViewerV3.Gallery.Controls;

namespace PhotoViewerV3.Albums;

/// <summary>Chip + Flyout für den Filter „Album“ (ADR-0054). Logik liegt im GalleryViewModel (GalleryViewModel.Albums.cs).</summary>
public partial class AlbumFilterDropDown : UserControl
{
    public AlbumFilterDropDown()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => FilterPopup.IsOpen = false;

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        => FlyoutListFilter.Apply<AlbumFilterEntry>(AlbumList, SearchBox.Text, a => a.Name);

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        SearchBox.Text = string.Empty;
        FlyoutListFilter.Clear(AlbumList);
    }
}
