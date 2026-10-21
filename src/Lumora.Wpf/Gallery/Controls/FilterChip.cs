using System.Windows;

namespace Lumora.Gallery.Controls;

/// <summary>
/// Angehängte Eigenschaft für die Filter-Chips der Galerie-Kopfzeile (0.2.43): <c>IsActive</c> = der Filter wirkt —
/// der Chip erscheint dann in Akzentfarbe (Stil <c>GalleryFilterChip</c> in <c>GalleryFilterStyles.xaml</c>).
/// Unabhängig von <c>IsChecked</c>, das bei Chips mit Auswahl-Popup „Popup offen“ bedeutet.
/// </summary>
public static class FilterChip
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive",
        typeof(bool),
        typeof(FilterChip),
        new FrameworkPropertyMetadata(false));

    public static bool GetIsActive(DependencyObject element) => (bool)element.GetValue(IsActiveProperty);

    public static void SetIsActive(DependencyObject element, bool value) => element.SetValue(IsActiveProperty, value);
}
