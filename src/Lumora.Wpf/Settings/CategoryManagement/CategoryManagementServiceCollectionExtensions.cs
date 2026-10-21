using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.Settings.CategoryManagement;

/// <summary>DI-Registrierung der Seite „Kategorien“ (ADR-0027); eine Zeile in <c>App.ConfigureServices</c>: <c>services.AddCategoryManagement();</c>.</summary>
public static class CategoryManagementServiceCollectionExtensions
{
    public static IServiceCollection AddCategoryManagement(this IServiceCollection services)
    {
        services.TryAddSingleton<CategoryManagementViewModel>();
        services.TryAddSingleton<CategoryManagementPage>();
        return services;
    }
}
