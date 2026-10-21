using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Lumora.Services.Catalog;

namespace Lumora.Services.Operations;

/// <summary>
/// DI-Registrierung von „Ordner in die Bibliothek übernehmen“ und „Ordner bewerten/kategorisieren“ (ADR-0034). Wird in
/// <c>AddLumoraServices</c> mit einer Zeile aufgerufen.
/// </summary>
public static class FolderAdoptionServiceCollectionExtensions
{
    public static IServiceCollection AddFolderAdoption(this IServiceCollection services)
    {
        services.TryAddSingleton<IFolderAdoptionService, FolderAdoptionService>();
        services.TryAddSingleton<IFolderMetadataService, FolderMetadataService>();
        return services;
    }
}
