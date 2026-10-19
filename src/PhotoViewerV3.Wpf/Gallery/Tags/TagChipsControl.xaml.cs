using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace PhotoViewerV3.Gallery.Tags;

/// <summary>Zeigt die Tags einer Datei als farbige Punkte (<see cref="Compact"/>) oder Pillen mit Namen.</summary>
public partial class TagChipsControl : UserControl
{
    public static readonly DependencyProperty TagsProperty = DependencyProperty.Register(
        nameof(Tags), typeof(IEnumerable), typeof(TagChipsControl), new PropertyMetadata(null));

    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(
        nameof(Compact), typeof(bool), typeof(TagChipsControl), new PropertyMetadata(false));

    public TagChipsControl()
    {
        InitializeComponent();
    }

    /// <summary>Tags (TagInfo) in Anzeigereihenfolge.</summary>
    public IEnumerable? Tags
    {
        get => (IEnumerable?)GetValue(TagsProperty);
        set => SetValue(TagsProperty, value);
    }

    /// <summary>True: nur farbige Punkte (Kachel), false: Pillen mit Namen.</summary>
    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }
}

/// <summary>Hex-Farbe eines Tags → Brush (siehe <see cref="TagBrushes"/>).</summary>
public sealed class TagBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => TagBrushes.ForHex(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
