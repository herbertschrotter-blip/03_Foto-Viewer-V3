using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PhotoViewerV3.Services.Infrastructure;

/// <summary>
/// Registriert den Katalog-Kontext der Sitzung (ADR-0049). Standard: normaler Katalog aus <see cref="IAppDataPaths"/>;
/// die Sitzung einer versteckten Bibliothek registriert vorher ihren eigenen (TryAdd lässt ihn stehen).
/// </summary>
public static class CatalogContextServiceCollectionExtensions
{
    public static IServiceCollection AddCatalogContext(this IServiceCollection services)
    {
        services.TryAddSingleton<ICatalogContext>(sp => new CatalogContext(sp.GetRequiredService<IAppDataPaths>(), isHidden: false));
        services.TryAddSingleton<ICatalogStoragePaths>(sp => sp.GetRequiredService<ICatalogContext>());
        return services;
    }
}
