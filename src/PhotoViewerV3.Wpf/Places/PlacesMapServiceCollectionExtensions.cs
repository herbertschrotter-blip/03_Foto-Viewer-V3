using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Gallery.Places;
using PhotoViewerV3.Places.Data;
using PhotoViewerV3.Places.Dialogs;
using PhotoViewerV3.Places.Map.Tiles;
using PhotoViewerV3.Places.Picker;
using PhotoViewerV3.Places.Styles;
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

        // Kartenkacheln (ADR-0051/0055): umschaltbarer Stil (OSM, CARTO, MapTiler) mit Platten-Cache je Stil
        // (<CacheRoot>\maptiles\<stil>, zusammen 500 MB LRU, CARTO/MapTiler höchstens 30 Tage) — abgeschaltet mit
        // PHOTOVIEWERV3_MAP_TILES=off, in Tests und im Rauchtest (dann wird der Stil aufgelöst, aber nie abgerufen)
        services.TryAddSingleton(_ => MapTileOptions.FromEnvironment());
        services.TryAddSingleton<IMapStyleService, MapStyleService>();
        services.TryAddSingleton<IMapThemeSource, WpfUiMapThemeSource>();
        services.TryAddSingleton<IMapServiceAccountStore, MapServiceAccountStore>(); // Kartendienst-Angaben (Website, E-Mail, Konto)
        services.TryAddSingleton<IMapTileUsageCounter, MapTileUsageCounter>(); // Lumoras Zählung je Dienst und Monat
        services.TryAddSingleton<IMapLinkOpener, ShellMapLinkOpener>();
        services.TryAddSingleton<IMapKeyValidator>(sp => new MapKeyValidator(
            sp.GetRequiredService<MapTileOptions>(),
            OsmTileSource.BuildUserAgent(AppInfo.Version),
            logger: sp.GetRequiredService<ILoggerFactory>().CreateLogger("PhotoViewerV3.Places.Tiles"),
            usage: sp.GetRequiredService<IMapTileUsageCounter>()));
        services.TryAddSingleton<IMapTileSource>(sp =>
        {
            var options = sp.GetRequiredService<MapTileOptions>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("PhotoViewerV3.Places.Tiles");
            var cacheRoot = sp.GetRequiredService<ICatalogStoragePaths>().CacheRoot;
            if (options.Enabled)
            {
                // Bedingungen von CARTO/MapTiler: auch Kacheln gerade nicht benutzter Stile nach 30 Tagen löschen
                // dazu einmalig die OSM-Kacheln aus dem Aufbau bis 0.4.0 (maptiles\z\…) in ihren Stil-Ordner übernehmen
                _ = Task.Run(() =>
                {
                    try
                    {
                        MapTileDiskCache.MigrateLegacyLayout(cacheRoot, MapStyleCatalog.OpenStreetMap.Id, logger);
                        MapTileDiskCache.SweepExpired(cacheRoot, MapStyleCatalog.All, DateTime.UtcNow, logger);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Kartenkachel-Cache konnte nicht aufgeräumt werden");
                    }
                });
            }

            var userAgent = OsmTileSource.BuildUserAgent(AppInfo.Version);
            var usage = sp.GetRequiredService<IMapTileUsageCounter>();
            return new StyledMapTileSource(
                sp.GetRequiredService<IMapStyleService>(),
                sp.GetRequiredService<IMapThemeSource>(),
                options,
                resolution => new HttpMapTileSource(
                    resolution.Definition,
                    resolution.ApiKey,
                    options,
                    new MapTileDiskCache(cacheRoot, options.DiskCacheBytes, logger, styleFolder: resolution.Definition.Id, maxAge: resolution.Definition.MaxCacheAge),
                    userAgent,
                    logger: logger,
                    onDownload: () => usage.Count(resolution.Definition.Vendor)),
                logger);
        });
        services.TryAddSingleton<MapTileProvider>();
        services.TryAddSingleton<MapStyleSettingsViewModel>(); // Einstellungen › Darstellung › Karte

        services.TryAddSingleton<IPlacesCatalogReader, PlacesCatalogReader>();
        services.TryAddSingleton<IPlacesImageSource, PlacesThumbnailProvider>();
        services.TryAddSingleton<IPlacesDialogs, PlacesDialogs>();
        services.TryAddSingleton<IMapPicker, MapPickerService>();
        services.TryAddSingleton<PlacesViewModel>();
        services.TryAddSingleton<PlacesPage>();
        return services;
    }
}
