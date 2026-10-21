using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Lumora.Services.Infrastructure;
using Lumora.Services.Motifs.Models;
using Lumora.Services.Motifs.Siglip;

namespace Lumora.Services.Motifs;

/// <summary>
/// Motiv-Erkennung (ADR-0052): Modellverwaltung, SigLIP-Encoder, Einstellungen, Begriffe, Hintergrund-Index und die
/// gemeinsame GPU-Sperre mit der Gesichtserkennung. Den Bild-Lader (<see cref="Faces.IFaceImageLoader"/>, WIC)
/// registriert das Wpf-Projekt.
/// </summary>
public static class MotifsServiceCollectionExtensions
{
    public static IServiceCollection AddMotifs(this IServiceCollection services)
    {
        services.TryAddSingleton<IGpuGate, GpuGate>();
        services.TryAddSingleton<IMotifModelProvider, MotifModelProvider>();
        services.TryAddSingleton<IMotifEncoderFactory, OnnxSiglipEncoderFactory>();
        services.TryAddSingleton<IMotifSettingsStore, MotifSettingsStore>();
        services.TryAddSingleton<MotifStore>();
        services.TryAddSingleton<MotifTermService>();
        services.TryAddSingleton<IMotifTermService>(sp => sp.GetRequiredService<MotifTermService>());
        services.TryAddSingleton<MotifIndexService>();
        services.TryAddSingleton<IMotifIndexService>(sp => sp.GetRequiredService<MotifIndexService>());
        return services;
    }
}
