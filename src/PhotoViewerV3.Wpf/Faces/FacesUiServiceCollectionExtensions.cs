using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PhotoViewerV3.Services.Faces;

namespace PhotoViewerV3.Faces;

/// <summary>
/// Wpf-Seite der Gesichtserkennung Teil 1 (ADR-0035): WIC-Bild-Lader für <see cref="IFaceImageLoader"/> und der
/// Einstellungs-Abschnitt. Die Dienste selbst registriert <c>AddPhotoViewerServices</c> (<c>AddFaces</c>).
/// </summary>
public static class FacesUiServiceCollectionExtensions
{
    public static IServiceCollection AddFacesUi(this IServiceCollection services)
    {
        services.TryAddSingleton<IFaceImageLoader, WicFaceImageLoader>();
        services.TryAddSingleton<FaceSettingsViewModel>();
        return services;
    }
}
