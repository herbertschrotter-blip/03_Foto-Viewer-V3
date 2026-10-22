using Lumora.Domain.Enums;
using Lumora.Services.Albums;
using Lumora.Services.Assistant;
using Lumora.Shell;
using Lumora.Stories.Assistant;

namespace Lumora.Wpf.Tests.Stories.Assistant;

/// <summary>Assistent-Attrappe: Verständnis und Treffer vorgegeben, merkt Aufrufe.</summary>
internal sealed class FakeAssistant : IStoryAssistant
{
    public List<string> Calls { get; } = [];

    public List<AssistantPlan> Evaluated { get; } = [];

    public AssistantSettings Settings { get; set; } = AssistantSettings.Default;

    public Func<string, AssistantUnderstanding> Understand { get; set; } = s => Understood(s, new AssistantPlan());

    public Func<AssistantPlan, AssistantResult> Evaluate { get; set; } = _ => Result(1, 2, 3);

    public List<AssistantHistoryItem> History { get; } = [];

    public List<AssistantAliasInfo> Aliases { get; } = [];

    public AssistantResolution? AfterAnswer { get; set; }

    public static AssistantUnderstanding Understood(string sentence, AssistantPlan plan, params AssistantQuestion[] questions)
        => new(sentence, new StoryIntent(), new AssistantResolution(plan, questions), FromRules: false);

    public static AssistantResult Result(params int[] ids) => new(ids, ids, ids.Take(8).ToList(), ids.Length, 0, 0) { Years = (2023, 2025) };

    /// <summary><paramref name="count"/> Treffer, davon die ersten <paramref name="best"/> gewählt (null = alle).</summary>
    public static AssistantResult ResultWith(int count, int? best)
    {
        var ids = Enumerable.Range(1, count).ToList();
        var selected = best is { } b && b < count ? ids.Take(b).ToList() : ids;
        return new AssistantResult(ids, selected, selected.Take(8).ToList(), count, 0, 0) { Years = (2023, 2025) };
    }

    public static AssistantPerson Person(int id, string name, string? relation = null) => new(id, name, name, string.Empty, null, relation, null, false, 10, FaceId: id * 10, FaceLibraryId: 1);

    public Task<AssistantSettings> GetSettingsAsync(CancellationToken ct = default) => Task.FromResult(Settings);

    public Task SaveSettingsAsync(AssistantSettings settings, CancellationToken ct = default)
    {
        Settings = settings;
        Calls.Add($"settings:{settings.Enabled}:{settings.UseGpu}:{settings.RememberHistory}:{settings.Model}");
        return Task.CompletedTask;
    }

    public Task<AssistantUnderstanding> UnderstandAsync(string sentence, CancellationToken ct = default)
    {
        Calls.Add("understand:" + sentence);
        return Task.FromResult(Understand(sentence));
    }

    public Task<AssistantUnderstanding> ResolveAsync(string sentence, StoryIntent intent, AssistantAnswers answers, bool fromRules = false, CancellationToken ct = default)
    {
        Calls.Add("resolve:" + sentence);
        return Task.FromResult(new AssistantUnderstanding(sentence, intent, AfterAnswer ?? new AssistantResolution(new AssistantPlan(), []), fromRules));
    }

    public Task AnswerAsync(AssistantQuestion question, QuestionChoice choice, AssistantAnswers answers, CancellationToken ct = default)
    {
        Calls.Add($"answer:{question.Key}:{choice.Label}");
        return Task.CompletedTask;
    }

    public Task<AssistantResult> EvaluateAsync(AssistantPlan plan, CancellationToken ct = default)
    {
        Evaluated.Add(plan);
        return Task.FromResult(Evaluate(plan));
    }

    public int? SuggestBest(int count) => count <= 24 ? null : 50;

    public string BuildTitle(AssistantPlan plan, AssistantResult result)
        => AssistantTitleBuilder.Build(plan, result.Years);

    public Task<int> CreateStoryAsync(AssistantPlan plan, AssistantResult result, CancellationToken ct = default)
    {
        Calls.Add("story:" + string.Join(",", result.Selected));
        return Task.FromResult(77);
    }

    public Task<AlbumInfo> CreateAlbumAsync(AssistantPlan plan, AssistantResult result, CancellationToken ct = default)
    {
        Calls.Add("album:" + string.Join(",", result.Selected));
        return Task.FromResult(new AlbumInfo(5, BuildTitle(plan, result), AlbumKind.Manual, result.Selected.Count, null, null, DateTime.UtcNow, DateTime.UtcNow));
    }

    public Task<IReadOnlyList<AssistantHistoryItem>> GetHistoryAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AssistantHistoryItem>>(History.ToList());

    public Task RememberAsync(string sentence, StoryIntent intent, IntentAction action, int resultCount, CancellationToken ct = default)
    {
        Calls.Add($"remember:{sentence}:{resultCount}");
        return Task.CompletedTask;
    }

    public Task DeleteHistoryAsync(int id, CancellationToken ct = default)
    {
        History.RemoveAll(h => h.Id == id);
        Calls.Add("delete-history:" + id);
        return Task.CompletedTask;
    }

    public Task<int> ClearHistoryAsync(CancellationToken ct = default)
    {
        var count = History.Count;
        History.Clear();
        Calls.Add("clear-history");
        return Task.FromResult(count);
    }

