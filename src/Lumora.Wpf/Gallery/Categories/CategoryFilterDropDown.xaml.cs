using System.Windows;
using System.Windows.Controls;

namespace Lumora.Gallery.Categories;

/// <summary>Dropdown für den Kategorien-Filter (ADR-0027). Logik liegt im GalleryViewModel.</summary>
public partial class CategoryFilterDropDown : UserControl
{
    public CategoryFilterDropDown()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => FilterPopup.IsOpen = false;
}
