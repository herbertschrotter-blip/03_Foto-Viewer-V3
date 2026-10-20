using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PhotoViewerV3.Services.Categories;

/// <summary>DI-Registrierung des Kategorien-Moduls (ADR-0027). Wird in <c>AddPhotoViewerServices</c> mit einer Zeile aufgerufen.</summary>
public static class CategoriesServiceCollectionExtensions
{
    public static IServiceCollection AddCategories(this IServiceCollection services)
    {
        services.TryAddSingleton<ICategoryService, CategoryService>();
        return services;
    }
}
