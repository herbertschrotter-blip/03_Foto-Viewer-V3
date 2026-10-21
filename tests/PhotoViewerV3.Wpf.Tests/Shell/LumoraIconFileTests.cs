using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoViewerV3.Shell.Branding;
using PhotoViewerV3.Shell.Dialogs;
using PhotoViewerV3.Wpf.Tests.Fixtures;
using Xunit.Abstractions;

namespace PhotoViewerV3.Wpf.Tests.Shell;

/// <summary>
/// App-Symbol Lumora.ico (ADR-0047): enthält alle Größen 16–256 px, entspricht den aus <c>LumoraIcon.xaml</c> gerenderten
/// Rahmen (reproduzierbar) und ist eingebettet (Fenster-Symbol). Mit <c>LUMORA_WRITE_ICON=1</c> schreibt der Test die
/// Datei neu (<c>build/make-icon.ps1</c>).
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class LumoraIconFileTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void Ico_enthaelt_alle_Groessen_und_entspricht_der_Vektorquelle()
    {
        var path = Path.Combine(RepoRoot(), "src", "PhotoViewerV3.Wpf", "Shell", "Branding", "Lumora.ico");
        if (Environment.GetEnvironmentVariable("LUMORA_WRITE_ICON") == "1")
        {
            TestUiThread.Run(
                () =>
                {
                    using var file = File.Create(path);
                    LumoraIconRenderer.WriteIco(file);
                    return true;
                },
                Timeout);
            output.WriteLine($"geschrieben: {path} ({new FileInfo(path).Length} Bytes)");
        }

        File.Exists(path).Should().BeTrue("Lumora.ico ist eingecheckt (build/make-icon.ps1)");
        using (var stream = File.OpenRead(path))
        {
            LumoraIconRenderer.ReadIcoSizes(stream).Should().Equal(LumoraIconRenderer.IcoSizes);
        }

        var differences = TestUiThread.Run(
            () =>
            {
                using var stream = File.OpenRead(path);
                var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                decoder.Frames.Select(f => f.PixelWidth).Should().BeEquivalentTo(LumoraIconRenderer.IcoSizes);
                return decoder.Frames.Select(f => (Size: f.PixelWidth, Diff: MeanDifference(f, LumoraIconRenderer.Render(f.PixelWidth)))).ToList();
            },
            Timeout);

        foreach (var (size, diff) in differences)
        {
            output.WriteLine($"{size,3} px: mittlere Abweichung {diff:N3}");
            diff.Should().BeLessThan(1.0, $"Rahmen {size} px entspricht der Vektorquelle (sonst build/make-icon.ps1 ausführen)");
        }
    }

    [Fact]
    public void Kleine_Groessen_nutzen_die_vereinfachte_Variante()
    {
        TestUiThread.Run(
            () =>
            {
                LumoraIconImages.DrawingFor(16).Should().BeSameAs(LumoraIconImages.SmallDrawing);
                LumoraIconImages.DrawingFor(24).Should().BeSameAs(LumoraIconImages.SmallDrawing);
                LumoraIconImages.DrawingFor(31).Should().BeSameAs(LumoraIconImages.SmallDrawing);
                LumoraIconImages.DrawingFor(32).Should().BeSameAs(LumoraIconImages.FullDrawing);
                LumoraIconImages.FullDrawing.Bounds.Should().Be(new System.Windows.Rect(0, 0, 128, 128));
                LumoraIconImages.SmallDrawing.Bounds.Should().Be(new System.Windows.Rect(0, 0, 128, 128));

                // 16 px: Kachel deckend, Ecken durchsichtig, Funke rechts oben hell (Bernstein/Gelb), Foto unten hell
                var small = Pixels(LumoraIconRenderer.Render(16));
                small.At(0, 0).A.Should().BeLessThan(80, "abgerundete Ecke");
                small.At(8, 2).A.Should().Be(255);
                var spark = small.At(12, 3);
                ((int)spark.R).Should().BeGreaterThan(spark.B + 60, "Funke ist warm (Bernstein)");
                return true;
            },
            Timeout);
    }

    [Fact]
    public void Ico_ist_eingebettet_und_Fenster_bekommen_das_Symbol()
    {
        using (var embedded = LumoraAppIcon.OpenIco())
        {
            embedded.Should().NotBeNull("Lumora.ico ist EmbeddedResource");
            var file = File.ReadAllBytes(Path.Combine(RepoRoot(), "src", "PhotoViewerV3.Wpf", "Shell", "Branding", "Lumora.ico"));
            using var copy = new MemoryStream();
            embedded!.CopyTo(copy);
            copy.ToArray().Should().Equal(file);
        }

        TestUiThread.Run(
            () =>
            {
                var icon = LumoraAppIcon.WindowIcon;
                icon.Should().BeAssignableTo<BitmapFrame>();
                ((BitmapFrame)icon!).Decoder.Should().BeOfType<IconBitmapDecoder>("WPF wählt dann je Symbolgröße den passenden Rahmen");

                var window = new AppFluentWindow();
                try
                {
                    window.Icon.Should().BeSameAs(icon);
                }
                finally
                {
                    window.Close();
                }

                return true;
            },
            Timeout);
    }

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PhotoViewerV3.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo-Wurzel (PhotoViewerV3.slnx) nicht gefunden");
    }

    internal static PixelBuffer Pixels(BitmapSource source)
    {
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = bgra.PixelWidth * 4;
        var data = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(data, stride, 0);
        return new PixelBuffer(data, bgra.PixelWidth, bgra.PixelHeight);
    }

    private static double MeanDifference(BitmapSource a, BitmapSource b)
    {
        var pa = Pixels(a);
        var pb = Pixels(b);
        pa.Width.Should().Be(pb.Width);
        long sum = 0;
        for (var i = 0; i < pa.Data.Length; i++)
        {
            sum += Math.Abs(pa.Data[i] - pb.Data[i]);
        }

        return (double)sum / pa.Data.Length;
    }

    /// <summary>BGRA-Pixel (nicht vormultipliziert).</summary>
    internal sealed record PixelBuffer(byte[] Data, int Width, int Height)
    {
        public Color At(int x, int y)
        {
            var i = ((y * Width) + x) * 4;
            return Color.FromArgb(Data[i + 3], Data[i + 2], Data[i + 1], Data[i]);
        }
    }
}
