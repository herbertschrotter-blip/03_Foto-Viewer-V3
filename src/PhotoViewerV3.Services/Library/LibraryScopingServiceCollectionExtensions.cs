using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PhotoViewerV3.Services.Library;

/// <summary>DI-Registrierung der Bibliotheks-Trennung (ADR-0048): Bereichsdienst, Verknüpfen, Katalogart.</summary>
public static class LibraryScopingServiceCollectionExtensions
{
    public static IServiceCollection AddLibraryScoping(this IServiceCollection services)
    {
        // Katalogart (versteckt ja/nein) kommt aus der Katalog-Sitzung: ICatalogContext (Services/Infrastructure, ADR-0049)
        services.TryAddSingleton<ILibraryScope, LibraryScope>();
        services.TryAddSingleton<ILibraryLinkService, LibraryLinkService>();
        return services;
    }
}
