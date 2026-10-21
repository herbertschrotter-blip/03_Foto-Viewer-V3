using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.Services.Quality;

/// <summary>
/// Foto-Güte und Highlights (ADR-0057): Hintergrund-Index (Schärfe, Belichtung, Bildwirkung), Text-Vektoren der
/// Bildwirkung und die Highlight-Auswahl für Storys. Eine Zeile in <c>AddLumoraServices</c>; den Bild-Lader
/// (<c>IFaceImageLoader</c>, WIC) registriert das Wpf-Projekt, den Start (<see cref="IFileQualityIndexService.Start"/>)
/// übernimmt <c>App.xaml.cs</c>. <see cref="IHighlightSelector"/> wird per <c>Replace</c> registriert — die Storys legen
/// eine einfache Ersatz-Implementierung per <c>TryAdd</c> an, diese hier gewinnt in jeder Reihenfolge.
/// </summary>
public static class QualityServiceCollectionExtensions
{
    public static IServiceCollection AddQuality(this IServiceCollection services)
    {
        services.TryAddSingleton<IQualityAesthetics, QualityAesthetics>();
        services.TryAddSingleton<IFileQualityIndexService, FileQualityIndexService>();
        services.Replace(ServiceDescriptor.Singleton<IHighlightSelector, QualityHighlightSelector>());
        return services;
    }
}
