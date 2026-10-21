using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PhotoViewerV3.Data.RawSql;
using PhotoViewerV3.Services.Albums;
using PhotoViewerV3.Services.Archive;
using PhotoViewerV3.Services.Catalog;
using PhotoViewerV3.Services.Categories;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Database.Relocation;
using PhotoViewerV3.Services.Duplicates;
using PhotoViewerV3.Services.Faces;
using PhotoViewerV3.Services.Faces.Assignment;
using PhotoViewerV3.Services.Faces.Erase;
using PhotoViewerV3.Services.Infrastructure;
using PhotoViewerV3.Services.Library;
using PhotoViewerV3.Services.Media;
using PhotoViewerV3.Services.Metadata;
using PhotoViewerV3.Services.Motifs;
using PhotoViewerV3.Services.Operations;
using PhotoViewerV3.Services.Places;
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
        services.AddCatalogContext(); // aktiver Katalog (normal/versteckt), Cache-/Temp-Ordner (ADR-0049)

        // Database
        services.TryAddSingleton<SqlitePragmaInterceptor>();
        services.TryAddSingleton<ILibraryContextFactory, LibraryContextFactory>();
        services.TryAddSingleton<DatabaseInitializer>();

        // Medienformate (Scanner, Renderer, Settings) — Factory, weil Microsoft.DI sonst den Konstruktor
        // (IEnumerable<string>, IEnumerable<string>) mit zwei LEEREN Listen wählt und kein Format erkannt wird.
        services.TryAddSingleton<IMediaFormatRegistry>(_ => new MediaFormatRegistry());

        // Bibliotheken (Services/Library)
        services.AddLibrary();
        services.AddLibraryScoping(); // Bibliotheks-Trennung: Bereich, Gruppen, Verknüpfen (ADR-0048)

        // Scanning (Data/RawSql + Services/Scanning)
        services.TryAddSingleton<BulkScanWriter>();
        services.TryAddSingleton<FtsIndexer>();
        services.TryAddSingleton<FolderEnumerator>();
        services.TryAddSingleton<IScanGate, ScanGate>(); // nie zwei Scans derselben Bibliothek, nie Scan neben Datei-Batch
        services.TryAddSingleton<IOwnChangeRegistry>(_ => new OwnChangeRegistry()); // eigene Schreibvorgänge für die Ordnerüberwachung
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
        services.AddCategories(); // frei konfigurierbare Kategorien mit Werten (ADR-0027)
        services.AddFolderAdoption(); // Ordner in die Bibliothek übernehmen, Ordner bewerten (ADR-0034)
        services.AddCaptureDates(); // Aufnahmedatum: EXIF/Video/Dateiname, Nachtragen, „Datum setzen…“ (ADR-0046)
        services.AddPlaces(); // Orte: GPS lesen/schreiben, Ortsnamen offline (GeoNames), Karte/Filter (ADR-0050)

        // Ordnerüberwachung (Services/Watch): FileSystemWatcher je Wurzel → Incremental-Scan nach Ruhezeit
        services.AddWatch();

        // Speicherort von Katalog-DB und Thumbnails ändern (Services/Database/Relocation, ADR-0026)
        services.AddDatabaseRelocation();

        // Gesichtserkennung Teil 1: Modell, ONNX-Analysator, Hintergrund-Index (Services/Faces, ADR-0035)
        services.AddFaces();
        services.AddFaceDataEraser(); // „Gesichtsdaten löschen“ je Bibliothek (Services/Faces/Erase, 0.2.38)

        // Gesichtserkennung Teil 3: Zuordnung, Vorschläge, Gruppen, Gesichts-Ausschnitte (Services/Faces/Assignment, ADR-0037)
        services.AddFaceRecognition();

        // Motive: SigLIP 2 lokal, Begriffe, Treffer, Hintergrund-Index, GPU-Sperre mit den Gesichtern (Services/Motifs, ADR-0052)
        services.AddMotifs();

        // Doppelte erkennen: Prüfsummen, Bild-Fingerabdrücke, Gruppen, Aufräumen mit Rückgängig (Services/Duplicates, ADR-0053)
        services.AddDuplicates();
        // Alben und Ordner-Verknüpfungen: Fotos verknüpfen statt kopieren (Services/Albums, ADR-0054)
        services.AddAlbums();

        return services;
    }
}
