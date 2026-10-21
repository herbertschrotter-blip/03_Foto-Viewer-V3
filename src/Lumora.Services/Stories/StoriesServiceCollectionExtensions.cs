using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Lumora.Services.Quality;

namespace Lumora.Services.Stories;

/// <summary>
/// DI der Storys (ADR-0058): Erkennung im Hintergrund, Dienst für die Story-Seite, Einstellungen, Uhr/Zeitzone und die
/// einfache Highlight-Auswahl <see cref="BasicHighlightSelector"/> — die Foto-Güte (ADR-0057) ersetzt sie per
/// <c>services.Replace(...)</c> durch die echte <see cref="IHighlightSelector"/>. Eine Zeile in <c>AddLumoraServices</c>;
/// den Start (<see cref="IStoryIndexService.Start"/>) übernimmt <c>App.xaml.cs</c>.
/// </summary>
public static class StoriesServiceCollectionExtensions
{
    public static IServiceCollection AddStories(this IServiceCollection services)
    {
        services.TryAddSingleton<StoryClock>();
        services.TryAddSingleton<StoryHomeCache>();
        services.TryAddSingleton<IHighlightSelector, BasicHighlightSelector>();
        services.TryAddSingleton<IStorySettingsStore, StorySettingsStore>();
        services.TryAddSingleton<IStoryIndexService, StoryIndexService>();
        services.TryAddSingleton<IStoryService, StoryService>();
        return services;
    }
}