    public Task<IReadOnlyList<AssistantAliasInfo>> GetAliasesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AssistantAliasInfo>>(Aliases.ToList());

    public Task DeleteAliasAsync(string term, CancellationToken ct = default)
    {
        Aliases.RemoveAll(a => a.Term == term);
        Calls.Add("delete-alias:" + term);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AssistantPerson>> GetPersonsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AssistantPerson>>([Person(1, "Nicole"), Person(2, "Nina")]);

    public Task<PlanTerm> MatchTermAsync(string text, CancellationToken ct = default)
        => Task.FromResult(new PlanTerm(text, [new TermMatch(TermMatchKind.Tag, text, [9], 4)]));
}

/// <summary>Dialoge mit Antwort und gemerkten Texten (Bestätigung zeigt Name/Quelle/Größe/Lizenz).</summary>
internal sealed class RecordingDialogs : IDialogService
{
    public bool ConfirmAnswer { get; set; }

    public List<string> Messages { get; } = [];

    public string? PromptAnswer { get; set; }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false, string cancelText = "Abbrechen")
    {
        Messages.Add(title + "\n" + message);
        return Task.FromResult(ConfirmAnswer);
    }

    public Task<DialogChoice> ChooseAsync(string title, string message, string primaryText, string secondaryText, string closeText = "Abbrechen", bool secondaryIsDestructive = false)
        => Task.FromResult(DialogChoice.None);

    public Task<string?> PromptTextAsync(string title, string message, string initialValue, string confirmText = "OK") => Task.FromResult(PromptAnswer);

    public Task ShowErrorAsync(string title, string message)
    {
        Messages.Add(message);
        return Task.CompletedTask;
    }

    public string? PickFolder(string title) => null;
}

internal sealed class FakeRuntime : IAssistantRuntime
{
    public AssistantRuntimeStatus Status { get; set; } = new(AssistantRuntimeState.ModelMissing, AssistantModels.DefaultModel, "0.12");

    public List<string> Pulls { get; } = [];

    public Task<AssistantRuntimeStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(Status);

    public Task PullModelAsync(string model, IProgress<OllamaPullProgress>? progress, CancellationToken ct)
    {
        Pulls.Add(model);
        progress?.Report(new OllamaPullProgress("downloading", 50, 100));
        Status = Status with { State = AssistantRuntimeState.Ready };
        return Task.CompletedTask;
    }

    public bool TryStartOllama() => true;
}

internal sealed class FakeInstaller : IOllamaInstaller
{
    public int Installs { get; private set; }

    public bool IsWingetAvailable { get; set; } = true;

    public Task<OllamaInstallResult> InstallAsync(IProgress<string>? progress, CancellationToken ct)
    {
        Installs++;
        return Task.FromResult(new OllamaInstallResult(true, "Ollama ist installiert."));
    }
}

internal sealed class FakeProbe : IModelManifestProbe
{
    public List<string> Asked { get; } = [];

    public Task<long?> GetSizeAsync(string model, CancellationToken ct)
    {
        Asked.Add(model);
        return Task.FromResult<long?>(1_929_000_000);
    }
}

internal sealed class FakeCollageDialogs : ICollageDialogService
{
    public List<CollageRequest> Requests { get; } = [];

    public Task<CollageSaveResult?> ShowAsync(CollageRequest request)
    {
        Requests.Add(request);
        return Task.FromResult<CollageSaveResult?>(new CollageSaveResult(@"C:\x\Collage.jpg", 1920, 1080, request.CandidateIds.Count));
    }
}

/// <summary>Collage-Dienst-Attrappe: Fotos 1:1 aus den Ids, merkt Speichern.</summary>
internal sealed class FakeCollageService : ICollageService
{
    public bool Allowed { get; set; } = true;

    public List<(CollageOptions Options, IReadOnlyList<CollagePhoto> Photos, string? Title, string Path)> Saves { get; } = [];

    public Task<IReadOnlyList<CollagePhoto>> LoadPhotosAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CollagePhoto>>(fileIds.Select(id => new CollagePhoto(id, $@"C:\fake\{id}.jpg", id % 3 == 0 ? 0.75 : 1.5, new DateTime(2024, 1, 1).AddDays(id))).ToList());

    public Task<IReadOnlyList<int>> RankAsync(IReadOnlyList<int> fileIds, int pool, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<int>>(fileIds.Take(pool).ToList());

    public Task<string> GetDefaultFolderAsync(CancellationToken ct = default) => Task.FromResult(@"C:\Bilder\Lumora\Collagen");

    public Task<bool> IsAllowedTargetAsync(string path, CancellationToken ct = default) => Task.FromResult(Allowed);

    public Task<CollageSaveResult> SaveAsync(CollageOptions options, IReadOnlyList<CollagePhoto> photos, string? title, string path, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        Saves.Add((options, photos, title, path));
        var (w, h, _) = CollageLayout.Dimensions(options.Format, options.Size);
        return Task.FromResult(new CollageSaveResult(path, w, h, photos.Count));
    }
}

internal sealed class FakeFilePicker : ICollageFilePicker
{
    public string? Path { get; set; }

    public string? PickSavePath(string initialFolder, string fileName) => Path;
}
