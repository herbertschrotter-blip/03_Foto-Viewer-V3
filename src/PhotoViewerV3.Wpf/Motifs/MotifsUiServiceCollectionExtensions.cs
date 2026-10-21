using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PhotoViewerV3.Faces;
using PhotoViewerV3.Services.Faces;

namespace PhotoViewerV3.Motifs;

/// <summary>
/// Wpf-Seite der Motiv-Erkennung (ADR-0052): Einstellungs-Reiter „Motive“. Den WIC-Bild-Lader teilt sie mit der
/// Gesichtserkennung (<see cref="IFaceImageLoader"/>); die Dienste registriert <c>AddPhotoViewerServices</c>
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
