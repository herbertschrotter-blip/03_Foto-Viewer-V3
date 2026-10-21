using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Lumora.Lightbox;

namespace Lumora.Slideshow;

/// <summary>
/// DI-Registrierung der Diashow (ADR-0059; eine Zeile in <c>App.ConfigureServices</c>: <c>services.AddSlideshow();</c>).
/// Nutzt den <see cref="LibVlcHost"/> der Lightbox mit. ViewModel, Video-Player und Fenster erzeugt der
/// <see cref="SlideshowService"/> je Diashow selbst (das Fenster gibt sie beim Schließen frei).
/// </summary>
public static class SlideshowServiceCollectionExtensions
{
    public static IServiceCollection AddSlideshow(this IServiceCollection services)
    {
        services.TryAddSingleton<LibVlcHost>();
        services.TryAddSingleton<ISlideshowItemLoader, SlideshowItemLoader>();
        services.TryAddSingleton<ISlideshowImageDecoder, SlideshowImageDecoder>();
        services.TryAddSingleton<ISlideshowSettingsStore, SlideshowSettingsStore>();
        services.TryAddSingleton<SlideshowService>();
        services.TryAddSingleton<ISlideshowService>(sp => sp.GetRequiredService<SlideshowService>());
        services.TryAddSingleton<SlideshowSettingsViewModel>();
        return services;
    }
}
