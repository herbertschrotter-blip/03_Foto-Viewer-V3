using System.Windows;
using System.Windows.Controls;

namespace PhotoViewerV3.Gallery.Controls;

/// <summary>
/// Kopfzeile der Galerie (0.2.43): Titel mit Trefferzeile und Suche, Filterkarte („Datei“, „Inhalt“, „Es gilt“),
/// Sortier- und Ansichtsleiste. Reines Layout; DataContext = GalleryViewModel. <see cref="PageTitle"/> setzt die
/// GalleryView („Galerie“ bzw. „Ordner“ auf der Ordner-Seite).
/// </summary>
public partial class GalleryToolbar : UserControl
{
    public static readonly DependencyProperty PageTitleProperty = DependencyProperty.Register(
        nameof(PageTitle),
        typeof(string),
        typeof(GalleryToolbar),
        new PropertyMetadata("Galerie"));

    public GalleryToolbar()
    {
        InitializeComponent();
    }

    /// <summary>Seitentitel links oben („Galerie“ oder „Ordner“).</summary>
    public string PageTitle
    {
        get => (string)GetValue(PageTitleProperty);
        set => SetValue(PageTitleProperty, value);
    }

    /// <summary>Strg+F: Suchfeld fokussieren und den Text markieren.</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }
}
