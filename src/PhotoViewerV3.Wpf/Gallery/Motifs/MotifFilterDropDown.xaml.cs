using System;
using System.Windows;
using System.Windows.Controls;
using PhotoViewerV3.Gallery.Controls;

namespace PhotoViewerV3.Gallery.Motifs;

/// <summary>Chip + Flyout für den Motiv-Filter (ADR-0052). Logik liegt im GalleryViewModel (GalleryViewModel.Motifs.cs).</summary>
public partial class MotifFilterDropDown : UserControl
{
    public MotifFilterDropDown()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => FilterPopup.IsOpen = false;

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        => FlyoutListFilter.Apply<MotifFilterEntry>(MotifList, SearchBox.Text, m => m.Name);

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        SearchBox.Text = string.Empty;
        FlyoutListFilter.Clear(MotifList);
    }
}
