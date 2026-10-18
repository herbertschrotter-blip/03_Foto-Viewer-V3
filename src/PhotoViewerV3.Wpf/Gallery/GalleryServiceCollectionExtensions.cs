using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PhotoViewerV3.Gallery.Services;

namespace PhotoViewerV3.Gallery;

/// <summary>
/// DI-Registrierung des Galerie-Moduls (nur die neuen Hilfsdienste; GalleryPageSource,
/// GalleryViewModel und GalleryView werden weiterhin in App.xaml.cs registriert).
/// </summary>
public static class GalleryServiceCollectionExtensions
{
    public static IServiceCollection AddGallery(this IServiceCollection services)
    {
        services.TryAddSingleton<IGalleryDialogService, GalleryDialogService>();
        services.TryAddSingleton<IFolderPickerService, FolderPickerService>();
        return services;
    }
}
