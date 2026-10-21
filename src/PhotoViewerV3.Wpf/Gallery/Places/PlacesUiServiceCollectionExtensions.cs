using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PhotoViewerV3.Gallery.Places;

/// <summary>
/// DI der Oberfläche zu Orten (ADR-0050): Dialog „Ort setzen“ (per TryAdd, damit der Rauchtest seinen Fake vorher setzen
/// kann) und <see cref="PlaceActions"/> für die Galerie. Die Kartenauswahl (<see cref="IMapPicker"/>) registriert die Karte
/// selbst. Aufruf in App.xaml.cs.
/// </summary>
public static class PlacesUiServiceCollectionExtensions
{
    public static IServiceCollection AddPlacesUi(this IServiceCollection services)
    {
        services.TryAddSingleton<IPlaceDialogService, PlaceDialogService>();
        services.TryAddSingleton<PlaceActions>();
        return services;
    }
}
