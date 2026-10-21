using System;
using System.Windows;
using System.Windows.Controls;
using PhotoViewerV3.Gallery.ViewModels;

namespace PhotoViewerV3.Gallery.Places;

/// <summary>Chip + Flyout für den Ort-Filter (ADR-0050). Logik liegt im GalleryViewModel (GalleryViewModel.Places.cs).</summary>
public partial class PlaceFilterDropDown : UserControl
{
    public PlaceFilterDropDown()
    {
        InitializeComponent();
    }

    private void OnPopupOpened(object? sender, EventArgs e)
    {
        if (DataContext is GalleryViewModel vm)
        {
            vm.LoadPlaceFilterCommand.Execute(null);
        }

        SearchBox.Focus();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => FilterPopup.IsOpen = false;

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        if (DataContext is GalleryViewModel vm)
        {
            vm.PlaceFilterSearch = null;
        }
    }
}
