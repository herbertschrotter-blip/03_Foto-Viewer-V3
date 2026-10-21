using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Lumora.Services.Metadata;

namespace Lumora.Services.Places;

/// <summary>
/// DI der Orte (ADR-0050): Ortsnamen offline (<see cref="ReverseGeocoder"/> als <see cref="IReverseGeocoder"/> und
/// <see cref="IPlaceLookup"/>), GPS-Leser, Lese-Abfragen (<see cref="GeoQueryService"/> für Karte und Galerie) und der
/// Dienst für Nachtragen, Ort setzen/entfernen und Rückgängig (<see cref="GeoLocationService"/>).
/// </summary>
public static class PlacesServiceCollectionExtensions
{
    public static IServiceCollection AddPlaces(this IServiceCollection services)
    {
        services.TryAddSingleton<ReverseGeocoder>();
        services.TryAddSingleton<IReverseGeocoder>(sp => sp.GetRequiredService<ReverseGeocoder>());
        services.TryAddSingleton<IPlaceLookup>(sp => sp.GetRequiredService<ReverseGeocoder>());
        services.TryAddSingleton<IGeoLocationReader, GeoLocationReader>();
        services.TryAddSingleton<GeoQueryService>();
        services.TryAddSingleton<IGeoQueryService>(sp => sp.GetRequiredService<GeoQueryService>());
        services.TryAddSingleton<IGeoCatalogReader>(sp => sp.GetRequiredService<GeoQueryService>());
        services.TryAddSingleton<GeoLocationService>();
        services.TryAddSingleton<IGeoTagService>(sp => sp.GetRequiredService<GeoLocationService>());
        services.TryAddSingleton<IGeoTagUndoService>(sp => sp.GetRequiredService<GeoLocationService>());
        services.TryAddSingleton<IGeoBackfillService>(sp => sp.GetRequiredService<GeoLocationService>());
        return services;
    }
}
