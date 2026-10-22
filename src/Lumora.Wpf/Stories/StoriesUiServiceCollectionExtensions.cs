using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.Stories;

/// <summary>
/// Story-Seite in der Oberfläche (ADR-0060): Datei-Infos, Vorschaubilder, Gesichter, Galerie-Brücke, ViewModels, Seite,
/// Einstellungs-Reiter und der Zähler „n neu“ der Navigation. Den Story-Dienst registriert <c>AddLumoraServices</c>
/// (<c>AddStories</c>, ADR-0058). Eine Zeile in <c>App.ConfigureServices</c>: <c>services.AddStoriesUi();</c> — nach
/// <c>AddPersonsUi</c> und <c>AddSlideshow</c> (Gesichts-Ausschnitte, Diashow-Einstellungen).
/// </summary>
public static class StoriesUiServiceCollectionExtensions
{
    public static IServiceCollection AddStoriesUi(this IServiceCollection services)
    {
        services.TryAddSingleton<IStoryMediaLoader, StoryMediaLoader>();
        services.TryAddSingleton<StoryImageProvider>();
        services.TryAddSingleton<IStoryImageSource>(sp => sp.GetRequiredService<StoryImageProvider>());
        services.TryAddSingleton<StoryFaceSource>();
        services.TryAddSingleton<IStoryFaceSource>(sp => sp.GetRequiredService<StoryFaceSource>());
        services.TryAddSingleton<StoryGalleryBridge>();
        services.TryAddSingleton<StoryPageServices>();
        services.TryAddSingleton<StoriesViewModel>();
        services.TryAddSingleton<StoriesPage>();
        services.TryAddSingleton<StorySettingsViewModel>();
        services.TryAddSingleton<StoriesNavigationBadgeUpdater>();
        return services;
    }
}
