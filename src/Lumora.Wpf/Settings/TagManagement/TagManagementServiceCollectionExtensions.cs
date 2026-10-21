using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.Settings.TagManagement;

/// <summary>DI-Registrierung der Seite „Tags“ (ADR-0021); eine Zeile in <c>App.ConfigureServices</c>: <c>services.AddTagManagement();</c>.</summary>
public static class TagManagementServiceCollectionExtensions
{
    public static IServiceCollection AddTagManagement(this IServiceCollection services)
    {
        services.TryAddSingleton<TagManagementViewModel>();
        services.TryAddSingleton<TagManagementPage>();
        return services;
    }
}
