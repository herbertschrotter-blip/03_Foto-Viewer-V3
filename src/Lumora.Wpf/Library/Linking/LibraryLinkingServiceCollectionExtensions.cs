using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.Libraries.Linking;

/// <summary>DI der Oberfläche „Bibliotheken verknüpfen“ (ADR-0048, Einstellungen › Bibliotheken &amp; Eingänge).</summary>
public static class LibraryLinkingServiceCollectionExtensions
{
    public static IServiceCollection AddLibraryLinkingUi(this IServiceCollection services)
    {
        services.TryAddSingleton<ILibraryLinkDialogService, LibraryLinkDialogService>();
        return services;
    }
}
