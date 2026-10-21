using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.Services.Catalog;

/// <summary>
/// DI-Registrierung des Catalog-Moduls (Benutzer-Metadaten: Favorit, Bewertung). Wird in
/// <c>AddLumoraServices</c> mit einer Zeile aufgerufen.
/// </summary>
public static class CatalogServiceCollectionExtensions
{
    public static IServiceCollection AddCatalog(this IServiceCollection services)
    {
        services.TryAddSingleton<ICatalogUpdateService, CatalogUpdateService>();
        return services;
    }
}
