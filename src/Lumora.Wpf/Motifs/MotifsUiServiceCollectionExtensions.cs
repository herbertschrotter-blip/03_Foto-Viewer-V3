using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Lumora.Faces;
using Lumora.Services.Faces;

namespace Lumora.Motifs;

/// <summary>
/// Wpf-Seite der Motiv-Erkennung (ADR-0052): Einstellungs-Reiter „Motive“. Den WIC-Bild-Lader teilt sie mit der
/// Gesichtserkennung (<see cref="IFaceImageLoader"/>); die Dienste registriert <c>AddLumoraServices</c>
/// (<c>AddMotifs</c>).
/// </summary>
public static class MotifsUiServiceCollectionExtensions
{
    public static IServiceCollection AddMotifsUi(this IServiceCollection services)
    {
        services.TryAddSingleton<IFaceImageLoader, WicFaceImageLoader>();
        services.TryAddSingleton<MotifSettingsViewModel>();
        return services;
    }
}
