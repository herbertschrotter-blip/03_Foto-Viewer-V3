using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.Services.Database.Relocation;

/// <summary>DI-Registrierung des DB-Umzugs (ADR-0026; wird aus AddLumoraServices mit einer Zeile aufgerufen).</summary>
public static class RelocationServiceCollectionExtensions
{
    public static IServiceCollection AddDatabaseRelocation(this IServiceCollection services)
    {
        services.TryAddSingleton<IStorageProbe, StorageProbe>();
        services.TryAddSingleton<IDatabaseRelocationService, DatabaseRelocationService>();
        return services;
    }
}
