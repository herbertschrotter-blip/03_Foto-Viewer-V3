using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PhotoViewerV3.Services.Faces.Erase;

/// <summary>Registrierung von „Gesichtsdaten löschen“ (eine Zeile in <c>AddPhotoViewerServices</c>).</summary>
public static class FaceDataServiceCollectionExtensions
{
    public static IServiceCollection AddFaceDataEraser(this IServiceCollection services)
    {
        services.TryAddSingleton<IFaceDataEraser, FaceDataEraser>();
        return services;
    }
}
