using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.Services.Tags;

/// <summary>DI-Registrierung des Tag-Moduls (ADR-0021). Wird in <c>AddLumoraServices</c> mit einer Zeile aufgerufen.</summary>
public static class TagsServiceCollectionExtensions
{
    public static IServiceCollection AddTags(this IServiceCollection services)
    {
        services.TryAddSingleton<ITagService, TagService>();
        return services;
    }
}
