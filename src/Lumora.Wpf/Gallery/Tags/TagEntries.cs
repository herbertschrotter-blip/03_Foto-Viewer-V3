using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumora.Services.Tags;

namespace Lumora.Gallery.Tags;

/// <summary>Hex-Farbe („#RRGGBB“) → eingefrorener SolidColorBrush; ungültige Werte ergeben Grau.</summary>
public static class TagBrushes
{
    private static readonly Dictionary<string, SolidColorBrush> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SolidColorBrush Fallback = Freeze(new SolidColorBrush(Color.FromRgb(0x75, 0x75, 0x75)));

    public static SolidColorBrush ForHex(string? hex)
    {
        if (!TagPalette.IsValid(hex))
        {
            return Fallback;
        }

        lock (Cache)
        {
            if (!Cache.TryGetValue(hex!, out var brush))
            {
                var color = (Color)ColorConverter.ConvertFromString(hex!.ToUpperInvariant());
                brush = Freeze(new SolidColorBrush(color));
                Cache[hex] = brush;
            }

            return brush;
        }
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Eintrag im Tag-Menü der Aktionsleiste bzw. des Kontextmenüs: Zustand bezogen auf die aktuelle Auswahl
/// (true = alle ausgewählten Dateien haben das Tag, false = keine, null = gemischt).
/// </summary>
public sealed partial class TagMenuEntry : ObservableObject
{
    public TagMenuEntry(TagInfo tag)
    {
        Tag = tag;
    }

    public TagInfo Tag { get; }

    public string Name => Tag.Name;

    public Brush Brush => TagBrushes.ForHex(Tag.Color);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnAll))]
    private bool? _state;

    public bool IsOnAll => State == true;
}

/// <summary>Eintrag im Tag-Filter-Dropdown (Häkchen = Tag gehört zum Filter).</summary>
public sealed partial class TagFilterEntry : ObservableObject
{
    public TagFilterEntry(TagInfo tag, int fileCount)
    {
        Tag = tag;
        FileCount = fileCount;
    }

    public TagInfo Tag { get; }

    public string Name => Tag.Name;

    public int FileCount { get; }

    public string CountText => FileCount.ToString("N0");

    public Brush Brush => TagBrushes.ForHex(Tag.Color);

    [ObservableProperty] private bool _isSelected;
}
