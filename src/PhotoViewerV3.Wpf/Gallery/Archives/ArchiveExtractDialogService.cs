using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Gallery.Services;
using PhotoViewerV3.Services.Archive;

namespace PhotoViewerV3.Gallery.Archives;

/// <summary>Öffnet <see cref="ExtractArchiveDialog"/> modal (UI-Thread) und liefert die Ergebnisse des Laufs.</summary>
public sealed class ArchiveExtractDialogService : IArchiveExtractDialogService
{
    private readonly IArchiveService _archives;
    private readonly IFolderPickerService _folderPicker;
    private readonly ILogger<ExtractArchiveDialogViewModel> _logger;

    public ArchiveExtractDialogService(IArchiveService archives, IFolderPickerService folderPicker, ILogger<ExtractArchiveDialogViewModel> logger)
    {
        _archives = archives;
        _folderPicker = folderPicker;
        _logger = logger;
    }

    public Task<IReadOnlyList<ArchiveExtractResult>> ShowAsync(ArchiveExtractDialogRequest request, Window? owner)
    {
        var viewModel = new ExtractArchiveDialogViewModel(request, _archives, _folderPicker, _logger);
        var dialog = new ExtractArchiveDialog(viewModel);
        owner ??= ResolveOwner();
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        _ = dialog.ShowDialog();
        return Task.FromResult(viewModel.Results);
    }

    private static Window? ResolveOwner()
    {
        var app = Application.Current;
        return app?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app?.MainWindow;
    }
}
