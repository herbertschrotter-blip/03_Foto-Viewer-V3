using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Lumora.Gallery.CaptureDates;

namespace Lumora.Folders;

/// <summary>
/// „Datum setzen…“ im Kontextmenü des Ordnerbaums (ADR-0046): alle Dateien des Ordners, im Dialog wahlweise samt
/// Unterordnern. Ergebnis wie die übrigen Ordner-Aktionen in der InfoBar über dem Baum; Rückgängig in der Galerie.
/// </summary>
public sealed partial class FolderTreeViewModel
{
    private CaptureDateActions? _captureDateActions;

    private bool CanSetFolderCaptureDate(FolderNode? node) => CanRunFolderAction(node) && _captureDateActions is not null;

    [RelayCommand(CanExecute = nameof(CanSetFolderCaptureDate))]
    private Task SetFolderCaptureDateAsync(FolderNode? node)
        => node is { IsPlaceholder: false } && _captureDateActions is { } actions
            ? RunFolderActionAsync("Datum setzen", node, () => actions.SetForFolderAsync(node.Id, node.Name, ActiveWindow()))
            : Task.CompletedTask;

    private static Window? ActiveWindow()
        => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
}
