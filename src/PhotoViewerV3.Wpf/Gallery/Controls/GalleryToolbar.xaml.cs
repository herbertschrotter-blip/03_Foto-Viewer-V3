using System.Windows.Controls;

namespace PhotoViewerV3.Gallery.Controls;

/// <summary>
/// Kopfzeile der Galerie (Filter, Suche, Sortierung, Seitengröße, Ansicht, Rückgängig, Aktualisieren).
/// Reines Layout; DataContext = GalleryViewModel, kein Code-Behind-Verhalten.
/// </summary>
public partial class GalleryToolbar : UserControl
{
    public GalleryToolbar()
    {
        InitializeComponent();
    }
}
