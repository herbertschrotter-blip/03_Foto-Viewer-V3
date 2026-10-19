using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PhotoViewerV3.Services.Tags;

/// <summary>DI-Registrierung des Tag-Moduls (ADR-0021). Wird in <c>AddPhotoViewerServices</c> mit einer Zeile aufgerufen.</summary>
public static class TagsServiceCollectionExtensions
{
    public static IServiceCollection AddTags(this IServiceCollection services)
    {
        services.TryAddSingleton<ITagService, TagService>();
        return services;
    }
}
