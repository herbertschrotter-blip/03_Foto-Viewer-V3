using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Lumora.Services.Faces.Crops;
using Lumora.Services.Faces.Recognition;

namespace Lumora.Services.Faces.Assignment;

/// <summary>
/// Gesichtserkennung Teil 3 (ADR-0037): Lern-/Gruppierbausteine (ADR-0036) als Singletons, Zuordnungsdienst und Cache
/// der Gesichts-Ausschnitte. Den WIC-Renderer der Ausschnitte (<see cref="IFaceCropRenderer"/>) registriert das
/// Wpf-Projekt. Eine Zeile in <c>AddLumoraServices</c>.
/// </summary>
public static class FaceRecognitionServiceCollectionExtensions
{
    public static IServiceCollection AddFaceRecognition(this IServiceCollection services)
    {
        services.TryAddSingleton<IFaceRecognizer, FaceRecognizer>();
        services.TryAddSingleton<IFaceClusterer, FaceClusterer>();
        services.TryAddSingleton<FaceRecognitionService>();
        services.TryAddSingleton<IFaceRecognitionService>(sp => sp.GetRequiredService<FaceRecognitionService>());
        services.TryAddSingleton<IFaceIgnoreService>(sp => sp.GetRequiredService<FaceRecognitionService>()); // ADR-0040
        services.TryAddSingleton<IPersonDetailsService>(sp => sp.GetRequiredService<FaceRecognitionService>()); // ADR-0045
        services.TryAddSingleton<FaceCropService>();
        services.TryAddSingleton<IFaceCropService>(sp => sp.GetRequiredService<FaceCropService>());
        return services;
    }
}
