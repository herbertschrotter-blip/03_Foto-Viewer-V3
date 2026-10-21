using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Lightbox;
using Lumora.Shell;
using Lumora.Shell.Dialogs;
using Lumora.Wpf.Tests.Fixtures;
using Wpf.Ui.Controls;

namespace Lumora.Wpf.Tests.Shell;

/// <summary>
/// Offener Punkt aus 0.2.11: Wird ein Fenster mit WPF-UI-<see cref="TitleBar"/> nach <c>Loaded</c>, aber vor dem
/// ersten <c>ContentRendered</c> geschlossen (Rauchtest: Lightbox öffnen und binnen ~100 ms schließen), wirft
/// <c>TitleBar.OnWindowContentRendered</c> „Hwnd mit dem Wert Null ist ungültig“. <see cref="AppFluentWindow"/>
/// verwirft das ContentRendered eines geschlossenen Fensters; die Tests öffnen und schließen deshalb sofort und
/// erwarten keine Ausnahme auf dem UI-Thread.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class EarlyCloseWindowTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public void Kontrolle_reines_FluentWindow_mit_TitleBar_wirft_beim_Schliessen_vor_dem_Zeichnen()
    {
        // Belegt das Verhalten von WPF-UI 4.0.2. Schlägt dieser Test nach einem Update fehl, ist der Fehler in der
        // Bibliothek behoben und AppFluentWindow.OnContentRendered kann entfallen.
        var errors = OpenAndCloseBeforeFirstRender(() => new FluentWindow { Content = WithTitleBar() }, modal: false);

        errors.Should().ContainSingle()
            .Which.Should().BeOfType<ArgumentException>();
    }

    [Fact]
    public void AppFluentWindow_mit_TitleBar_schliesst_vor_dem_Zeichnen_ohne_Ausnahme()
    {
        var errors = OpenAndCloseBeforeFirstRender(() => new AppFluentWindow { Content = WithTitleBar() }, modal: false);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Lightbox_oeffnen_und_sofort_schliessen_ohne_Ausnahme()
    {
        var errors = OpenAndCloseBeforeFirstRender(
            () =>
            {
                var viewModel = new LightboxViewModel(
                    new LibVlcHost(NullLogger<LibVlcHost>.Instance),
                    contextFactory: null!,
                    NullLogger<LightboxViewModel>.Instance);
                return new LightboxWindow(viewModel, NullLogger<LightboxWindow>.Instance);
            },
            modal: false);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void AppDialogWindow_modal_oeffnen_und_sofort_schliessen_ohne_Ausnahme()
    {
        var errors = OpenAndCloseBeforeFirstRender(
            () => new AppDialogWindow(new DialogRequest("Test", "Sofort wieder zu") { PrimaryText = "OK" }),
            modal: true);

        errors.Should().BeEmpty();
    }

    private static Grid WithTitleBar()
    {
        var grid = new Grid();
        grid.Children.Add(new TitleBar { Title = "Test" });
        return grid;
    }

    /// <summary>
    /// Öffnet das Fenster außerhalb des sichtbaren Bereichs und schließt es aus dem <c>Loaded</c> heraus mit normaler
    /// Priorität — also nachdem die TitleBar ContentRendered abonniert hat, aber bevor WPF ContentRendered (Input-
    /// Priorität) auslöst. Danach wird die Warteschlange bis ApplicationIdle abgearbeitet; Ausnahmen aus der TitleBar
    /// werden gesammelt statt den Test-UI-Thread zu beenden.
    /// </summary>
    private static List<Exception> OpenAndCloseBeforeFirstRender(Func<Window> create, bool modal)
    {
        var errors = new List<Exception>();
        var dispatcher = TestUiThread.Dispatcher;
        void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            if (e.Exception.StackTrace?.Contains("TitleBar", StringComparison.Ordinal) == true)
            {
                errors.Add(e.Exception);
                e.Handled = true;
            }
        }

        dispatcher.Invoke(() => dispatcher.UnhandledException += OnUnhandled);
        try
        {
            var renderedWhileOpen = false;
            TestUiThread.Run(
                () =>
                {
                    var window = create();
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Left = -5000;
                    window.Top = -5000;
                    window.ShowActivated = false;
                    window.ShowInTaskbar = false;

                    window.ContentRendered += (_, _) => renderedWhileOpen |= new WindowInteropHelper(window).Handle != IntPtr.Zero;
                    window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, window.Close);
                    if (modal)
                    {
                        _ = window.ShowDialog();
                    }
                    else
                    {
                        window.Show();
                    }

                    return true;
                },
                Timeout);

            // Die verwaiste ContentRendered-Operation (Input) und alles danach abarbeiten lassen.
            dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task.Wait(Timeout).Should().BeTrue();
            renderedWhileOpen.Should().BeFalse("das Fenster soll vor dem ersten ContentRendered geschlossen werden");
        }
        finally
        {
            dispatcher.Invoke(() => dispatcher.UnhandledException -= OnUnhandled);
        }

        return errors;
    }
}
