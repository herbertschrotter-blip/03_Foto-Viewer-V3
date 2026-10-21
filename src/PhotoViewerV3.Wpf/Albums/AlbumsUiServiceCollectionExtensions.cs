using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PhotoViewerV3.Services.Albums;

namespace PhotoViewerV3.Albums;

/// <summary>
/// Alben in der Oberfläche (ADR-0054): Auswerter der intelligenten Alben (Galerie-SQL hinter dem Services-Vertrag
/// <see cref="ISmartAlbumEvaluator"/>), <see cref="AlbumActions"/> für Galerie und Ordner-Seite, Titelbilder, Seite „Alben“
/// und die Alben-Liste der Ordner-Seite. Eine Zeile in <c>App.ConfigureServices</c>: <c>services.AddAlbumsUi();</c>.
/// </summary>
public static class AlbumsUiServiceCollectionExtensions
{
    public static IServiceCollection AddAlbumsUi(this IServiceCollection services)
    {
        services.TryAddSingleton<ISmartAlbumEvaluator, GallerySmartAlbumEvaluator>();
        services.TryAddSingleton<AlbumActions>();
        services.TryAddSingleton<AlbumCoverProvider>();
        services.TryAddSingleton<AlbumsViewModel>();
        services.TryAddSingleton<AlbumsPage>();
        services.TryAddSingleton<FolderPageAlbumsViewModel>();
        return services;
    }
}
