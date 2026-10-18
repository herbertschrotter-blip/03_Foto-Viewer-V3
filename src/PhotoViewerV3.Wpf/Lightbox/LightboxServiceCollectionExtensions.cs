using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PhotoViewerV3.Lightbox;

/// <summary>
/// DI-Registrierung des Lightbox-Moduls (eine Zeile in <c>App.ConfigureServices</c>:
/// <c>services.AddLightbox();</c>). Der Host disposed die IDisposable-Singletons beim Beenden;
/// <see cref="LightboxService"/> hängt von <see cref="LibVlcHost"/> ab, damit der Player-Besitzer
/// vor der LibVLC-Instanz freigegeben wird.
/// </summary>
public static class LightboxServiceCollectionExtensions
{
    public static IServiceCollection AddLightbox(this IServiceCollection services)
    {
        services.TryAddSingleton<LibVlcHost>();
        services.TryAddSingleton<ILightboxService, LightboxService>();
        services.AddTransient<LightboxViewModel>();
        return services;
    }
}
