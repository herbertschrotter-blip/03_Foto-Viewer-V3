using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.Services.Metadata;

/// <summary>DI des Aufnahmedatums (ADR-0046): Leser (EXIF/Video/Dateiname) und Katalog-Dienst samt Nachtragen.</summary>
public static class CaptureDateServiceCollectionExtensions
{
    public static IServiceCollection AddCaptureDates(this IServiceCollection services)
    {
        services.TryAddSingleton<ICaptureDateReader, CaptureDateReader>();
        services.TryAddSingleton<ICaptureDateService, CaptureDateService>();
        return services;
    }
}
