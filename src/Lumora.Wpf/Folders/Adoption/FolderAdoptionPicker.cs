using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Logging;
using Lumora.Services.Database;
using Lumora.Services.Operations;

namespace Lumora.Folders.Adoption;

/// <summary>
/// Auswahl für „Ordner in die Bibliothek übernehmen…“ (Zielordner, Vorschau, Optionen) hinter einem Interface, damit
/// Ordnerbaum, Bibliotheksseite und Rauchtest ohne Fenster auskommen. Liefert den berechneten Plan oder null bei Abbruch.
/// </summary>
public interface IFolderAdoptionPicker
{
    Task<FolderAdoptionChoice?> PickAsync(FolderAdoptionContext context, AdoptionSourceSnapshot snapshot);
}

/// <summary>Öffnet den <see cref="FolderAdoptionDialog"/> modal auf dem UI-Thread.</summary>
public sealed class FolderAdoptionPicker : IFolderAdoptionPicker
{
    private readonly ILibraryContextFactory _factory;
    private readonly IFileOperationService _fileOps;
    private readonly IFolderAdoptionService _adoption;
    private readonly ILogger<FolderAdoptionViewModel> _logger;

    public FolderAdoptionPicker(ILibraryContextFactory factory, IFileOperationService fileOps, IFolderAdoptionService adoption, ILogger<FolderAdoptionViewModel> logger)
    {
        _factory = factory;
        _fileOps = fileOps;
        _adoption = adoption;
        _logger = logger;
    }

    public Task<FolderAdoptionChoice?> PickAsync(FolderAdoptionContext context, AdoptionSourceSnapshot snapshot)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            return dispatcher.InvokeAsync(() => PickAsync(context, snapshot)).Task.Unwrap();
        }

        var viewModel = new FolderAdoptionViewModel(context, snapshot, _factory, _fileOps, _adoption, _logger);
        var app = Application.Current;
        var owner = app?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app?.MainWindow;
        return Task.FromResult(FolderAdoptionDialog.Show(owner, viewModel));
    }
}
