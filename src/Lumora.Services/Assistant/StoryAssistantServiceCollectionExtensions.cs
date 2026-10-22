using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Lumora.Services.HiddenLibraries.Containers;

namespace Lumora.Services.Assistant;

/// <summary>
/// DI des Story-Assistenten (ADR-0063): Ollama-Client (nur 127.0.0.1:11434), Sprach-KI, Einstellungen, Verlauf/Aliase,
/// Abfragen, Auflösung, Collage-Dienst, winget-Installer und Registry-Größe. Den Collage-Renderer (WIC/WPF) registriert das
/// Wpf-Projekt. Eine Zeile in <c>AddLumoraServices</c>. Jede Katalog-Sitzung hat ihren eigenen Container — Verlauf und
/// gemerkte Antworten einer versteckten Bibliothek liegen so nur in deren Katalog.
/// </summary>
public static class StoryAssistantServiceCollectionExtensions
{
    public static IServiceCollection AddStoryAssistant(this IServiceCollection services)
    {
        services.TryAddSingleton(_ => new OllamaClient(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }));
        services.TryAddSingleton<IAssistantSettingsStore, AssistantSettingsStore>();
        services.TryAddSingleton<IOllamaLocator, OllamaLocator>();
        services.TryAddSingleton<OllamaIntentModel>();
        services.TryAddSingleton<IIntentModel>(sp => sp.GetRequiredService<OllamaIntentModel>());
        services.TryAddSingleton<IAssistantRuntime>(sp => sp.GetRequiredService<OllamaIntentModel>());
        services.TryAddSingleton<IAssistantStore, AssistantStore>();
        services.TryAddSingleton<AssistantQueries>();
        services.TryAddSingleton<StoryIntentResolver>();
        services.TryAddSingleton<IStoryAssistant, StoryAssistantService>();
        services.TryAddSingleton<ICollageService, CollageService>();
        services.TryAddSingleton<IProcessOutputRunner, ProcessOutputRunner>();
        services.TryAddSingleton<IOllamaInstaller>(sp => new WingetOllamaInstaller(sp.GetRequiredService<IProcessOutputRunner>()));
        services.TryAddSingleton<IModelManifestProbe>(_ => new OllamaRegistryProbe(new HttpClient()));
        return services;
    }
}
