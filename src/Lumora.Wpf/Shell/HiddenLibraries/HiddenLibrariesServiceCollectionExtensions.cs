using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Lumora.Services.HiddenLibraries;
using Lumora.Services.Infrastructure;

namespace Lumora.Shell.HiddenLibraries;

/// <summary>Was eine Katalog-Sitzung über sich wissen muss (ADR-0049).</summary>
/// <param name="Sessions">Der Sitzungsverwalter des Prozesses (in jedem Container dieselbe Instanz).</param>
/// <param name="IsHidden">Container einer versteckten Bibliothek (Pfade = <c>.lumora</c>).</param>
/// <param name="NormalAppDataRoot">App-Wurzel des normalen Katalogs (Gesichtsmodelle).</param>
public sealed record CatalogSessionSetup(ICatalogSessionManager Sessions, bool IsHidden, string NormalAppDataRoot);

/// <summary>DI der Katalog-Sitzung und der versteckten Bibliotheken (ADR-0049); eine Zeile in <c>App.ConfigureServices</c>.</summary>
public static class HiddenLibrariesServiceCollectionExtensions
{
    /// <summary>Vor <c>AddLumoraServices</c> aufrufen (dort TryAdd für Katalog-Kontext, Umzug, Modelle).</summary>
    public static IServiceCollection AddCatalogSession(this IServiceCollection services, IAppDataPaths paths, CatalogSessionSetup setup)
    {
        services.AddSingleton(setup.Sessions);
        if (setup.IsHidden)
        {
            services.AddHiddenCatalog(paths, setup.NormalAppDataRoot);
        }

        services.TryAddSingleton<IHiddenLibraryDialogs, HiddenLibraryDialogs>();
        services.AddSingleton<HiddenLibrariesViewModel>();
        return services;
    }
}
