using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Lumora.Shell;
using Lumora.Shell.Dialogs;
using Lumora.Wpf.Tests.Fixtures;

namespace Lumora.Wpf.Tests.Shell;

/// <summary>
/// Fenster mit Höhe aus dem Inhalt (Dialoge) dürfen nicht höher sein als ihr Inhalt: Der FluentWindow-Stil von WPF-UI
/// setzt eine Mindesthöhe von 320 px, die unter der Knopfleiste leeren Raum ließ (0.4.13).
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ContentSizedWindowHeightTests
{
    [Fact]
    public void Kurzer_Entfernen_Dialog_ist_so_hoch_wie_sein_Inhalt()
    {
        var request = new DialogRequest(
            "Bibliothek aus dem Katalog entfernen?",
            "„Sonstiges“ wird mit allen Katalogdaten (Ordner, Dateien, Vorschaubilder, Favoriten, Bewertungen) aus dem Katalog entfernt.\n\nDie Fotos und Videos auf der Festplatte bleiben unverändert — es werden keine Dateien gelöscht.")
        {
            PrimaryText = "Entfernen",
            PrimaryIsDestructive = true,
            CloseText = "Abbrechen",
        };

        var (window, content, minHeight) = Measure(() => new AppDialogWindow(request));

        minHeight.Should().Be(0, "die Mindesthöhe des WPF-UI-Stils gilt nicht für Dialoge mit Höhe aus dem Inhalt");
        window.Should().BeLessThan(300, "der kurze Dialog braucht deutlich weniger als die frühere Mindesthöhe von 320 px");
        window.Should().BeApproximately(content, 2, "unter dem Inhalt bleibt kein leerer Raum");
    }

    [Fact]
    public void Eigene_Mindesthoehe_aus_XAML_oder_Code_bleibt_erhalten()
    {
        var (_, _, minHeight) = Measure(() => new AppFluentWindow
        {
            SizeToContent = SizeToContent.Height,
            Width = 300,
            MinHeight = 200,
            Content = new Border { Height = 50 },
        });

        minHeight.Should().Be(200);
    }

    [Fact]
    public void Fenster_mit_fester_Hoehe_behalten_die_Mindesthoehe_des_Stils()
    {
        var (_, _, minHeight) = Measure(() => new AppFluentWindow { Width = 400, Height = 400, Content = new Border() });

        minHeight.Should().BeGreaterThan(0, "nur Fenster mit Höhe aus dem Inhalt verlieren die Mindesthöhe");
    }

    private static (double Window, double Content, double MinHeight) Measure(Func<Window> create)
    {
        return TestUiThread.Run(() =>
        {
            var window = create();
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -5000;
            window.Top = -5000;
            window.ShowActivated = false;
            (double, double, double) result = default;
            window.ContentRendered += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            {
                var root = (FrameworkElement)window.Content;
                result = (window.ActualHeight, root.DesiredSize.Height, window.MinHeight);
                window.Close();
            });
            _ = window.ShowDialog();
            return result;
        }, TimeSpan.FromSeconds(20));
    }
}
