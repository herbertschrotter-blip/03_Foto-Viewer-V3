using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PhotoViewerV3.Data.RawSql;
using PhotoViewerV3.Services.Archive;
using PhotoViewerV3.Services.Catalog;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Infrastructure;
using PhotoViewerV3.Services.Library;
using PhotoViewerV3.Services.Media;
using PhotoViewerV3.Services.Operations;
using PhotoViewerV3.Services.Scanning;
using PhotoViewerV3.Services.Sorter;
using PhotoViewerV3.Services.Tags;
using PhotoViewerV3.Services.Thumbnails;
using PhotoViewerV3.Services.Watch;

namespace PhotoViewerV3.Services;

/// <summary>
/// DI-Helper für die Services-Schicht. Erlaubt der Wpf-App eine einzige Zeile zur
/// Registrierung aller produktiven Services (Database, …) statt Service-by-Service.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPhotoViewerServices(this IServiceCollection services)
    {
        // Infrastructure — TryAdd damit eine bereits registrierte Instanz nicht überschrieben wird
        services.TryAddSingleton<IAppDataPaths, AppDataPaths>();

        // Database
        services.TryAddSingleton<SqlitePragmaInterceptor>();
        services.TryAddSingleton<ILibraryContextFactory, LibraryContextFactory>();
        services.TryAddSingleton<DatabaseInitializer>();

        // Medienformate (Scanner, Renderer, Settings) — Factory, weil Microsoft.DI sonst den Konstruktor
        // (IEnumerable<string>, IEnumerable<string>) mit zwei LEEREN Listen wählt und kein Format erkannt wird.
        services.TryAddSingleton<IMediaFormatRegistry>(_ => new MediaFormatRegistry());

        // Bibliotheken (Services/Library)
        services.AddLibrary();

        // Scanning (Data/RawSql + Services/Scanning)
        services.TryAddSingleton<BulkScanWriter>();
        services.TryAddSingleton<FtsIndexer>();
        services.TryAddSingleton<FolderEnumerator>();
        services.TryAddSingleton<IScanService, ScanService>();

        // Thumbnails — Video-Renderer (FFmpeg) ist WPF-frei und lebt hier; derselbe FFmpeg-Wrapper
        // dient als Fallback für Bildformate ohne WIC-Codec (WebP/HEIC/AVIF/RAW).
        // Der Bild-Renderer (WIC/IImageThumbnailRenderer) wird vom Wpf-Projekt registriert.
        services.TryAddSingleton<FfmpegVideoThumbnailer>();
        services.TryAddSingleton<IVideoThumbnailRenderer>(sp => sp.GetRequiredService<FfmpegVideoThumbnailer>());
        services.TryAddSingleton<IFallbackImageThumbnailRenderer>(sp => sp.GetRequiredService<FfmpegVideoThumbnailer>());
        services.TryAddSingleton<IThumbnailService, ThumbnailService>();

        // Datei-Operationen (Papierkorb/Move/Rename/Undo) + Katalog-Metadaten (Favorit/Bewertung)
        services.AddOperations();
        services.AddCatalog();
        services.AddSorter();
        services.AddArchives();
        services.AddTags();

        // Ordnerüberwachung (Services/Watch): FileSystemWatcher je Wurzel → Incremental-Scan nach Ruhezeit
        services.AddWatch();

        return services;
    }
}
