using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PhotoViewerV3.Gallery.Data;
using PhotoViewerV3.Gallery.Services;
using PhotoViewerV3.Gallery.ViewModels;
using PhotoViewerV3.Gallery.Views;

namespace PhotoViewerV3.Gallery;

/// <summary>
/// DI-Registrierung des Galerie-Moduls (Wpf/Gallery): Raw-SQLite-Lesepfad, ViewModel (Singleton — Galerie-Seite
/// und Ordner-Seite teilen den Zustand), View (transient, je Seite eine Instanz) sowie Dialog- und
/// Ordnerauswahl-Dienste. Letztere per TryAdd, damit der Rauchtest (SmokeTestRunner.ConfigureServices) seine
/// Fakes vorher setzen kann. Wird in App.xaml.cs mit einer Zeile aufgerufen.
/// </summary>
public static class GalleryServiceCollectionExtensions
{
    public static IServiceCollection AddGallery(this IServiceCollection services)
    {
        services.AddSingleton<GalleryPageSource>();
        services.AddSingleton<GalleryViewModel>();
        services.AddTransient<GalleryView>();
        services.TryAddSingleton<IGalleryDialogService, GalleryDialogService>();
        services.TryAddSingleton<IFolderPickerService, FolderPickerService>();
        return services;
    }
}
