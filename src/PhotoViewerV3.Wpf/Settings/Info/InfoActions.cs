using System.Diagnostics;
using System.IO;
using System.Windows;
using PhotoViewerV3.Shell.Dialogs;

namespace PhotoViewerV3.Settings.Info;

/// <summary>Seiteneffekte von Einstellungen › Info (Explorer, Datei öffnen, Zwischenablage, Textdialog) — in Tests ersetzbar.</summary>
public interface IInfoActions
{
    /// <summary>Datei im Explorer markieren; fehlt sie noch, ihren Ordner öffnen (nie etwas anlegen).</summary>
    void RevealFile(string path);

    /// <summary>Ordner im Explorer öffnen (wird bei Bedarf angelegt, wie in Speicher &amp; Wartung).</summary>
    void OpenFolder(string path);

    /// <summary>Datei mit dem zugeordneten Programm öffnen (sonst Editor).</summary>
    void OpenFile(string path);

    void CopyText(string text);

    /// <summary>Langen Text in einem schlichten App-Dialog (scrollbar) zeigen.</summary>
    void ShowText(string title, string message, string details);
}

/// <summary>Produktive Umsetzung von <see cref="IInfoActions"/> (UI-Thread).</summary>
public sealed class InfoActions : IInfoActions
{
    public void RevealFile(string path)
    {
        if (File.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            return;
        }

        // Katalog noch nicht angelegt: nur den Ordner zeigen — kein Verzeichnis unter dem Dateinamen anlegen
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            return;
        }

        throw new DirectoryNotFoundException($"{path} existiert noch nicht.");
    }

    public void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    public void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Keine Zuordnung für .md: im Editor öffnen
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
    }

    public void CopyText(string text) => Clipboard.SetText(text);

    public void ShowText(string title, string message, string details)
    {
        var owner = Application.Current?.MainWindow is { IsVisible: true } main ? main : null;
        _ = AppDialogWindow.Show(owner, new DialogRequest(title, message) { Details = details, CloseText = "Schließen" });
    }
}
