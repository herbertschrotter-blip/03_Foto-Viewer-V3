using System.Windows.Controls;
using System.Windows.Input;

namespace PhotoViewerV3.Gallery.Controls;

/// <summary>Chip „Bewertung“ der Galerie-Kopfzeile (Mindest-Bewertung 0–5); DataContext = GalleryViewModel.</summary>
public partial class GalleryRatingFilterDropDown : UserControl
{
    public GalleryRatingFilterDropDown()
    {
        InitializeComponent();
    }

    /// <summary>Nach der Auswahl schließt das Popup (die Bindung hat den Filter bereits gesetzt).</summary>
    private void OnOptionChosen(object sender, MouseButtonEventArgs e) => RatingPopup.IsOpen = false;

    /// <summary>Enter/Leertaste bestätigt die per Pfeiltaste gewählte Zeile, Esc schließt.</summary>
    private void OnOptionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Space or Key.Escape)
        {
            RatingPopup.IsOpen = false;
            Toggle.Focus();
            e.Handled = true;
        }
    }
}
