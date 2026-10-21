using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoViewerV3.Settings.Info;
using PhotoViewerV3.Shell;
using PhotoViewerV3.Shell.Branding;
using PhotoViewerV3.Wpf.Tests.Fixtures;
using Wpf.Ui.Appearance;
using Xunit.Abstractions;
using static PhotoViewerV3.Wpf.Tests.Shell.LumoraIconFileTests;

namespace PhotoViewerV3.Wpf.Tests.Shell;

/// <summary>
/// Produktname Lumora (ADR-0047): Programmdatei/Produkt „Lumora“, sichtbare Titel, Info-Kopf, keine Pack-URIs mit dem
/// Assembly-Namen, Schriftzug rendert im Verlauf (Pixel-Stichproben hell/dunkel) und die Linse sitzt wie ein „o“.
/// PNGs landen unter <c>bin/…/branding-shots</c>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class LumoraBrandingTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void Programmdatei_und_Produkt_heissen_Lumora()
    {
        var assembly = typeof(PhotoViewerV3.App).Assembly;
        assembly.GetName().Name.Should().Be("Lumora");
        assembly.GetCustomAttribute<AssemblyProductAttribute>()!.Product.Should().Be("Lumora");
        assembly.GetCustomAttribute<AssemblyTitleAttribute>()!.Title.Should().Be("Lumora");

        var info = FileVersionInfo.GetVersionInfo(assembly.Location);
        info.ProductName.Should().Be("Lumora");
        info.FileDescription.Should().Be("Lumora", "Taskleiste/Task-Manager zeigen die Dateibeschreibung");
        Path.GetFileName(assembly.Location).Should().Be("Lumora.dll");
        LumoraBrand.ExecutableName.Should().Be("Lumora.exe");

        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PhotoViewerV3.Wpf", "PhotoViewerV3.Wpf.csproj"));
        csproj.Should().Contain("<AssemblyName>Lumora</AssemblyName>").And.Contain("<ApplicationIcon");
    }

    [Fact]
    public void Sichtbare_Namen_und_Info_Vorgaenger()
    {
        AppInfo.AppName.Should().Be("Lumora");
        LumoraBrand.WindowTitle("IMG_0001.jpg").Should().Be("IMG_0001.jpg – Lumora");
        LumoraBrand.WindowTitle(null).Should().Be("Lumora");

        var root = RepoRoot();
        var main = File.ReadAllText(Path.Combine(root, "src", "PhotoViewerV3.Wpf", "MainWindow.xaml"));
        CountOf(main, "Title=\"{x:Static branding:LumoraBrand.ProductName}\"").Should().Be(2, "Fenster- und Titelleistentitel");
        main.Should().Contain("LumoraIconImages.Small", "Titelleisten-Symbol");
        var lightbox = File.ReadAllText(Path.Combine(root, "src", "PhotoViewerV3.Wpf", "Lightbox", "LightboxWindow.xaml"));
        lightbox.Should().Contain("Title=\"{Binding Title, Converter={x:Static branding:LumoraWindowTitleConverter.Instance}}\"");
        LumoraWindowTitleConverter.Instance.Convert("bild.jpg", typeof(string), null, CultureInfo.InvariantCulture).Should().Be("bild.jpg – Lumora");

        // Kein sichtbarer Text nennt mehr „PhotoViewer“ (Code-Bezeichner, Pfade und Kommentare ausgenommen)
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src", "PhotoViewerV3.Wpf"), "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".xaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line)))
            .Where(l => VisibleProductName(l.Text))
            .Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}")
            .ToList();
        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Kein_XAML_verweist_per_Assembly_Namen_auf_eigene_Ressourcen()
    {
        // Seit der Umbenennung der Programmdatei (Lumora.dll) zeigte „/PhotoViewerV3;component/…“ ins Leere
        var offenders = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "PhotoViewerV3.Wpf"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".xaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("/PhotoViewerV3;component", StringComparison.Ordinal))
            .ToList();
        offenders.Should().BeEmpty("eigene Ressourcen relativ oder über x:Static einbinden");
    }

    [Fact]
    public void Info_Kopf_und_Navigation_zeigen_Symbol_und_Schriftzug()
    {
        TestUiThread.Run(
            () =>
            {
                var info = new InfoSection();
                var infoMark = info.FindName("Wordmark").Should().BeOfType<LumoraWordmark>().Subject;
                infoMark.FontSize.Should().Be(30);
                FindImages(info).Should().Contain(i => ReferenceEquals(i.Source, LumoraIconImages.Full));

                var switcher = new LibrarySwitcher();
                var navMark = switcher.FindName("Wordmark").Should().BeOfType<LumoraWordmark>().Subject;
                navMark.FontSize.Should().Be(22);
                var header = switcher.FindName("BrandHeader").Should().BeOfType<StackPanel>().Subject;
                ((Panel)header.Parent).Children.IndexOf(header).Should().Be(0, "Schriftzug steht über dem Umschalter");
                FindImages(switcher).Should().Contain(i => ReferenceEquals(i.Source, LumoraIconImages.Small), "unter 32 px vereinfacht");
                return true;
            },
            Timeout);
    }

    [Theory]
    [InlineData(18, 400)]
    [InlineData(22, 400)]
    [InlineData(30, 350)]
    [InlineData(68, 300)]
    public void Strichstaerke_folgt_der_Groesse(double size, int openTypeWeight)
        => LumoraWordmark.WeightFor(size).ToOpenTypeWeight().Should().Be(openTypeWeight);

    [Fact]
    public void Schriftzug_rendert_im_Verlauf_hell_und_dunkel()
    {
        var shots = Path.Combine(AppContext.BaseDirectory, "branding-shots");
        Directory.CreateDirectory(shots);

        var light = Render(dark: false, fontSize: 68, Path.Combine(shots, "wordmark-68-hell.png"));
        var dark = Render(dark: true, fontSize: 68, Path.Combine(shots, "wordmark-68-dunkel.png"));
        Render(dark: false, fontSize: 22, Path.Combine(shots, "wordmark-22-hell.png"));
        Render(dark: true, fontSize: 22, Path.Combine(shots, "wordmark-22-dunkel.png"));
        Render(dark: false, fontSize: 30, Path.Combine(shots, "wordmark-30-hell.png"));
        output.WriteLine($"PNGs: {shots}");

        foreach (var (name, sample, palette) in new[] { ("hell", light, LumoraPalette.Light), ("dunkel", dark, LumoraPalette.Dark) })
        {
            output.WriteLine($"{name}: links {sample.Left}, rechts {sample.Right}, Linsenmitte {sample.LensCenter}, Tinte {sample.InkWidth} px");
            Distance(sample.Left, palette.Indigo).Should().BeLessThan(90, $"{name}: „l“ beginnt Indigo");
            Distance(sample.Right, palette.Amber).Should().BeLessThan(90, $"{name}: „a“ endet Bernstein");
            Distance(sample.Left, sample.Right).Should().BeGreaterThan(150, "Verlauf über das ganze Wort");
            ((int)sample.LensCenter.R).Should().BeGreaterThan(sample.LensCenter.B + 60, "Funke in der Linse ist warm");
        }

        Distance(light.Right, dark.Right).Should().BeGreaterThan(20, "dunkles Schema hat eigene Farben");
    }

    [Fact]
    public void Markenpinsel_folgen_dem_Farbschema()
    {
        TestUiThread.Run(
            () =>
            {
                var previous = ApplicationThemeManager.GetAppTheme();
                var dictionary = new LumoraBrandDictionary();
                try
                {
                    ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                    dictionary.Palette.Should().Be(LumoraPalette.Dark);
                    ((LinearGradientBrush)dictionary[LumoraBrand.WordmarkBrushKey]).GradientStops[0].Color.Should().Be(LumoraPalette.Dark.Indigo);
                    ApplicationThemeManager.Apply(ApplicationTheme.Light);
                    dictionary.Palette.Should().Be(LumoraPalette.Light);
                    ((LinearGradientBrush)dictionary[LumoraBrand.LensRingBrushKey]).GradientStops[0].Color.Should().Be(LumoraPalette.Light.Violet);

                    // DynamicResource: ein Schriftzug unter dem Wörterbuch färbt sich beim Wechsel um
                    var mark = new LumoraWordmark { FontSize = 30 };
                    var host = new Border { Child = mark };
                    host.Resources.MergedDictionaries.Add(dictionary);
                    ((LinearGradientBrush)mark.WordmarkBrush).GradientStops[3].Color.Should().Be(LumoraPalette.Light.Amber);
                    dictionary.Apply(ApplicationTheme.Dark);
                    ((LinearGradientBrush)mark.WordmarkBrush).GradientStops[3].Color.Should().Be(LumoraPalette.Dark.Amber);
                    ((LinearGradientBrush)mark.LensRingBrush).GradientStops[1].Color.Should().Be(LumoraPalette.Dark.Coral);
                    mark.Lens.RingBrush.Should().BeSameAs(mark.LensRingBrush);
                }
                finally
                {
                    dictionary.Detach();
                    if (previous != ApplicationTheme.Unknown)
                    {
                        ApplicationThemeManager.Apply(previous);
                    }
                }

                return true;
            },
            Timeout);
    }

    [Theory]
    [InlineData(22)]
    [InlineData(30)]
    [InlineData(68)]
    public void Linse_sitzt_wie_ein_o(double fontSize)
    {
        var (lens, glyph, wordmarkWidth, textWidth) = TestUiThread.Run(
            () =>
            {
                var mark = new LumoraWordmark { FontSize = fontSize };
                var reference = new TextBlock
                {
                    Text = "lumora",
                    FontSize = fontSize,
                    FontFamily = mark.FontFamily,
                    FontWeight = LumoraWordmark.WeightFor(fontSize),
                    Foreground = Brushes.Black,
                };
                var markInk = RenderInk(mark);
                var textInk = RenderInk(reference);

                // Spaltenbereich des „o“: hinter „lum“, so breit wie sein Vorschub
                var typeface = new Typeface(mark.FontFamily, FontStyles.Normal, LumoraWordmark.WeightFor(fontSize), FontStretches.Normal);
                double Width(string s) => new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, fontSize, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;
                var from = (int)Math.Round(Width("lum")) + 1;
                var to = (int)Math.Round(Width("lumo")) - 1;
                return (markInk.Rows(from, to), textInk.Rows(from, to), markInk.InkWidth, textInk.InkWidth);
            },
            Timeout);

        output.WriteLine($"{fontSize} px: Linse Zeilen {lens.Top}–{lens.Bottom}, „o“ {glyph.Top}–{glyph.Bottom}; Breite Schriftzug {wordmarkWidth}, Text {textWidth}");
        var tolerance = Math.Max(1.5, fontSize * 0.04);
        Math.Abs(lens.Top - glyph.Top).Should().BeLessThanOrEqualTo(tolerance, "Oberkante wie das „o“");
        Math.Abs(lens.Bottom - glyph.Bottom).Should().BeLessThanOrEqualTo(tolerance, "Unterkante wie das „o“ (auf der Grundlinie)");
        ((double)Math.Abs(wordmarkWidth - textWidth)).Should().BeLessThanOrEqualTo(tolerance, "gleiche Wortbreite wie mit echtem „o“");
    }

    private static bool VisibleProductName(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("<!--", StringComparison.Ordinal)
            || trimmed.StartsWith("*", StringComparison.Ordinal) || trimmed.StartsWith("using ", StringComparison.Ordinal)
            || trimmed.StartsWith("namespace ", StringComparison.Ordinal)
            || trimmed.Contains("FormerProductName =", StringComparison.Ordinal))
        {
            return false;
        }

        // In Zeichenketten bzw. XAML-Attributen: „PhotoViewer“ als Wort, nicht als Bezeichner PhotoViewerV3.*/Pfad
        var text = line.Replace("PhotoViewerV3", string.Empty, StringComparison.Ordinal)
            .Replace("AddPhotoViewerServices", string.Empty, StringComparison.Ordinal);
        return text.Contains("PhotoViewer", StringComparison.Ordinal)
               && !text.Contains("PhotoViewer V2", StringComparison.Ordinal);
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static List<Image> FindImages(DependencyObject root)
    {
        var found = new List<Image>();
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is Image image)
            {
                found.Add(image);
            }

            found.AddRange(FindImages(child));
        }

        return found;
    }

    private static double Distance(Color a, Color b)
        => Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.B - b.B, 2));

    private sealed record Sample(Color Left, Color Right, Color LensCenter, int InkWidth);

    /// <summary>Schriftzug mit Symbol auf Mica-Grund rendern; links/rechts = mittlere Farbe der äußersten Tintenspalten.</summary>
    private static Sample Render(bool dark, double fontSize, string pngPath) => TestUiThread.Run(
        () =>
        {
            var background = dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3);
            var dictionary = new LumoraBrandDictionary();
            dictionary.Detach(); // feste Palette für diesen Render
            dictionary.Apply(dark ? ApplicationTheme.Dark : ApplicationTheme.Light);
            var mark = new LumoraWordmark { FontSize = fontSize, VerticalAlignment = VerticalAlignment.Center };
            var iconSize = Math.Round(fontSize * 1.25);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16) };
            row.Children.Add(new Image
            {
                Source = iconSize < LumoraIconImages.SmallVariantBelow ? LumoraIconImages.Small : LumoraIconImages.Full,
                Width = iconSize,
                Height = iconSize,
                Margin = new Thickness(0, 0, fontSize * 0.4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(mark);
            var host = new Border { Background = new SolidColorBrush(background), Child = row };
            host.Resources.MergedDictionaries.Add(dictionary);

            host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            host.Arrange(new Rect(host.DesiredSize));
            host.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth), (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(pngPath))
            {
                encoder.Save(file);
            }

            // Nur den Schriftzug auswerten: eigener Schriftzug ohne Hintergrund unter demselben Wörterbuch
            var inkMark = new LumoraWordmark { FontSize = fontSize };
            var inkHost = new Border { Child = inkMark };
            inkHost.Resources.MergedDictionaries.Add(dictionary);
            var ink = RenderInk(inkHost);
            var lensRect = inkMark.Lens.TransformToAncestor(inkHost).TransformBounds(new Rect(inkMark.Lens.RenderSize));
            var lensCenter = ink.Pixels.At((int)(lensRect.X + (lensRect.Width / 2)), (int)(lensRect.Y + (lensRect.Height / 2)));
            return new Sample(ink.ColumnColor(ink.FirstColumn + 1), ink.ColumnColor(ink.LastColumn - 1), lensCenter, ink.InkWidth);
        },
        Timeout);

    private sealed record Ink(PixelBuffer Pixels, int FirstColumn, int LastColumn)
    {
        public int InkWidth => LastColumn - FirstColumn + 1;

        /// <summary>Mittlere Farbe der deckenden Pixel einer Spalte.</summary>
        public Color ColumnColor(int x)
        {
            long r = 0, g = 0, b = 0, n = 0;
            for (var y = 0; y < Pixels.Height; y++)
            {
                var c = Pixels.At(x, y);
                if (c.A > 200)
                {
                    r += c.R;
                    g += c.G;
                    b += c.B;
                    n++;
                }
            }

            return n == 0 ? Colors.Transparent : Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
        }

        /// <summary>Oberste/unterste Zeile mit Tinte im Spaltenbereich.</summary>
        public (double Top, double Bottom) Rows(int from, int to)
        {
            int top = int.MaxValue, bottom = -1;
            for (var y = 0; y < Pixels.Height; y++)
            {
                for (var x = Math.Max(0, from); x <= Math.Min(Pixels.Width - 1, to); x++)
                {
                    if (Pixels.At(x, y).A > 100)
                    {
                        top = Math.Min(top, y);
                        bottom = Math.Max(bottom, y);
                    }
                }
            }

            return (top, bottom);
        }
    }

    /// <summary>Element allein (durchsichtig) rendern und die Tintenspalten bestimmen.</summary>
    private static Ink RenderInk(FrameworkElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();
        element.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        var width = (int)Math.Ceiling(element.ActualWidth);
        var height = (int)Math.Ceiling(element.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = Pixels(bitmap);
        int first = -1, last = -1;
        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
            {
                if (pixels.At(x, y).A > 100)
                {
                    if (first < 0)
                    {
                        first = x;
                    }

                    last = x;
                    break;
                }
            }
        }

        return new Ink(pixels, first, last);
    }
}
