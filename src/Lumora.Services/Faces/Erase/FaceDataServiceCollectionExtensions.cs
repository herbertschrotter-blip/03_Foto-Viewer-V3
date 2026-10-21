using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.Services.Faces.Erase;

/// <summary>Registrierung von „Gesichtsdaten löschen“ (eine Zeile in <c>AddLumoraServices</c>).</summary>
public static class FaceDataServiceCollectionExtensions
{
    public static IServiceCollection AddFaceDataEraser(this IServiceCollection services)
    {
        services.TryAddSingleton<IFaceDataEraser, FaceDataEraser>();
        return services;
    }
}
