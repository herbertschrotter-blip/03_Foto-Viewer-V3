using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Lumora.Services.Faces.Indexing;
using Lumora.Services.Faces.Models;
using Lumora.Services.Faces.Onnx;

namespace Lumora.Services.Faces;

/// <summary>
/// Gesichtserkennung Teil 1 (ADR-0035): Modellverwaltung, ONNX-Analysator, Einstellungen, Hintergrund-Index. Den
/// Bild-Lader (<see cref="IFaceImageLoader"/>, WIC) registriert das Wpf-Projekt.
/// </summary>
public static class FacesServiceCollectionExtensions
{
    public static IServiceCollection AddFaces(this IServiceCollection services)
    {
        services.TryAddSingleton<IFaceModelProvider, FaceModelProvider>();
        services.TryAddSingleton<IFaceAnalyzerFactory, OnnxFaceAnalyzerFactory>();
        services.TryAddSingleton<IFaceIndexSettingsStore, FaceIndexSettingsStore>();
        services.TryAddSingleton<FaceIndexService>();
        services.TryAddSingleton<IFaceIndexService>(sp => sp.GetRequiredService<FaceIndexService>());
        return services;
    }
}
