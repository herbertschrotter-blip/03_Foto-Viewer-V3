using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Gallery.Places;
using PhotoViewerV3.Places.Data;
using PhotoViewerV3.Places.Dialogs;
using PhotoViewerV3.Places.Map.Tiles;
using PhotoViewerV3.Places.Picker;
using PhotoViewerV3.Places.ViewModels;
using PhotoViewerV3.Services.Infrastructure;
using PhotoViewerV3.Services.Places;
using PhotoViewerV3.Settings.Info;

namespace PhotoViewerV3.Places;

/// <summary>
/// Seite „Orte“ (ADR-0051): Kachelquelle (OSM bzw. abgeschaltet), Kachel-Cache über <see cref="ICatalogStoragePaths"/>,
/// Katalog-Lesepfad, Vorschaubilder, Dialoge, Kartenfenster (<see cref="IMapPicker"/>), ViewModel und Seite. Eine Zeile in
/// <c>App.ConfigureServices</c>: <c>services.AddPlacesMap();</c> — nach <c>SmokeTestRunner.ConfigureServices</c> (TryAdd:
/// der Rauchtest trägt vorher seinen Dialog und die abgeschaltete Kachelquelle ein).
/// </summary>
public static class PlacesMapServiceCollectionExtensions
{
    public static IServiceCollection AddPlacesMap(this IServiceCollection services)
    {
        // Daten-Dienste (IGeoQueryService, IGeoTagService, IReverseGeocoder) registriert AddPlaces() in Services (ADR-0050)

        // Ablage außerhalb des Katalogs: ICatalogStoragePaths kommt aus der Katalog-Sitzung (ADR-0049, AddHiddenCatalog…)

        // Bereich (ILibraryScope, ADR-0048: aktive Bibliothek bzw. „Alle verknüpften“) und Datenschutz-Merkmal (ICatalogContext)
        services.TryAddSingleton<IPlacesScope, LibraryScopePlacesScope>();
        services.TryAddSingleton<IMapPrivacyContext, CatalogMapPrivacyContext>();

        // Kartenkacheln: OSM mit Platten-Cache (500 MB, LRU) — abgeschaltet mit PHOTOVIEWERV3_MAP_TILES=off und im Rauchtest
        services.TryAddSingleton(_ => MapTileOptions.FromEnvironment());
        services.TryAddSingleton<IMapTileSource>(sp =>
        {
            var options = sp.GetRequiredService<MapTileOptions>();
            if (!options.Enabled)
            {
                return new DisabledMapTileSource();
            }

            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("PhotoViewerV3.Places.Tiles");
            var cache = new MapTileDiskCache(sp.GetRequiredService<ICatalogStoragePaths>().CacheRoot, options.DiskCacheBytes, logger);
            return new OsmTileSource(options, cache, OsmTileSource.BuildUserAgent(AppInfo.Version), logger: logger);
        });
        services.TryAddSingleton<MapTileProvider>();

        services.TryAddSingleton<IPlacesCatalogReader, PlacesCatalogReader>();
        services.TryAddSingleton<IPlacesImageSource, PlacesThumbnailProvider>();
        services.TryAddSingleton<IPlacesDialogs, PlacesDialogs>();
        services.TryAddSingleton<IMapPicker, MapPickerService>();
        services.TryAddSingleton<PlacesViewModel>();
        services.TryAddSingleton<PlacesPage>();
        return services;
    }
}
