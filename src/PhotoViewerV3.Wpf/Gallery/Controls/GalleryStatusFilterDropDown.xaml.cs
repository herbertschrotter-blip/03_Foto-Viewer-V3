using System.Windows;
using System.Windows.Controls;

namespace PhotoViewerV3.Gallery.Controls;

/// <summary>Status-Filter der Galerie-Kopfzeile (Eingang/Archiv); DataContext = GalleryViewModel.</summary>
public partial class GalleryStatusFilterDropDown : UserControl
{
    public GalleryStatusFilterDropDown()
    {
        InitializeComponent();
    }

    /// <summary>Nach der Auswahl schließt das Popup (die Bindung hat den Filter bereits gesetzt).</summary>
    private void OnOptionClick(object sender, RoutedEventArgs e) => StatusPopup.IsOpen = false;
}
