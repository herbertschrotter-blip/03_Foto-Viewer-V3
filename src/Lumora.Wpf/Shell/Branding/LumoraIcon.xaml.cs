using System;
using System.Windows;
using System.Windows.Media;

namespace Lumora.Shell.Branding;

/// <summary>Vektor-Quelle des App-Symbols (<c>LumoraIcon.xaml</c>); Zugriff über <see cref="LumoraIconImages"/>.</summary>
public partial class LumoraIconDictionary : ResourceDictionary
{
    public LumoraIconDictionary() => InitializeComponent();
}

/// <summary>
/// Eingefrorene Zeichnungen des App-Symbols für XAML (<c>{x:Static branding:LumoraIconImages.Full}</c>) und den
/// ICO-Erzeuger. Unabhängig von den App-Ressourcen, damit Fenster und Steuerelemente auch in Tests ohne App.xaml laden.
/// </summary>
public static class LumoraIconImages
{
    /// <summary>Unter dieser Kantenlänge (px) wird das vereinfachte Symbol gezeigt (ein Foto, großer Funke).</summary>
    public const int SmallVariantBelow = 32;

    private static readonly Lazy<LumoraIconDictionary> Dictionary = new(() => new LumoraIconDictionary());

    /// <summary>Volles Symbol (Fotostapel mit Funke), 128er-Raster.</summary>
    public static Drawing FullDrawing => (Drawing)Dictionary.Value["LumoraIconDrawing"];

    /// <summary>Vereinfachtes Symbol für kleine Größen.</summary>
    public static Drawing SmallDrawing => (Drawing)Dictionary.Value["LumoraIconSmallDrawing"];

    /// <summary>Volles Symbol als Bildquelle (ab 32 px).</summary>
    public static ImageSource Full => (ImageSource)Dictionary.Value["LumoraIconImage"];

    /// <summary>Vereinfachtes Symbol als Bildquelle (unter 32 px: Titelleiste, Navigation).</summary>
    public static ImageSource Small => (ImageSource)Dictionary.Value["LumoraIconSmallImage"];

    /// <summary>Passende Zeichnung zur Pixelgröße.</summary>
    public static Drawing DrawingFor(int pixelSize) => pixelSize < SmallVariantBelow ? SmallDrawing : FullDrawing;
}
