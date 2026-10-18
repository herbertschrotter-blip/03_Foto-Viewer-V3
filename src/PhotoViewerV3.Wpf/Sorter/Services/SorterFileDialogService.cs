using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace PhotoViewerV3.Sorter.Services;

/// <summary>OpenFileDialog/SaveFileDialog-Implementierung von <see cref="ISorterFileDialogService"/>.</summary>
public sealed class SorterFileDialogService : ISorterFileDialogService
{
    public string? PickOpenFile(string title, string filter, string? initialDirectory)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            Multiselect = false,
            CheckFileExists = true,
        };
        ApplyInitialDirectory(dialog, initialDirectory);

        var ok = Show(dialog);
        return ok ? dialog.FileName : null;
    }

    public string? PickSaveFile(string title, string filter, string defaultFileName, string? initialDirectory)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = defaultFileName,
            AddExtension = true,
            OverwritePrompt = true,
        };
        ApplyInitialDirectory(dialog, initialDirectory);

        var ok = Show(dialog);
        return ok ? dialog.FileName : null;
    }

    private static void ApplyInitialDirectory(FileDialog dialog, string? initialDirectory)
    {
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }
    }

    private static bool Show(FileDialog dialog)
    {
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                    ?? Application.Current?.MainWindow;
        return (owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) == true;
    }
}
