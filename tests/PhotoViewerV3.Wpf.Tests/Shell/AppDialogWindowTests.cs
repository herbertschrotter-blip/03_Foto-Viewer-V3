using System.Diagnostics;
using System.Windows.Threading;
using PhotoViewerV3.Shell;
using PhotoViewerV3.Shell.Dialogs;
using PhotoViewerV3.Wpf.Tests.Fixtures;

namespace PhotoViewerV3.Wpf.Tests.Shell;

/// <summary>
/// Regression zu v0.2.0: Die Namensabfrage „Neue Bibliothek“ (langer Pfad + Eingabefeld) blieb mit der
/// Wpf.Ui MessageBox in einer Layout-Schleife hängen (Dialog ohne Knöpfe, CPU voll, Speicher wächst).
/// Das eigene <see cref="AppDialogWindow"/> muss sich öffnen, bedienen und schließen lassen.
/// </summary>
public sealed class AppDialogWindowTests
{
    private const string LongFolder = @"D:\OneDrive\Dokumente\02 Arbeit\01 Projekte\25-05 Office Lights Grenzgasse\02 Fotos";

    [Fact]
    public void Namensabfrage_mit_langem_Pfad_und_Eingabefeld_schliesst_mit_Primary_und_liefert_den_Text()
    {
        var request = new DialogRequest("Neue Bibliothek", $"Ordner: {LongFolder}\n\nName der Bibliothek:")
        {
            PrimaryText = "Anlegen",
            InputInitialValue = "02 Fotos",
        };

        var (outcome, elapsed) = ShowAndFinish(request, DialogChoice.Primary);

        outcome.Choice.Should().Be(DialogChoice.Primary);
        outcome.InputText.Should().Be("02 Fotos");
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Drei_Knopf_Dialog_liefert_Secondary()
    {
        var request = new DialogRequest(
            "Ungewöhnlich viele Dateien fehlen",
            "Beim Scan wurden 142.381 fehlende Dateien erkannt. Das kann passieren, wenn ein Laufwerk nicht vollständig verfügbar ist.")
        {
            PrimaryText = "Erneut prüfen",
            SecondaryText = "Als fehlend markieren",
            SecondaryIsDestructive = true,
        };

        var (outcome, _) = ShowAndFinish(request, DialogChoice.Secondary);

        outcome.Choice.Should().Be(DialogChoice.Secondary);
        outcome.InputText.Should().BeNull("ohne Eingabefeld gibt es keinen Text");
    }

    [Fact]
    public void Fehlerdialog_mit_vielen_Detailzeilen_schliesst_mit_None()
    {
        var details = string.Join(
            Environment.NewLine,
            Enumerable.Range(1, 200).Select(i => System.IO.Path.Combine(LongFolder, "IMG_" + i.ToString("0000") + ".jpg") + ": Ziel existiert bereits"));
        var request = new DialogRequest("Verschieben: 200 Fehler", "200 von 200 Datei(en) konnten nicht verarbeitet werden.")
        {
            CloseText = "Schließen",
            Details = details,
        };

        var (outcome, elapsed) = ShowAndFinish(request, DialogChoice.None);

        outcome.Choice.Should().Be(DialogChoice.None);
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    private static (DialogOutcome Outcome, TimeSpan Elapsed) ShowAndFinish(DialogRequest request, DialogChoice finishWith)
    {
        var sw = Stopwatch.StartNew();
        var outcome = TestUiThread.Run(() =>
        {
            var window = new AppDialogWindow(request);
            // Erst wenn das Fenster fertig gelayoutet ist (ContextIdle), wird der Knopf „gedrückt“ —
            // hängt das Layout, kommt dieser Aufruf nie und der Test läuft in den Timeout.
            window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => window.Finish(finishWith));
            _ = window.ShowDialog();
            return window.Outcome;
        }, TimeSpan.FromSeconds(15));
        sw.Stop();
        return (outcome, sw.Elapsed);
    }
}
