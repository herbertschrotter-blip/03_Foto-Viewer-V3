using System.Windows.Controls;

namespace Lumora.Lightbox;

/// <summary>Inhaltsansicht eines Archivs in der Lightbox (ADR-0022); DataContext ist das <see cref="LightboxViewModel"/> des Fensters.</summary>
public partial class ArchiveContentView : UserControl
{
    public ArchiveContentView()
    {
        InitializeComponent();
    }
}
