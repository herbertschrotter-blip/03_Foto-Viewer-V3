using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.Gallery.Archives;

/// <summary>
/// DI-Registrierung der Archiv-Oberfläche (Wpf/Gallery/Archives: Entpacken-Dialog). Wird von <c>AddGallery()</c> mit
/// einer Zeile aufgerufen; TryAdd, damit der Rauchtest vorab einen Ersatz setzen kann.
/// </summary>
public static class GalleryArchiveServiceCollectionExtensions
{
    public static IServiceCollection AddGalleryArchives(this IServiceCollection services)
    {
        services.TryAddSingleton<IArchiveExtractDialogService, ArchiveExtractDialogService>();
        return services;
    }
}
