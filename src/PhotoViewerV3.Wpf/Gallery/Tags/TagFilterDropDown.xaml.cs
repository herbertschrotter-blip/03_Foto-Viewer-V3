using System.Windows;
using System.Windows.Controls;

namespace PhotoViewerV3.Gallery.Tags;

/// <summary>Dropdown für den Tag-Filter (Häkchen je Tag, ODER/UND/ohne Tag). Logik liegt im GalleryViewModel.</summary>
public partial class TagFilterDropDown : UserControl
{
    public TagFilterDropDown()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => FilterPopup.IsOpen = false;
}
