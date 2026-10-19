using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PhotoViewerV3.Services.Watch;

/// <summary>DI-Registrierung der Ordnerüberwachung (wird aus AddPhotoViewerServices mit einer Zeile aufgerufen).</summary>
public static class WatchServiceCollectionExtensions
{
    public static IServiceCollection AddWatch(this IServiceCollection services)
    {
        services.TryAddSingleton<IWatchSettingsStore, WatchSettingsStore>();
        // Singleton + IDisposable: der Container beendet die Überwachung beim Herunterfahren des Hosts.
        services.TryAddSingleton<ILibraryWatchService, LibraryWatchService>();
        return services;
    }
}
