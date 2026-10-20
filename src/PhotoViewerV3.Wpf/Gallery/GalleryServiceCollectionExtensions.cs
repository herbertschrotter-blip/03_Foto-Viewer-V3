using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PhotoViewerV3.Gallery.ArchiveTarget;
using PhotoViewerV3.Gallery.Archives;
using PhotoViewerV3.Gallery.Categories;
using PhotoViewerV3.Gallery.Data;
using PhotoViewerV3.Gallery.ListView;
using PhotoViewerV3.Gallery.Services;
using PhotoViewerV3.Gallery.Tags;
using PhotoViewerV3.Gallery.ViewModels;
using PhotoViewerV3.Gallery.Views;
using PhotoViewerV3.Services.Operations;

namespace PhotoViewerV3.Gallery;

/// <summary>
/// DI-Registrierung des Galerie-Moduls (Wpf/Gallery): Raw-SQLite-Lesepfad, ViewModel (Singleton — Galerie-Seite
/// und Ordner-Seite teilen den Zustand), View (transient, je Seite eine Instanz), Dialog- und
/// Ordnerauswahl-Dienste sowie „Ins Archiv…“ (<see cref="IArchiveMoveService"/> für Galerie und Lightbox, ADR-0020).
/// Letztere per TryAdd, damit der Rauchtest (SmokeTestRunner.ConfigureServices) seine Fakes vorher setzen kann.
/// Wird in App.xaml.cs mit einer Zeile aufgerufen.
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
        services.AddGalleryArchives();
        services.TryAddSingleton<IArchiveTargetPicker, ArchiveTargetPicker>();
        services.TryAddSingleton<IArchiveMoveService, ArchiveMoveService>();
        // Tags (ADR-0021) und Listenansicht: Tag-Auswahl-Dialog, Ansichts-Einstellungen (Grid/Liste, Spalten)
        services.TryAddSingleton<ITagPickerService, TagPickerService>();
        services.TryAddSingleton<ICategoryPickerService, CategoryPickerService>(); // Strg+K (ADR-0027)
        services.TryAddSingleton<IGalleryViewSettingsStore, GalleryViewSettingsStore>();
        return services;
    }
}
