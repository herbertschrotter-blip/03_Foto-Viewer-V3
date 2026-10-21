using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace PhotoViewerV3.Gallery.Services;

/// <summary>OpenFolderDialog-Implementierung von <see cref="IFolderPickerService"/>.</summary>
public sealed class FolderPickerService : IFolderPickerService
{
    public string? PickFolder(string title, string? initialDirectory)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
            AddToRecent = false, // keine Einträge unter „Zuletzt verwendet“ (ADR-0049)
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                    ?? Application.Current?.MainWindow;
        var ok = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return ok == true ? dialog.FolderName : null;
    }
}
