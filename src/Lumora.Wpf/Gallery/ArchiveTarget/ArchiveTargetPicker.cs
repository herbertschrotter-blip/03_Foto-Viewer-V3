using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Logging;
using Lumora.Services.Database;
using Lumora.Services.Operations;

namespace Lumora.Gallery.ArchiveTarget;

/// <summary>Öffnet den <see cref="ArchiveTargetDialog"/> modal auf dem UI-Thread (Owner = aktives Fenster, z. B. die Lightbox).</summary>
public sealed class ArchiveTargetPicker : IArchiveTargetPicker
{
    private readonly ILibraryContextFactory _factory;
    private readonly IFileOperationService _fileOps;
    private readonly ILogger<ArchiveTargetViewModel> _logger;

    public ArchiveTargetPicker(ILibraryContextFactory factory, IFileOperationService fileOps, ILogger<ArchiveTargetViewModel> logger)
    {
        _factory = factory;
        _fileOps = fileOps;
        _logger = logger;
    }

    public Task<string?> PickAsync(ArchiveTargetRequest request)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            return dispatcher.InvokeAsync(() => PickAsync(request)).Task.Unwrap();
        }

        var viewModel = new ArchiveTargetViewModel(request, _factory, _fileOps, _logger);
        return Task.FromResult(ArchiveTargetDialog.Show(ResolveOwner(), viewModel));
    }

    private static Window? ResolveOwner()
    {
        var app = Application.Current;
        if (app is null)
        {
            return null;
        }

        return app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app.MainWindow;
    }
}
