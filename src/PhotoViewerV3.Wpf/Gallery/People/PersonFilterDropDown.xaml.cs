using System.Windows;
using System.Windows.Controls;

namespace PhotoViewerV3.Gallery.People;

/// <summary>Dropdown für den Personen-Filter (0.2.38). Logik liegt im GalleryViewModel (GalleryViewModel.Persons.cs).</summary>
public partial class PersonFilterDropDown : UserControl
{
    public PersonFilterDropDown()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => FilterPopup.IsOpen = false;
}
