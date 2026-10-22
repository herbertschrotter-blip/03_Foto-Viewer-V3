using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Lumora.Services.Assistant;
using Lumora.Shell;

namespace Lumora.Stories.Assistant;

/// <summary>
/// Story-Assistent in der Oberfläche (ADR-0063): Collage-Renderer (WIC/WPF hinter dem Services-Vertrag), Collage-Dialog,
/// Leiste der Seite Storys, Einstellungs-Reiter. Im Rauchtest (<c>LUMORA_SMOKE_ROOT</c>) ersetzt eine Attrappe die
/// Sprach-KI und Ollama — kein Netzwerk, kein Modell. Aufruf aus <c>AddStoriesUi</c>.
/// </summary>
public static class StoryAssistantUiServiceCollectionExtensions
{
    public static IServiceCollection AddStoryAssistantUi(this IServiceCollection services)
    {
        services.TryAddSingleton<ICollageRenderer, WpfCollageRenderer>();
        services.TryAddSingleton<ICollageFilePicker, CollageFilePicker>();
        services.TryAddSingleton<ICollageDialogService, CollageDialogService>();
        services.TryAddSingleton<StoryAssistantViewModel>();
        services.TryAddSingleton<AssistantSettingsViewModel>();
        if (SmokeTestRunner.IsRequested)
        {
            services.Replace(ServiceDescriptor.Singleton<IIntentModel, RuleIntentModel>());
            services.Replace(ServiceDescriptor.Singleton<IAssistantRuntime, ReadyAssistantRuntime>());
        }

        return services;
    }
}

/// <summary>
/// Attrappe der Sprach-KI (Rauchtest): übersetzt klare Sätze mit den Regeln von <see cref="SimpleIntentParser"/> in das
/// Drahtformat — dieselbe Prüfung und Auflösung wie mit Ollama, aber ohne Netzwerk und Modell.
/// </summary>
public sealed class RuleIntentModel : IIntentModel
{
    public Task<IntentModelResponse> InterpretAsync(IntentPrompt prompt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var intent = SimpleIntentParser.TryParse(prompt);
        return Task.FromResult(new IntentModelResponse(intent is null ? "{}" : StoryIntentJson.ToJson(intent)));
    }
}

/// <summary>Rauchtest: „Ollama bereit“ ohne Ollama (kein Hinweis in der Leiste, kein Download).</summary>
public sealed class ReadyAssistantRuntime : IAssistantRuntime
{
    public Task<AssistantRuntimeStatus> GetStatusAsync(CancellationToken ct = default)
        => Task.FromResult(new AssistantRuntimeStatus(AssistantRuntimeState.Ready, "attrappe", "Rauchtest"));

    public Task PullModelAsync(string model, System.IProgress<OllamaPullProgress>? progress, CancellationToken ct) => Task.CompletedTask;

    public bool TryStartOllama() => true;
}
