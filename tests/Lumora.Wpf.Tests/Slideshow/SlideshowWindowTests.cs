using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Slideshow;
using Lumora.Wpf.Tests.Fixtures;
using Xunit.Abstractions;

namespace Lumora.Wpf.Tests.Slideshow;

/// <summary>
/// Diashow-Fenster (ADR-0059): öffnen und vor dem ersten Zeichnen schließen ohne „Hwnd mit dem Wert Null“, Esc schließt und
/// gibt alles frei, Überblendung zwischen zwei Ebenen, Speicher bleibt über 200 echt dekodierte Bilder stabil.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class SlideshowWindowTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private readonly ITestOutputHelper _output;

    public SlideshowWindowTests(ITestOutputHelper output) => _output = output;

    private static SlideshowViewModel CreateViewModel(IReadOnlyList<int> ids, FakeItemLoader loader, ISlideshowImageDecoder decoder, Dispatcher dispatcher)
        => new(
            new ListSlideshowSequence(ids),
            0,
            "Test",
            SlideshowSettings.Default,
            loader,
            decoder,
            video: null,
            TimeProvider.System,
            action => dispatcher.BeginInvoke(action),
            NullLogger.Instance);

    private static SlideshowWindow CreateWindow(SlideshowViewModel viewModel)
    {
        var window = new SlideshowWindow(viewModel, video: null, NullLogger.Instance)
        {
            Left = -5000,
            Top = -5000,
            Width = 1280,
            Height = 720,
            ShowActivated = false,
        };
        return window;
    }

    [Fact]
    public async Task Oeffnen_und_vor_dem_ersten_Zeichnen_schliessen_ohne_Ausnahme()
    {
        var errors = new List<Exception>();
        var dispatcher = TestUiThread.Dispatcher;
        void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            errors.Add(e.Exception);
            e.Handled = true;
        }

        dispatcher.Invoke(() => dispatcher.UnhandledException += OnUnhandled);
        try
        {
            var renderedWhileOpen = false;
            TestUiThread.Run(
                () =>
                {
                    var loader = new FakeItemLoader();
                    loader.AddImage(1);
                    var window = CreateWindow(CreateViewModel([1], loader, new FakeImageDecoder(), dispatcher));
                    window.ContentRendered += (_, _) => renderedWhileOpen |= new WindowInteropHelper(window).Handle != IntPtr.Zero;
                    window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, window.Close);
                    window.Show();
                    return true;
                },
                Timeout);

            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task.WaitAsync(Timeout);
            renderedWhileOpen.Should().BeFalse("das Fenster soll vor dem ersten ContentRendered geschlossen werden");
        }
        finally
        {
            dispatcher.Invoke(() => dispatcher.UnhandledException -= OnUnhandled);
        }

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Ohne_Show_schliessen_wirft_nicht()
    {
        TestUiThread.Run(
            () =>
            {
                var window = CreateWindow(CreateViewModel([1], new FakeItemLoader(), new FakeImageDecoder(), TestUiThread.Dispatcher));
                window.Close();
                return true;
            },
            Timeout);
    }

    [Fact]
    public async Task Esc_schliesst_und_gibt_das_ViewModel_frei_Ueberblendung_wechselt_die_Ebene()
    {
        var dispatcher = TestUiThread.Dispatcher;
        var loader = new FakeItemLoader();
        loader.AddImage(1);
        loader.AddImage(2);
        SlideshowWindow? window = null;
        SlideshowViewModel? viewModel = null;
        var closed = false;

        TestUiThread.Run(
            () =>
            {
                viewModel = CreateViewModel([1, 2], loader, new FakeImageDecoder(), dispatcher);
                window = CreateWindow(viewModel);
                window.Closed += (_, _) => closed = true;
                window.Show();
                return true;
            },
            Timeout);

        await WaitUntilAsync(() => dispatcher.Invoke(() => viewModel!.CurrentSlide is not null));
        dispatcher.Invoke(() => window!.WindowStyle).Should().Be(WindowStyle.None, "rahmenloses Vollbild auch nach dem Laden");
        var first = dispatcher.Invoke(() => window!.FrontLayer);
        dispatcher.Invoke(() => first!.Source).Should().NotBeNull();

        await dispatcher.InvokeAsync(() => viewModel!.NextAsync()).Task.Unwrap().WaitAsync(Timeout);
        var second = dispatcher.Invoke(() => window!.FrontLayer);
        second.Should().NotBeSameAs(first, "das neue Bild blendet auf der anderen Ebene ein");
        dispatcher.Invoke(() => second!.Source).Should().NotBeNull();

        dispatcher.Invoke(() => window!.HandleKey(Key.Escape)).Should().BeTrue();
        await WaitUntilAsync(() => closed);
        dispatcher.Invoke(() => viewModel!.CurrentSlide).Should().BeNull("das Fenster gibt das ViewModel beim Schließen frei");
        dispatcher.Invoke(() => window!.HandleKey(Key.F)).Should().BeTrue("F bleibt ohne Wirkung — Vollbild bleibt");
    }

    [Fact]
    public void Eingepasste_Groesse_entspricht_Uniform()
    {
        SlideshowWindow.FitSize(4000, 3000, 1920, 1080).Should().Be((1440.0, 1080.0));
        SlideshowWindow.FitSize(3000, 1000, 1920, 1080).Should().Be((1920.0, 640.0));
        SlideshowWindow.FitSize(0, 0, 1920, 1080).Should().Be((1920.0, 1080.0));
    }

    [Fact]
    public void Dekodier_Breite_fuellt_den_Bildschirm_und_nie_groesser_als_die_Quelle()
    {
        SlideshowImageDecoder.DecodeWidth(6000, 4000, 1, 4300, 2420).Should().BeInRange(3630, 3631, "Höhe begrenzt: 4000 → 2420");
        SlideshowImageDecoder.DecodeWidth(6000, 4000, 6, 4300, 2420).Should().BeInRange(2420, 2421, "hochkant gedreht: Breite der Quelle wird zur Höhe");
        SlideshowImageDecoder.DecodeWidth(800, 600, 1, 4300, 2420).Should().Be(800, "nie hochskalieren");
    }

    [Fact]
    public async Task Speicher_bleibt_ueber_200_dekodierte_Bilder_stabil()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lumora-slideshow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var paths = Enumerable.Range(0, 3).Select(i => WriteJpeg(Path.Combine(dir, $"bild{i}.jpg"), 3000, 2000, (byte)(60 * i))).ToList();
            var loader = new FakeItemLoader();
            var ids = Enumerable.Range(1, 220).ToList();
            foreach (var id in ids)
            {
                loader.Items[id] = new SlideshowItemInfo(id, paths[id % paths.Count], Domain.Enums.MediaType.Image, false, null, null, []);
            }

            var dispatcher = TestUiThread.Dispatcher;
            SlideshowViewModel? viewModel = null;
            SlideshowWindow? window = null;
            TestUiThread.Run(
                () =>
                {
                    viewModel = CreateViewModel(ids, loader, new SlideshowImageDecoder(), dispatcher);
                    window = CreateWindow(viewModel);
                    window.Show();
                    viewModel.TogglePause(); // die Zeitgeber sollen nicht dazwischenblättern
                    return true;
                },
                Timeout);
            await WaitUntilAsync(() => dispatcher.Invoke(() => viewModel!.CurrentSlide is not null));

            async Task StepAsync(int count)
            {
                for (var i = 0; i < count; i++)
                {
                    await dispatcher.InvokeAsync(() => viewModel!.NextAsync()).Task.Unwrap().WaitAsync(Timeout);
                }
            }

            await StepAsync(20); // Aufwärmen
            var before = Measure();
            var sw = Stopwatch.StartNew();
            await StepAsync(200);
            sw.Stop();
            var after = Measure();
            var slide = dispatcher.Invoke(() => viewModel!.CurrentSlide!);
            var cached = dispatcher.Invoke(() => viewModel!.CachedImageCount);
            dispatcher.Invoke(() => window!.Close());

            _output.WriteLine(
                $"200 Bilder (3000×2000 JPEG, dekodiert {slide.Image!.PixelWidth}×{slide.Image.PixelHeight}): {sw.Elapsed.TotalMilliseconds / 200:N1} ms je Bild; " +
                $"verwaltet {before.Managed / 1048576.0:N1} → {after.Managed / 1048576.0:N1} MB, privat {before.Private / 1048576.0:N1} → {after.Private / 1048576.0:N1} MB; im Cache {cached}");
            cached.Should().BeLessThanOrEqualTo(3);
            (after.Managed - before.Managed).Should().BeLessThan(64L * 1024 * 1024, "keine verwalteten Reste je Bild");
            (after.Private - before.Private).Should().BeLessThan(200L * 1024 * 1024, "200 Bilder à ≈ 8 MB dürfen sich nicht ansammeln");
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static (long Managed, long Private) Measure()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        TestUiThread.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        return (GC.GetTotalMemory(forceFullCollection: true), process.PrivateMemorySize64);
    }

    private static string WriteJpeg(string path, int width, int height, byte shade)
    {
        var stride = width * 3;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = (y * stride) + (x * 3);
                pixels[o] = (byte)(x * 255 / width);
                pixels[o + 1] = (byte)(y * 255 / height);
                pixels[o + 2] = shade;
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, stride);
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > Timeout)
            {
                throw new TimeoutException("Bedingung nicht erfüllt");
            }

            await Task.Delay(20);
        }
    }
}
