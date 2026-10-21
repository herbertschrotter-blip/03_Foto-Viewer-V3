using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PhotoViewerV3.Settings.PersonRelations;

/// <summary>Registrierung (eine Zeile in <c>App.ConfigureServices</c>): Abschnitt „Personen &amp; Beziehungen“ (ADR-0045).</summary>
public static class PersonRelationsSettingsServiceCollectionExtensions
{
    public static IServiceCollection AddPersonRelationsSettings(this IServiceCollection services)
    {
        services.TryAddSingleton<PersonRelationsSettingsViewModel>();
        return services;
    }
}
