using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoViewerV3.Lightbox.Controls;
using PhotoViewerV3.Wpf.Tests.Fixtures;

namespace PhotoViewerV3.Wpf.Tests.Lightbox;

/// <summary>
/// Regression zu v0.2.3: Ein Bild, das größer als die Bühne ist, wurde von WPF erst auf den Layout-Platz
/// zugeschnitten und dann per RenderTransform verkleinert — sichtbar blieb nur die linke obere Ecke in Fit-Größe.
/// Eingepasst muss das Bild die Bühne in der Breite füllen; ein Treffertest am rechten Rand muss das Bild treffen.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ZoomPanControlTests
{
    [Fact]
    public void Grosses_Bild_fuellt_eingepasst_die_ganze_Breite_und_wird_nicht_zugeschnitten()
    {
        var result = TestUiThread.Run(() =>
        {
            var control = new ZoomPanControl();
            var window = new Window
            {
                Width = 900,
                Height = 700,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Left = -5000, // außerhalb des Sichtbereichs
                Top = -5000,
                Content = control,
            };
            window.Show();
            try
            {
                control.Source = new WriteableBitmap(4000, 2250, 96, 96, PixelFormats.Bgr32, null);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded); // Layout + Render abwarten

                var viewW = control.ActualWidth;
                var viewH = control.ActualHeight;
                var expectedScale = Math.Min(viewW / 4000, viewH / 2250);
                var rightEdge = new Point(viewW - 4, viewH / 2);
                var hit = VisualTreeHelper.HitTest(control, rightEdge)?.VisualHit;
                return (control.ZoomPercent, expectedScale * 100, hit is Image, viewW);
            }
            finally
            {
                window.Close();
            }
        }, TimeSpan.FromSeconds(15));

        result.Item4.Should().BeGreaterThan(500, "das Fenster muss eine echte Breite haben");
        result.Item1.Should().BeApproximately(result.Item2, 1.0, "Fit-Zoom = Bühne/Bildbreite");
        result.Item3.Should().BeTrue("am rechten Rand der Bühne muss das Bild sichtbar sein (kein Layout-Zuschnitt)");
    }
}
