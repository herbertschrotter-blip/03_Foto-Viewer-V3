using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lumora.Shell.Branding;

/// <summary>
/// Fenster-Symbol aller App-Fenster (Taskleiste, Alt+Tab): <c>Lumora.ico</c> als eingebettete Ressource. Die
/// <see cref="IconBitmapDecoder"/>-Rahmen lässt WPF für kleines und großes Symbol jeweils die passende Größe wählen.
/// Fehlt die Ressource oder ist sie nicht lesbar, bleibt das Fenster beim Symbol der EXE (kein Fehler).
/// </summary>
public static class LumoraAppIcon
{
    /// <summary>LogicalName der eingebetteten ICO-Datei (siehe Lumora.Wpf.csproj).</summary>
    public const string ResourceName = "Lumora.ico";

    private static readonly Lazy<ImageSource?> Icon = new(Load);

    /// <summary>Symbol für <c>Window.Icon</c>, null wenn nicht verfügbar.</summary>
    public static ImageSource? WindowIcon => Icon.Value;

    /// <summary>Eingebettete ICO-Datei öffnen (null, wenn sie beim Bau fehlte).</summary>
    public static Stream? OpenIco() => typeof(LumoraAppIcon).Assembly.GetManifestResourceStream(ResourceName);

    private static ImageSource? Load()
    {
        try
        {
            using var stream = OpenIco();
            if (stream is null)
            {
                return null;
            }

            var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            if (frame.CanFreeze)
            {
                frame.Freeze();
            }

            return frame;
        }
        catch (Exception)
        {
            return null; // Symbol ist Kosmetik — Fenster öffnen sich trotzdem
        }
    }
}
