using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PhotoViewerV3.Services.Database.Relocation;

/// <summary>DI-Registrierung des DB-Umzugs (ADR-0026; wird aus AddPhotoViewerServices mit einer Zeile aufgerufen).</summary>
public static class RelocationServiceCollectionExtensions
{
    public static IServiceCollection AddDatabaseRelocation(this IServiceCollection services)
    {
        services.TryAddSingleton<IStorageProbe, StorageProbe>();
        services.TryAddSingleton<IDatabaseRelocationService, DatabaseRelocationService>();
        return services;
    }
}
