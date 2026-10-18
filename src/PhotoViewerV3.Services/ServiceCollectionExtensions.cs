using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PhotoViewerV3.Data.RawSql;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Infrastructure;
using PhotoViewerV3.Services.Scanning;
using PhotoViewerV3.Services.Thumbnails;

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

        // Scanning (Data/RawSql + Services/Scanning)
        services.TryAddSingleton<BulkScanWriter>();
        services.TryAddSingleton<FtsIndexer>();
        services.TryAddSingleton<FolderEnumerator>();
        services.TryAddSingleton<IScanService, ScanService>();

        // Thumbnails — Video-Renderer (FFmpeg) ist WPF-frei und lebt hier.
        // Der Bild-Renderer (WIC/IImageThumbnailRenderer) wird vom Wpf-Projekt registriert.
        services.TryAddSingleton<IVideoThumbnailRenderer, FfmpegVideoThumbnailer>();
        services.TryAddSingleton<IThumbnailService, ThumbnailService>();

        return services;
    }
}
