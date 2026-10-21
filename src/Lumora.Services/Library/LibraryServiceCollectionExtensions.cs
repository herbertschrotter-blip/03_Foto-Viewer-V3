using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.Services.Library;

/// <summary>DI-Registrierung des Bibliotheks-Moduls (wird aus AddLumoraServices mit einer Zeile aufgerufen).</summary>
public static class LibraryServiceCollectionExtensions
{
    public static IServiceCollection AddLibrary(this IServiceCollection services)
    {
        services.TryAddSingleton<ILibraryService, LibraryService>();
        return services;
    }
}
