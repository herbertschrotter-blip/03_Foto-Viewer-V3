using System.Windows.Controls;

namespace Lumora.Gallery.Controls;

/// <summary>Suchfeld der Galerie (ADR-0055: dasselbe Steuerelement in Galerie, Ordner und Orte); DataContext = GalleryViewModel.</summary>
public partial class GallerySearchBox : UserControl
{
    public GallerySearchBox()
    {
        InitializeComponent();
    }

    /// <summary>Das Eingabefeld (Layout-Tests, Strg+F).</summary>
    public Wpf.Ui.Controls.TextBox Box => SearchBox;

    /// <summary>Strg+F: Suchfeld fokussieren und den Text markieren.</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }
}
