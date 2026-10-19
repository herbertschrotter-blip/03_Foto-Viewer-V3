using System;
using System.Windows.Interop;
using Wpf.Ui.Controls;

namespace PhotoViewerV3.Shell.Dialogs;

/// <summary>
/// Basis für FluentWindows der App, die schon vor dem ersten Zeichnen wieder geschlossen werden können
/// (Lightbox, <see cref="AppDialogWindow"/>).
/// <para>
/// Hintergrund: WPF stellt <c>ContentRendered</c> nach dem Laden mit Input-Priorität in die Dispatcher-Warteschlange
/// und bricht das beim Schließen nicht ab. Wird das Fenster dazwischen geschlossen, läuft das Ereignis auf einem
/// Fenster ohne Handle. Die WPF-UI-4-<see cref="TitleBar"/> hängt sich in ihrem <c>Loaded</c> an
/// <c>Window.ContentRendered</c> und ruft dort <c>HwndSource.FromHwnd(handle)</c> auf — mit Handle 0 wirft das
/// „Hwnd mit dem Wert Null ist ungültig“ (ArgumentException) in den globalen Ausnahme-Handler.
/// </para>
/// <para>
/// Abhilfe zentral an der Quelle: <see cref="OnContentRendered"/> reicht das Ereignis nicht mehr weiter, wenn das
/// Fenster bereits geschlossen ist. Kein Abonnent (TitleBar, eigener Code) sieht dann ein ContentRendered eines toten
/// Fensters; im Normalfall ändert sich nichts.
/// </para>
/// </summary>
public class AppFluentWindow : FluentWindow
{
    /// <summary>true, sobald <c>Closed</c> ausgelöst wurde.</summary>
    public bool IsClosed { get; private set; }

    protected override void OnClosed(EventArgs e)
    {
        IsClosed = true;
        base.OnClosed(e);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        if (IsClosed || new WindowInteropHelper(this).Handle == IntPtr.Zero)
        {
            return; // vor dem ersten Zeichnen geschlossen — Ereignis verwerfen (siehe Klassenkommentar)
        }

        base.OnContentRendered(e);
    }
}
