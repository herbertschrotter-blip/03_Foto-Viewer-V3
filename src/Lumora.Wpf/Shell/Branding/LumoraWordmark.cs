using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace Lumora.Shell.Branding;

/// <summary>
/// Schriftzug „lumora“ (ADR-0047, Mockup 07): Kleinbuchstaben in Segoe UI Variable Display im Lichtverlauf
/// Indigo → Violett → Koralle → Bernstein über das ganze Wort; das „o“ ist eine dünne Linse (<see cref="LumoraLens"/>) mit
/// dem Funken darin. Größe über <see cref="Control.FontSize"/>; Strichstärke folgt der Größe (bis 24 px normal, bis 40 px
/// halbleicht, darüber leicht), Farben je Farbschema über die Ressourcen <see cref="LumoraBrand.WordmarkBrushKey"/> und
/// <see cref="LumoraBrand.LensRingBrushKey"/> (<see cref="LumoraBrandDictionary"/>); ohne sie gilt die helle Palette.
/// <para>
/// Aufbau: ein TextBlock „lum“ + Linse (InlineUIContainer auf der Grundlinie) + „ra“. Die Linse misst sich am echten
/// „o“ der Schrift (Breite = Vorschub, Höhe und Unterlänge = Glyphen-Umriss), damit sie wie ein Buchstabe sitzt. Der
/// Verlauf liegt in absoluten Koordinaten über dem ganzen TextBlock, damit „lum“ und „ra“ einen Verlauf teilen.
/// </para>
/// </summary>
public sealed class LumoraWordmark : UserControl
{
    /// <summary>Verlauf des Schriftzugs (relativ 0..1); wird über die Wortbreite gelegt.</summary>
    public static readonly DependencyProperty WordmarkBrushProperty = DependencyProperty.Register(
        nameof(WordmarkBrush), typeof(Brush), typeof(LumoraWordmark),
        new FrameworkPropertyMetadata(LumoraPalette.Light.CreateWordmarkBrush(), (d, _) => ((LumoraWordmark)d).UpdateTextBrush()));

    /// <summary>Verlauf des Linsenrings.</summary>
    public static readonly DependencyProperty LensRingBrushProperty = DependencyProperty.Register(
        nameof(LensRingBrush), typeof(Brush), typeof(LumoraWordmark),
        new FrameworkPropertyMetadata(LumoraPalette.Light.CreateLensRingBrush(), (d, e) => ((LumoraWordmark)d).OnLensRingBrushChanged((Brush)e.NewValue)));

    /// <summary>Bis zu dieser Schriftgröße (px) normale Strichstärke und kräftige Linse ohne Innenring.</summary>
    public const double SmallSizeMax = 24;

    /// <summary>Bis zu dieser Schriftgröße (px) halbleichte Strichstärke, darüber leicht.</summary>
    public const double MediumSizeMax = 40;

    /// <summary>Halbleicht (OpenType 350, wie im Mockup für den Info-Kopf); WPF kennt dafür keinen Namen.</summary>
    private static readonly FontWeight SemiLight = FontWeight.FromOpenTypeWeight(350);

    private readonly TextBlock _text;

    public LumoraWordmark()
    {
        Lens = new LumoraLens { RingBrush = LensRingBrush };
        _text = new TextBlock { TextWrapping = TextWrapping.NoWrap };
        _text.Inlines.Add(new Run("lum"));
        _text.Inlines.Add(new InlineUIContainer(Lens) { BaselineAlignment = BaselineAlignment.Baseline });
        _text.Inlines.Add(new Run("ra"));
        _text.SizeChanged += (_, _) => UpdateTextBrush();
        // Lokal binden: ein implizites TextBlock-Theme (WPF-UI) setzt sonst eigene Schriftgröße/-art per Style
        _text.SetBinding(TextBlock.FontSizeProperty, new Binding(nameof(FontSize)) { Source = this });
        _text.SetBinding(TextBlock.FontFamilyProperty, new Binding(nameof(FontFamily)) { Source = this });
        Content = _text;
        Focusable = false;
        IsTabStop = false;
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");

        Loaded += (_, _) => UpdateMetrics(); // jetzt mit der Skalierung des Bildschirms, auf dem das Fenster liegt
        SetResourceReference(WordmarkBrushProperty, LumoraBrand.WordmarkBrushKey);
        SetResourceReference(LensRingBrushProperty, LumoraBrand.LensRingBrushKey);
        UpdateMetrics();
    }

    public Brush WordmarkBrush
    {
        get => (Brush)GetValue(WordmarkBrushProperty);
        set => SetValue(WordmarkBrushProperty, value);
    }

    public Brush LensRingBrush
    {
        get => (Brush)GetValue(LensRingBrushProperty);
        set => SetValue(LensRingBrushProperty, value);
    }

    /// <summary>Die Linse an Stelle des „o“ (für Tests und Rauchtest).</summary>
    internal LumoraLens Lens { get; }

    /// <summary>Tatsächlich gezeichneter Textpinsel (absolut über die Wortbreite).</summary>
    internal Brush? TextBrush => _text.Foreground;

    private void OnLensRingBrushChanged(Brush brush)
    {
        if (Lens is not null)
        {
            Lens.RingBrush = brush;
        }
    }

