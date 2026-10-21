using System;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace PhotoViewerV3.Albums;

/// <summary>Schalter „mit Verknüpfungen“/„nur echte Dateien“ und Zähler der Ordneransicht (ADR-0054); DataContext = GalleryViewModel.</summary>
public partial class FolderLinksHeader : UserControl
{
    public FolderLinksHeader()
    {
        InitializeComponent();
    }
}

/// <summary>
/// Bool ↔ Optionsfeld: angehakt, wenn der Wert dem Parameter („True“/„False“) entspricht; nur das ANHAKEN schreibt
/// zurück (Abhaken = <see cref="Binding.DoNothing"/>). Ohne das schaukelten sich die Optionsfelder der beiden
/// Galerie-Instanzen (Galerie- und Ordner-Seite) gegenseitig auf (Stapelüberlauf im Rauchtest).
/// </summary>
public sealed class BoolOptionConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b == IsTrue(parameter);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? IsTrue(parameter) : Binding.DoNothing;

    private static bool IsTrue(object? parameter) => string.Equals(parameter as string, "True", StringComparison.OrdinalIgnoreCase);
}
