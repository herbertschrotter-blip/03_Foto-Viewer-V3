using System;
using System.Windows;
using System.Windows.Controls;
using Lumora.Gallery.Controls;

namespace Lumora.Gallery.Tags;

/// <summary>Chip + Flyout für den Tag-Filter (Häkchen je Tag, mindestens eines / alle / ohne Tag). Logik liegt im GalleryViewModel.</summary>
public partial class TagFilterDropDown : UserControl
{
    public TagFilterDropDown()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => FilterPopup.IsOpen = false;

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        => FlyoutListFilter.Apply<TagFilterEntry>(TagList, SearchBox.Text, t => t.Name);

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        SearchBox.Text = string.Empty;
        FlyoutListFilter.Clear(TagList);
    }
}