    /// <summary>Strichstärke zur Schriftgröße: klein normal, mittel halbleicht, groß leicht.</summary>
    public static FontWeight WeightFor(double fontSize)
        => fontSize <= SmallSizeMax ? FontWeights.Normal
            : fontSize <= MediumSizeMax ? SemiLight
            : FontWeights.Light;

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == FontSizeProperty || e.Property == FontFamilyProperty)
        {
            UpdateMetrics();
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateMetrics(); // Glyphen-Maße hängen an der Bildschirm-Skalierung
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new WordmarkAutomationPeer(this);

    /// <summary>Strichstärke, Linsengröße und -lage aus dem „o“ der Schrift ableiten.</summary>
    private void UpdateMetrics()
    {
        if (_text is null || Lens is null)
        {
            return; // Basiskonstruktor setzt Eigenschaften, bevor die Teile existieren
        }

        var size = FontSize;
        var weight = WeightFor(size);
        _text.FontWeight = weight;
        Lens.IsBold = size <= SmallSizeMax;

        var typeface = new Typeface(FontFamily, FontStyles.Normal, weight, FontStretches.Normal);
        var o = new FormattedText("o", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var bounds = o.BuildGeometry(new Point(0, 0)).Bounds;
        var glyphHeight = bounds.IsEmpty ? size * 0.52 : bounds.Height;
        var descent = bounds.IsEmpty ? 0 : Math.Max(0, bounds.Bottom - o.Baseline); // Überhang des „o“ unter die Grundlinie

        // Ringaußenkante = Höhe des „o“; die Linse zeichnet den Ring mit Rand im 100er-Raster
        var box = glyphHeight / Lens.OuterDiameterFraction;
        var side = Math.Max(0, (o.WidthIncludingTrailingWhitespace - box) / 2);
        Lens.Width = box;
        Lens.Height = box;
        Lens.Margin = new Thickness(side, 0, side, 0);
        Lens.RenderTransform = new TranslateTransform(0, descent + ((1 - Lens.OuterDiameterFraction) / 2 * box));
    }

    /// <summary>Relativen Verlauf als absoluten Pinsel über den ganzen TextBlock legen (leicht geneigt wie im Mockup).</summary>
    private void UpdateTextBrush()
    {
        if (_text is null)
        {
            return;
        }

        var width = _text.ActualWidth;
        var height = _text.ActualHeight;
        if (WordmarkBrush is not LinearGradientBrush source || width <= 0 || height <= 0)
        {
            _text.Foreground = WordmarkBrush;
            return;
        }

        var brush = new LinearGradientBrush(source.GradientStops.Clone())
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, height * 0.3),
            EndPoint = new Point(width, height * 0.7),
        };
        brush.Freeze();
        _text.Foreground = brush;
    }

    /// <summary>Ein Blatt im Automation-Baum mit dem Namen „Lumora“ (statt „lum“ + Bild + „ra“).</summary>
    private sealed class WordmarkAutomationPeer(LumoraWordmark owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetNameCore() => LumoraBrand.ProductName;

        protected override string GetClassNameCore() => nameof(LumoraWordmark);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override System.Collections.Generic.List<AutomationPeer>? GetChildrenCore() => null;
    }
}

/// <summary>
/// Das „o“ von Lumora: dünner Ring im Verlauf Violett → Koralle, feiner Innenring, in der Mitte der Funke (Mockup-Symbole
/// <c>lensSpark</c>, klein <c>lensSparkBold</c> mit dickerem Ring ohne Innenring). Gezeichnet im 100er-Raster.
/// </summary>
public sealed class LumoraLens : FrameworkElement
{
    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush), typeof(Brush), typeof(LumoraLens),
        new FrameworkPropertyMetadata(LumoraPalette.Light.CreateLensRingBrush(), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsBoldProperty = DependencyProperty.Register(
        nameof(IsBold), typeof(bool), typeof(LumoraLens),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush RingBrush
    {
        get => (Brush)GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    /// <summary>Kräftige Variante für kleine Größen (Ring 12 statt 9, kein Innenring, größerer Funke).</summary>
    public bool IsBold
    {
        get => (bool)GetValue(IsBoldProperty);
        set => SetValue(IsBoldProperty, value);
    }

    /// <summary>Anteil der Ring-Außenkante an der Kantenlänge (Ring r 43 + 4,5 bzw. r 42 + 6 im 100er-Raster).</summary>
    public double OuterDiameterFraction => IsBold ? 0.96 : 0.95;

    protected override void OnRender(DrawingContext dc)
    {
        var scale = Math.Min(ActualWidth, ActualHeight) / 100.0;
        if (scale <= 0)
        {
            return;
        }

        dc.PushTransform(new ScaleTransform(scale, scale));
        var center = new Point(50, 50);
        if (IsBold)
        {
            dc.DrawEllipse(null, new Pen(RingBrush, 12), center, 42, 42);
            DrawSpark(dc, 27, 46);
        }
        else
        {
            dc.DrawEllipse(null, new Pen(RingBrush, 9), center, 43, 43);
            dc.PushOpacity(0.45);
            dc.DrawEllipse(null, new Pen(RingBrush, 3), center, 27, 27);
            dc.Pop();
            DrawSpark(dc, 30, 40);
        }

        dc.Pop();
    }

    private static void DrawSpark(DrawingContext dc, double offset, double size)
    {
        var factor = size / 24.0;
        dc.PushTransform(new MatrixTransform(factor, 0, 0, factor, offset, offset));
        dc.DrawGeometry(LumoraIconParts.SparkBrush, null, LumoraIconParts.SparkGeometry);
        dc.Pop();
    }
}
