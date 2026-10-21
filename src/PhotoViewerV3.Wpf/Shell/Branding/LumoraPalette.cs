using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace PhotoViewerV3.Shell.Branding;

/// <summary>
/// Markenfarben von Lumora (Mockup 07, „Gewählt · A + B + C“): Indigo (App-Akzent) → Violett → Koralle → Bernstein (Licht).
/// Hell und dunkel getrennt; die Pinsel sind eingefroren und damit threadübergreifend nutzbar.
/// </summary>
public sealed record LumoraPalette(Color Indigo, Color Violet, Color Coral, Color Amber)
{
    /// <summary>Helles Farbschema: #5B5FC7 → #8B5CF6 → #F97362 → #F5A524.</summary>
    public static LumoraPalette Light { get; } = new(
        Color.FromRgb(0x5B, 0x5F, 0xC7), Color.FromRgb(0x8B, 0x5C, 0xF6), Color.FromRgb(0xF9, 0x73, 0x62), Color.FromRgb(0xF5, 0xA5, 0x24));

    /// <summary>Dunkles Farbschema: #818CF8 → #A78BFA → #FB8A7A → #FBBF24.</summary>
    public static LumoraPalette Dark { get; } = new(
        Color.FromRgb(0x81, 0x8C, 0xF8), Color.FromRgb(0xA7, 0x8B, 0xFA), Color.FromRgb(0xFB, 0x8A, 0x7A), Color.FromRgb(0xFB, 0xBF, 0x24));

    /// <summary>Palette zum WPF-UI-Farbschema (Dunkel → dunkel, sonst hell).</summary>
    public static LumoraPalette For(ApplicationTheme theme) => theme == ApplicationTheme.Dark ? Dark : Light;

    /// <summary>Verlauf des Schriftzugs über das ganze Wort (relativ, links → rechts).</summary>
    public LinearGradientBrush CreateWordmarkBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            GradientStops =
            {
                new GradientStop(Indigo, 0.08),
                new GradientStop(Violet, 0.45),
                new GradientStop(Coral, 0.75),
                new GradientStop(Amber, 0.97),
            },
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>Verlauf des Linsenrings (Violett → Koralle, links → rechts).</summary>
    public LinearGradientBrush CreateLensRingBrush()
    {
        var brush = new LinearGradientBrush(Violet, Coral, new Point(0, 0.5), new Point(1, 0.5));
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Feste (vom Farbschema unabhängige) Bausteine des App-Symbols und des Funkens; von <c>LumoraIcon.xaml</c> per
/// <c>x:Static</c> genutzt, damit jede Farbe nur einmal definiert ist. Koordinaten im 128er- bzw. 24er-Raster des Mockups.
/// </summary>
public static class LumoraIconParts
{
    /// <summary>Vierzackiger Funke im 24er-Raster (Mockup-Symbol <c>spark</c>).</summary>
    public static Geometry SparkGeometry { get; } = Frozen(Geometry.Parse("M12,0 C13,8 16,11 24,12 C16,13 13,16 12,24 C11,16 8,13 0,12 C8,11 11,8 12,0 Z"));

    /// <summary>Funken-Verlauf (radial): Hellgelb → Bernstein → Koralle.</summary>
    public static Brush SparkBrush { get; } = Frozen(new RadialGradientBrush
    {
        GradientStops =
        {
            new GradientStop(Color.FromRgb(0xFF, 0xE7, 0xA3), 0),
            new GradientStop(Color.FromRgb(0xF5, 0xA5, 0x24), 0.55),
            new GradientStop(Color.FromRgb(0xF9, 0x73, 0x62), 1),
        },
    });

    /// <summary>Kachel: Indigo #6366F1 → #4145A0 diagonal.</summary>
    public static Brush TileBrush { get; } = Frozen(new LinearGradientBrush(
        Color.FromRgb(0x63, 0x66, 0xF1), Color.FromRgb(0x41, 0x45, 0xA0), new Point(0, 0), new Point(1, 1)));

    /// <summary>Vorderes Foto: #FDE68A → #F97362 senkrecht.</summary>
    public static Brush PhotoWarmBrush { get; } = Frozen(new LinearGradientBrush(
        Color.FromRgb(0xFD, 0xE6, 0x8A), Color.FromRgb(0xF9, 0x73, 0x62), new Point(0, 0), new Point(0, 1)));

    /// <summary>Hinteres Foto: #C4B5FD → #8B5CF6 senkrecht.</summary>
    public static Brush PhotoVioletBrush { get; } = Frozen(new LinearGradientBrush(
        Color.FromRgb(0xC4, 0xB5, 0xFD), Color.FromRgb(0x8B, 0x5C, 0xF6), new Point(0, 0), new Point(0, 1)));

    /// <summary>Weißer Fotorahmen vorn.</summary>
    public static Brush FrameBrush { get; } = Frozen(new SolidColorBrush(Colors.White));

    /// <summary>Weißer Fotorahmen hinten (Deckkraft 55 %).</summary>
    public static Brush BackFrameBrush { get; } = Frozen(new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)));

    /// <summary>Berg im Foto (#4145A0, Deckkraft 85 %).</summary>
    public static Brush MountainBrush { get; } = Frozen(new SolidColorBrush(Color.FromArgb(0xD9, 0x41, 0x45, 0xA0)));

    /// <summary>Berg im vereinfachten Symbol (deckend).</summary>
    public static Brush MountainSolidBrush { get; } = Frozen(new SolidColorBrush(Color.FromRgb(0x41, 0x45, 0xA0)));

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
