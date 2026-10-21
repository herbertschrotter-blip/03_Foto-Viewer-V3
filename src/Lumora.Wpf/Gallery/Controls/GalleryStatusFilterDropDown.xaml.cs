using System.Windows.Controls;
using System.Windows.Input;

namespace Lumora.Gallery.Controls;

/// <summary>Chip „Ablage“ der Galerie-Kopfzeile (Überall / Eingang / Bibliothek); DataContext = GalleryViewModel.</summary>
public partial class GalleryStatusFilterDropDown : UserControl
{
    public GalleryStatusFilterDropDown()
    {
        InitializeComponent();
    }

    /// <summary>Nach der Auswahl schließt das Popup (die Bindung hat den Filter bereits gesetzt).</summary>
    private void OnOptionChosen(object sender, MouseButtonEventArgs e) => StatusPopup.IsOpen = false;

    /// <summary>Enter/Leertaste bestätigt die per Pfeiltaste gewählte Zeile, Esc schließt.</summary>
    private void OnOptionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Space or Key.Escape)
        {
            StatusPopup.IsOpen = false;
            Toggle.Focus();
            e.Handled = true;
        }
    }
}
