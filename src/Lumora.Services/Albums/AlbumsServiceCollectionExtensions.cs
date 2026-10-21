using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.Services.Albums;

/// <summary>
/// DI-Registrierung der Alben (ADR-0054). Wird in <c>AddLumoraServices</c> mit einer Zeile aufgerufen; den Auswerter
/// der intelligenten Alben (<see cref="ISmartAlbumEvaluator"/>) registriert das Wpf-Projekt.
/// </summary>
public static class AlbumsServiceCollectionExtensions
{
    public static IServiceCollection AddAlbums(this IServiceCollection services)
    {
        services.TryAddSingleton<IAlbumService, AlbumService>();
        services.TryAddSingleton<IFolderLinkService, FolderLinkService>();
        return services;
    }
}
