using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PhotoViewerV3.Services.Archive;

/// <summary>
/// DI-Registrierung des Archiv-Moduls (Services/Archive, ADR-0022). Wird in <c>AddPhotoViewerServices</c> mit
/// einer Zeile aufgerufen. TryAdd, damit Tests oder der Rauchtest eigene Implementierungen vorab setzen können.
/// </summary>
public static class ArchiveServiceCollectionExtensions
{
    public static IServiceCollection AddArchives(this IServiceCollection services)
    {
        services.TryAddSingleton<IArchiveService, ArchiveService>();
        return services;
    }
}
