using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.Services.Archive;

/// <summary>
/// DI-Registrierung des Archiv-Moduls (Services/Archive, ADR-0022). Wird in <c>AddLumoraServices</c> mit
/// einer Zeile aufgerufen. TryAdd, damit Tests oder der Rauchtest eigene Implementierungen vorab setzen können.
/// </summary>
public static class ArchiveServiceCollectionExtensions
{
    public static IServiceCollection AddArchives(this IServiceCollection services)
    {
        services.TryAddSingleton<IArchiveService, ArchiveService>();

        // Automatisches Entpacken neu gefundener Archive in Eingangsordnern (ADR-0032, Standard aus); Start in App.xaml.cs
        services.TryAddSingleton<IArchiveAutoExtractSettingsStore, ArchiveAutoExtractSettingsStore>();
        services.TryAddSingleton<IArchiveAutoExtractService, ArchiveAutoExtractService>();
        return services;
    }
}
