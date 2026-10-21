using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lumora.Services.Faces;

namespace Lumora.Wpf.Tests.Motifs;

/// <summary>
/// Referenzbilder für die Prüfung der Motiv-Erkennung mit echtem Modell (ADR-0052) — ohne persönliche Fotos und ohne
/// Dateien im Repo: synthetisch gezeichnet (Hund/Katze als Emoji-Glyphe, Dokumentseite, Programmfenster) bzw. die
/// mit Windows ausgelieferten Hintergrundbilder (Sonnenuntergang über einem See), falls vorhanden.
/// Gezeichnet auf einem eigenen STA-Thread (keine Application nötig).
/// </summary>
internal static class MotifReferenceImages
{
    /// <summary>Windows-11-Hintergrund „Sunrise“: Sonne tief über einem See (Abendrot).</summary>
    public const string WindowsSunsetLake = @"C:\Windows\Web\Screen\img102.jpg";

    /// <summary>Windows-11-Hintergrund: See bei Tag mit Sonne, Ufer mit Bäumen.</summary>
    public const string WindowsDayLake = @"C:\Windows\Web\Wallpaper\ThemeC\img28.jpg";

    /// <summary>Windows-11-Hintergrund „Bloom“: abstrakte blaue Blüte.</summary>
    public const string WindowsAbstract = @"C:\Windows\Web\Wallpaper\Windows\img19.jpg";

    public static FaceImage Glyph(string text, int size = 448)
        => Render(size, size, dc =>
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, size, size));
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Emoji"), size * 0.7, Brushes.Black, 1.0);
            dc.DrawText(formatted, new Point((size - formatted.Width) / 2, (size - formatted.Height) / 2));
        });

    /// <summary>Eine Textseite (Brief) — weiß mit Zeilen schwarzer Schrift.</summary>
    public static FaceImage Document()
        => Render(420, 594, dc =>
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 420, 594));
            var y = 30.0;
            var title = new FormattedText("Rechnung Nr. 2024-117", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Arial"), 20, Brushes.Black, 1.0);
            dc.DrawText(title, new Point(30, y));
            y += 50;
            for (var i = 0; i < 26; i++)
            {
                var line = new FormattedText(
                    i % 5 == 4 ? string.Empty : "Sehr geehrte Damen und Herren, hiermit erlauben wir uns, Ihnen folgende Leistungen",
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Times New Roman"), 11, Brushes.Black, 1.0)
                { MaxTextWidth = 360 };
                dc.DrawText(line, new Point(30, y));
                y += 18;
            }
        });

    /// <summary>Ein Programmfenster wie auf einem Bildschirmfoto: Titelleiste, Menü, Knöpfe, Liste.</summary>
    public static FaceImage Screenshot()
        => Render(640, 400, dc =>
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(243, 243, 243)), null, new Rect(0, 0, 640, 400));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(32, 32, 32)), null, new Rect(0, 0, 640, 32));
            Text(dc, "Einstellungen – System", 10, 8, 12, Brushes.White);
            dc.DrawRectangle(Brushes.White, new Pen(Brushes.LightGray, 1), new Rect(0, 32, 180, 368));
            string[] menu = ["Startseite", "System", "Bluetooth und Geräte", "Netzwerk und Internet", "Personalisierung", "Apps", "Konten", "Zeit und Sprache"];
            for (var i = 0; i < menu.Length; i++)
            {
                Text(dc, menu[i], 16, 50 + i * 28, 12, Brushes.Black);
            }

            for (var i = 0; i < 6; i++)
            {
                dc.DrawRoundedRectangle(Brushes.White, new Pen(Brushes.Gainsboro, 1), new Rect(200, 50 + i * 56, 420, 48), 4, 4);
                Text(dc, $"Bildschirm und Anzeige {i + 1}", 216, 58 + i * 56, 13, Brushes.Black);
                Text(dc, "Helligkeit, Nachtmodus, Skalierung", 216, 76 + i * 56, 10, Brushes.Gray);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0, 103, 192)), null, new Rect(560, 62 + i * 56, 44, 22), 11, 11);
            }
        });

    public static FaceImage? WindowsWallpaper(string path, Func<string, int, FaceImage> load)
        => File.Exists(path) ? load(path, 448) : null;

    private static void Text(DrawingContext dc, string text, double x, double y, double size, Brush brush)
        => dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, 1.0), new Point(x, y));

    private static FaceImage Render(int width, int height, Action<DrawingContext> draw)
    {
        FaceImage? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    draw(dc);
                }

                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var rgb = new FormatConvertedBitmap(bitmap, PixelFormats.Rgb24, null, 0);
                var pixels = new byte[width * height * 3];
                rgb.CopyPixels(pixels, width * 3, 0);
                result = new FaceImage(pixels, width, height);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return error is null ? result! : throw new InvalidOperationException("Referenzbild konnte nicht gezeichnet werden.", error);
    }
}
