using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Services.Library;

namespace PhotoViewerV3.Shell;

/// <summary>DataContext des Hauptfensters: Statusleiste und Startseiten-Entscheidung.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly ILibraryService _libraries;
    private readonly ILogger<ShellViewModel> _logger;

    public ShellViewModel(StatusBarViewModel statusBar, ILibraryService libraries, ILogger<ShellViewModel> logger)
    {
        StatusBar = statusBar;
        _libraries = libraries;
        _logger = logger;
    }

    public StatusBarViewModel StatusBar { get; }

    /// <summary>Startseite: Galerie, wenn eine aktive Bibliothek existiert, sonst die Bibliotheksverwaltung.</summary>
    public async Task<string> ResolveStartPageAsync()
    {
        try
        {
            var active = await _libraries.GetActiveAsync().ConfigureAwait(true);
            return active is null ? NavigationTags.Library : NavigationTags.Gallery;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Aktive Bibliothek konnte nicht ermittelt werden — Start auf der Bibliotheksseite");
            return NavigationTags.Library;
        }
    }
}
